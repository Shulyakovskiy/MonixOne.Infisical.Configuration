using Microsoft.Extensions.Configuration;

namespace MonixOne.Infisical.Configuration;

/// <summary>
/// Fetches Infisical secrets and exposes them as a normal configuration source.
/// Keys containing double underscores are normalized to ':' for options binding.
/// </summary>
public sealed class InfisicalConfigurationProvider : ConfigurationProvider, IDisposable
{
    private readonly InfisicalConfigurationOptions _options;
    private readonly HttpClient _httpClient;
    private readonly InfisicalSecretsClient _client;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly Dictionary<string, EnvironmentValue> _environmentValues = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private int _disposed;

    public InfisicalConfigurationProvider(InfisicalConfigurationOptions options)
        : this(options, new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
    {
    }

    internal InfisicalConfigurationProvider(InfisicalConfigurationOptions options, HttpMessageHandler handler)
    {
        _options = options;
        _httpClient = new HttpClient(handler)
        {
            BaseAddress = CreateBaseAddress(options.Url),
            Timeout = Timeout.InfiniteTimeSpan
        };
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        _client = new InfisicalSecretsClient(_httpClient);
    }

    public override void Load() => RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();

    internal async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _disposeCancellation.Token);
        await _refreshLock.WaitAsync(refreshCancellation.Token).ConfigureAwait(false);

        try
        {
            refreshCancellation.CancelAfter(_options.RefreshTimeout);
            var secrets = await _client.FetchAsync(_options, refreshCancellation.Token).ConfigureAwait(false);
            if (secrets.Length == 0)
            {
                throw new InvalidOperationException(
                    "Infisical returned no secrets. The last successfully loaded configuration is retained.");
            }

            var configurationData = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var processEnvironmentData = new Dictionary<string, string>(_environmentValues.Comparer);
            foreach (var secret in secrets)
            {
                if (string.IsNullOrEmpty(secret.SecretKey) || secret.SecretValue is null
                    || secret.SecretKey.Contains('\0') || secret.SecretKey.Contains('=')
                    || secret.SecretValue.Contains('\0'))
                {
                    throw new InvalidOperationException("Infisical returned an invalid secret key or value.");
                }

                var configurationKey = secret.SecretKey.Replace(
                    "__", ConfigurationPath.KeyDelimiter, StringComparison.Ordinal);
                if (!configurationData.TryAdd(configurationKey, secret.SecretValue))
                {
                    throw new InvalidOperationException(
                        $"Infisical returned duplicate configuration key '{configurationKey}'. " +
                        "Use unique secret keys or disable recursive loading.");
                }

                processEnvironmentData.Add(secret.SecretKey, secret.SecretValue);
            }

            // Cancellation can no longer interrupt the synchronous publication below.
            // Validate the entire response before changing any process-level values.
            refreshCancellation.Token.ThrowIfCancellationRequested();
            if (_options.SetProcessEnvironment)
            {
                UpdateProcessEnvironment(processEnvironmentData);
            }

            Data = configurationData;
            OnReload();
        }
        catch (OperationCanceledException exception) when (
            refreshCancellation.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested && !_disposeCancellation.IsCancellationRequested)
        {
            throw new TimeoutException($"Infisical configuration refresh exceeded {_options.RefreshTimeout}.", exception);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void UpdateProcessEnvironment(Dictionary<string, string> values)
    {
        var previousValues = new Dictionary<string, string?>(_environmentValues.Comparer);
        var nextOwnership = new Dictionary<string, EnvironmentValue>(_environmentValues.Comparer);
        try
        {
            foreach (var (key, value) in values)
            {
                var previous = Environment.GetEnvironmentVariable(key);
                previousValues.Add(key, previous);
                var original = _environmentValues.TryGetValue(key, out var owned)
                    && previous == owned.AppliedValue ? owned.OriginalValue : previous;
                Environment.SetEnvironmentVariable(key, value, EnvironmentVariableTarget.Process);
                nextOwnership.Add(key, new EnvironmentValue(original, value));
            }

            foreach (var (key, owned) in _environmentValues)
            {
                if (!values.ContainsKey(key) && Environment.GetEnvironmentVariable(key) == owned.AppliedValue)
                {
                    previousValues.Add(key, owned.AppliedValue);
                    Environment.SetEnvironmentVariable(key, owned.OriginalValue, EnvironmentVariableTarget.Process);
                }
            }
        }
        catch
        {
            foreach (var (key, previous) in previousValues)
            {
                Environment.SetEnvironmentVariable(key, previous, EnvironmentVariableTarget.Process);
            }

            throw;
        }

        _environmentValues.Clear();
        foreach (var (key, owned) in nextOwnership)
        {
            _environmentValues.Add(key, owned);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _disposeCancellation.Cancel();
            _httpClient.Dispose();
            // Pending refreshes still need to release the semaphore. Its managed
            // state and the cancellation source are collected with the provider.
        }
    }

    private static Uri CreateBaseAddress(string? url)
    {
        var host = string.IsNullOrWhiteSpace(url) ? "https://app.infisical.com" : url.Trim();
        if (!host.Contains("://", StringComparison.Ordinal))
        {
            host = "https://" + host;
        }

        var uri = new Uri(host, UriKind.Absolute);
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Infisical URL must use HTTP or HTTPS.");
        }

        return uri;
    }

    private sealed record EnvironmentValue(string? OriginalValue, string AppliedValue);
}
