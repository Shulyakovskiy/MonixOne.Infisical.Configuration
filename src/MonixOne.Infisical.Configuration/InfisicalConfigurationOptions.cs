using System.Globalization;

namespace MonixOne.Infisical.Configuration;

/// <summary>
/// Settings for loading Infisical secrets into ASP.NET Core configuration and
/// the current process environment.
/// </summary>
public sealed class InfisicalConfigurationOptions
{
    public const string ConfigurationSectionName = "Infisical";

    private TimeSpan _refreshInterval = TimeSpan.FromHours(1);
    private long? _refreshIntervalSeconds;
    private bool _refreshIntervalConfigured;
    private bool _recursive;
    private bool _recursiveConfigured;

    /// <summary>
    /// Enables loading Infisical secrets and registering background refresh.
    /// When disabled, AddInfisical does not validate Infisical settings or
    /// change application configuration.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    public string ProjectId { get; set; } = string.Empty;

    public string EnvironmentSlug { get; set; } = string.Empty;

    public string SecretPath { get; set; } = string.Empty;

    public string? Url { get; set; }

    public bool Recursive
    {
        get => _recursive;
        set
        {
            _recursive = value;
            _recursiveConfigured = true;
        }
    }

    public bool ExpandSecretReferences { get; set; } = true;

    /// <summary>
    /// Also copies each Infisical secret to EnvironmentVariableTarget.Process.
    /// </summary>
    public bool SetProcessEnvironment { get; set; } = true;

    /// <summary>
    /// Background configuration refresh interval. Defaults to one hour.
    /// </summary>
    public TimeSpan RefreshInterval
    {
        get => _refreshInterval;
        set
        {
            _refreshInterval = value;
            _refreshIntervalSeconds = null;
            _refreshIntervalConfigured = true;
        }
    }

    /// <summary>
    /// Refresh interval in seconds for binding from the <c>Infisical</c>
    /// configuration section.
    /// </summary>
    public long? RefreshIntervalSeconds
    {
        get => _refreshIntervalSeconds;
        set
        {
            _refreshIntervalSeconds = value;
            if (value is not null)
            {
                _refreshIntervalConfigured = true;
            }
        }
    }

    /// <summary>
    /// Maximum duration of a refresh, including login, HTTP retries and response reading.
    /// Defaults to 30 seconds.
    /// </summary>
    public TimeSpan RefreshTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public string ClientIdEnvironmentVariable { get; set; } = "INFISICAL_CLIENT_ID";

    public string ClientSecretEnvironmentVariable { get; set; } = "INFISICAL_CLIENT_SECRET";

    public string ProjectIdEnvironmentVariable { get; set; } = "INFISICAL_PROJECT_ID";

    public string EnvironmentSlugEnvironmentVariable { get; set; } = "INFISICAL_ENVIRONMENT";

    public string SecretPathEnvironmentVariable { get; set; } = "INFISICAL_SECRET_PATH";

    public string UrlEnvironmentVariable { get; set; } = "INFISICAL_URL";

    public string RecursiveEnvironmentVariable { get; set; } = "INFISICAL_RECURSIVE";

    public string RefreshIntervalSecondsEnvironmentVariable { get; set; } =
        "INFISICAL_REFRESH_INTERVAL_SECONDS";

    internal void ApplyEnvironmentDefaults()
    {
        ClientId = ValueOrCurrent(ClientId, ClientIdEnvironmentVariable);
        ClientSecret = ValueOrCurrent(ClientSecret, ClientSecretEnvironmentVariable);
        ProjectId = ValueOrCurrent(ProjectId, ProjectIdEnvironmentVariable);
        EnvironmentSlug = ValueOrCurrent(EnvironmentSlug, EnvironmentSlugEnvironmentVariable);
        SecretPath = ValueOrCurrent(SecretPath, SecretPathEnvironmentVariable);
        Url = ValueOrCurrentNullable(Url, UrlEnvironmentVariable);

        if (!_recursiveConfigured
            && bool.TryParse(Environment.GetEnvironmentVariable(RecursiveEnvironmentVariable), out var recursive))
        {
            _recursive = recursive;
        }

        if (!_refreshIntervalConfigured)
        {
            var refreshIntervalSeconds = Environment.GetEnvironmentVariable(
                RefreshIntervalSecondsEnvironmentVariable);

            if (!string.IsNullOrWhiteSpace(refreshIntervalSeconds))
            {
                if (!long.TryParse(
                        refreshIntervalSeconds,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var environmentSeconds)
                    || environmentSeconds <= 0
                    || environmentSeconds > (long)TimeSpan.MaxValue.TotalSeconds)
                {
                    throw new InvalidOperationException(
                        $"{RefreshIntervalSecondsEnvironmentVariable} must be a positive integer number of seconds.");
                }

                RefreshIntervalSeconds = environmentSeconds;
            }
        }

        if (RefreshIntervalSeconds is { } seconds)
        {
            if (seconds <= 0 || seconds > (long)TimeSpan.MaxValue.TotalSeconds)
            {
                throw new InvalidOperationException(
                    $"{ConfigurationSectionName}:RefreshIntervalSeconds must be a positive integer number of seconds.");
            }

            _refreshInterval = TimeSpan.FromSeconds(seconds);
        }
    }

    internal void Validate()
    {
        var missingVariables = new[]
            {
                (Name: ClientIdEnvironmentVariable, Value: ClientId),
                (Name: ClientSecretEnvironmentVariable, Value: ClientSecret),
                (Name: ProjectIdEnvironmentVariable, Value: ProjectId),
                (Name: EnvironmentSlugEnvironmentVariable, Value: EnvironmentSlug)
            }
            .Where(variable => string.IsNullOrWhiteSpace(variable.Value))
            .Select(variable => variable.Name)
            .ToArray();

        if (missingVariables.Length > 0)
        {
            throw new InvalidOperationException(
                $"Missing required Infisical settings: {string.Join(", ", missingVariables)}.");
        }

        if (string.IsNullOrWhiteSpace(SecretPath))
        {
            SecretPath = "/";
        }

        var maximumTimerPeriod = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
        if (RefreshInterval < TimeSpan.FromMilliseconds(1) || RefreshInterval > maximumTimerPeriod)
        {
            throw new InvalidOperationException(
                "Infisical refresh interval must be positive and between 1 and 4294967294 milliseconds.");
        }

        if (RefreshTimeout < TimeSpan.FromMilliseconds(1) || RefreshTimeout > maximumTimerPeriod)
        {
            throw new InvalidOperationException(
                "Infisical refresh timeout must be between 1 and 4294967294 milliseconds.");
        }
    }

    private static string ValueOrCurrent(string current, string environmentVariable) =>
        !string.IsNullOrWhiteSpace(current)
            ? current
            : Environment.GetEnvironmentVariable(environmentVariable) ?? string.Empty;

    private static string? ValueOrCurrentNullable(string? current, string environmentVariable) =>
        !string.IsNullOrWhiteSpace(current)
            ? current
            : Environment.GetEnvironmentVariable(environmentVariable);
}
