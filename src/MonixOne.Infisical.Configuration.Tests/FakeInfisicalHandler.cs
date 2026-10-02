using System.Net;
using System.Net.Http.Json;

namespace MonixOne.Infisical.Configuration.Tests;

internal sealed class FakeInfisicalHandler : HttpMessageHandler
{
    private int _loginCount;
    private int _listCount;

    public int LoginCount => Volatile.Read(ref _loginCount);
    public int ListCount => Volatile.Read(ref _listCount);
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Login { get; set; }
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? List { get; set; }
    public (string Key, string Value)[] Secrets { get; set; } = [("Application__Value", "initial")];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsolutePath == "/api/v1/auth/universal-auth/login")
        {
            Interlocked.Increment(ref _loginCount);
            return Login?.Invoke(request, cancellationToken) ?? Task.FromResult(Json(new { accessToken = "test-token" }));
        }

        Interlocked.Increment(ref _listCount);
        return List?.Invoke(request, cancellationToken) ?? Task.FromResult(SecretsResponse(Secrets));
    }

    internal static HttpResponseMessage SecretsResponse(params (string Key, string Value)[] secrets) =>
        Json(new { secrets = secrets.Select(secret => new { secretKey = secret.Key, secretValue = secret.Value }).ToArray() });

    internal static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    internal static InfisicalConfigurationOptions Options(bool setEnvironment = false) => new()
    {
        ClientId = "test-client-id",
        ClientSecret = "test-client-secret",
        ProjectId = "test-project-id",
        EnvironmentSlug = "dev",
        SecretPath = "/",
        Url = "https://infisical.test/api",
        SetProcessEnvironment = setEnvironment
    };
}
