using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace MonixOne.Infisical.Configuration;

// The SDK does not accept CancellationToken. Keep its HTTP contract, but pass
// cancellation through login, requests, response reading and retry delays.
internal sealed class InfisicalSecretsClient(HttpClient client)
{
    internal async Task<Secret[]> FetchAsync(
        InfisicalConfigurationOptions options,
        CancellationToken cancellationToken)
    {
        using var loginResponse = await client.PostAsJsonAsync(
            "/api/v1/auth/universal-auth/login",
            new { clientId = options.ClientId, clientSecret = options.ClientSecret },
            cancellationToken).ConfigureAwait(false);
        loginResponse.EnsureSuccessStatusCode();
        var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(login?.AccessToken))
        {
            throw new InvalidOperationException("Infisical login returned no access token.");
        }

        var url = QueryHelpers.AddQueryString("/api/v3/secrets/raw", new Dictionary<string, string?>
        {
            ["workspaceId"] = options.ProjectId,
            ["environment"] = options.EnvironmentSlug,
            ["secretPath"] = options.SecretPath,
            ["recursive"] = options.Recursive ? "true" : "false",
            ["expandSecretReferences"] = options.ExpandSecretReferences ? "true" : "false",
            ["include_imports"] = "true",
            ["viewSecretValue"] = "true"
        });

        using var response = await ListAsync(url, login.AccessToken, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<ListResponse>(cancellationToken)
            .ConfigureAwait(false);
        if (payload?.Secrets is null || payload.Secrets.Any(secret => secret is null))
        {
            throw new InvalidOperationException("Infisical returned an invalid secrets response.");
        }

        var secrets = payload.Secrets.ToList();
        // Imported values must not override local values. Preserve the SDK's
        // precedence between imports, and let the provider reject ambiguous keys.
        var keys = secrets.Select(secret => secret.SecretKey).ToHashSet(StringComparer.Ordinal);
        foreach (var import in payload.Imports ?? [])
        {
            if (import is null || import.Secrets?.Any(secret => secret is null) == true)
            {
                throw new InvalidOperationException("Infisical returned an invalid secrets import.");
            }

            foreach (var secret in import.Secrets ?? [])
            {
                if (keys.Add(secret.SecretKey))
                {
                    secrets.Add(secret);
                }
            }
        }

        return secrets.ToArray();
    }

    private async Task<HttpResponseMessage> ListAsync(
        string url,
        string accessToken,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            try
            {
                var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (attempt == 3 || !IsTransient(response.StatusCode))
                {
                    return response;
                }

                response.Dispose();
            }
            catch (HttpRequestException) when (attempt < 3)
            {
                // Only read requests are retried; credential failures return immediately.
            }

            await Task.Delay(TimeSpan.FromSeconds(1 << (attempt + 1)), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        || (int)statusCode >= 500;

    internal sealed record Secret(string SecretKey, string SecretValue);
    private sealed record LoginResponse(string AccessToken);
    private sealed record ListResponse(Secret[]? Secrets, SecretImport[]? Imports);
    private sealed record SecretImport(Secret[]? Secrets);
}
