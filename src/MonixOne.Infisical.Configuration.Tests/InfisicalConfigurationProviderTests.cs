using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace MonixOne.Infisical.Configuration.Tests;

public sealed class InfisicalConfigurationProviderTests
{
    [Fact]
    public async Task RefreshAsync_Cancelled_KeepsExistingConfiguration()
    {
        // Arrange
        using var provider = new InfisicalConfigurationProvider(
            new InfisicalConfigurationOptions());
        provider.Set("Application:ExistingSetting", "preserved");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        // Act
        await Should.ThrowAsync<OperationCanceledException>(() =>
            provider.RefreshAsync(cancellation.Token));

        // Assert
        provider.TryGet("Application:ExistingSetting", out var value).ShouldBeTrue();
        value.ShouldBe("preserved");
    }

    [Fact]
    public async Task RefreshAsync_UpdatesConfigurationAndOptionsMonitor_WithNewLogin()
    {
        var handler = new FakeInfisicalHandler();
        using var provider = new InfisicalConfigurationProvider(FakeInfisicalHandler.Options(), handler);
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().Add(new ProviderSource(provider)).Build();
        var services = new ServiceCollection();
        services.Configure<ApplicationOptions>(configuration.GetSection("Application"));
        using var serviceProvider = services.BuildServiceProvider();
        var monitor = serviceProvider.GetRequiredService<IOptionsMonitor<ApplicationOptions>>();
        var initialOptions = serviceProvider.GetRequiredService<IOptions<ApplicationOptions>>().Value;
        var reloadCount = 0;
        using var subscription = monitor.OnChange(_ => reloadCount++);

        handler.Secrets = [("Application__Value", "updated")];
        await provider.RefreshAsync(TestContext.Current.CancellationToken);

        configuration["Application:Value"].ShouldBe("updated");
        monitor.CurrentValue.Value.ShouldBe("updated");
        initialOptions.Value.ShouldBe("initial");
        reloadCount.ShouldBe(1);
        handler.LoginCount.ShouldBe(2);
        handler.ListCount.ShouldBe(2);
    }

    [Theory]
    [InlineData(null, "secret")]
    [InlineData("host-value", "secret")]
    [InlineData(null, "")]
    [InlineData("host-value", "")]
    public async Task RefreshAsync_RemovedSecret_RestoresOriginalEnvironment(string? original, string secretValue)
    {
        var key = "MONIXONE_INFISICAL_TEST_" + Guid.NewGuid().ToString("N");
        try
        {
            Environment.SetEnvironmentVariable(key, original);
            var handler = new FakeInfisicalHandler { Secrets = [(key, secretValue), (key + "_KEEP", "initial")] };
            using var provider = new InfisicalConfigurationProvider(FakeInfisicalHandler.Options(true), handler);
            await provider.RefreshAsync(TestContext.Current.CancellationToken);
            Environment.GetEnvironmentVariable(key).ShouldBe(secretValue);
            handler.Secrets = [(key + "_KEEP", "updated")];

            await provider.RefreshAsync(TestContext.Current.CancellationToken);

            provider.TryGet(key, out _).ShouldBeFalse();
            Environment.GetEnvironmentVariable(key).ShouldBe(original);
        }
        finally { Environment.SetEnvironmentVariable(key, null); Environment.SetEnvironmentVariable(key + "_KEEP", null); }
    }

    [Fact]
    public async Task RefreshAsync_RemovedSecret_DoesNotEraseExternalEnvironmentChange()
    {
        var key = "MONIXONE_INFISICAL_TEST_" + Guid.NewGuid().ToString("N");
        try
        {
            var handler = new FakeInfisicalHandler { Secrets = [(key, "secret")] };
            using var provider = new InfisicalConfigurationProvider(FakeInfisicalHandler.Options(true), handler);
            await provider.RefreshAsync(TestContext.Current.CancellationToken);
            Environment.SetEnvironmentVariable(key, "external-change");
            handler.Secrets = [(key + "_NEW", "new-secret")];

            await provider.RefreshAsync(TestContext.Current.CancellationToken);

            Environment.GetEnvironmentVariable(key).ShouldBe("external-change");
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, null);
            Environment.SetEnvironmentVariable(key + "_NEW", null);
        }
    }

    [Fact]
    public async Task RefreshAsync_EmptyResponse_ThrowsAndPreservesConfigurationAndEnvironment()
    {
        var key = "MONIXONE_INFISICAL_TEST_" + Guid.NewGuid().ToString("N");
        try
        {
            var handler = new FakeInfisicalHandler { Secrets = [(key, "preserved")] };
            using var provider = new InfisicalConfigurationProvider(FakeInfisicalHandler.Options(true), handler);
            await provider.RefreshAsync(TestContext.Current.CancellationToken);
            var reload = provider.GetReloadToken();
            handler.Secrets = [];

            var exception = await Should.ThrowAsync<InvalidOperationException>(() => provider.RefreshAsync(TestContext.Current.CancellationToken));

            exception.Message.ShouldContain("no secrets");
            provider.TryGet(key, out var value).ShouldBeTrue();
            value.ShouldBe("preserved");
            Environment.GetEnvironmentVariable(key).ShouldBe("preserved");
            reload.HasChanged.ShouldBeFalse();
        }
        finally { Environment.SetEnvironmentVariable(key, null); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RefreshAsync_CancelledDuringHttp_AbortsRequestWithoutPublishing(bool duringLogin)
    {
        var handler = new FakeInfisicalHandler();
        using var provider = new InfisicalConfigurationProvider(FakeInfisicalHandler.Options(), handler);
        await provider.RefreshAsync(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aborted = false;
        async Task<HttpResponseMessage> Block(HttpRequestMessage request, CancellationToken token)
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { aborted = token.IsCancellationRequested; }
            return FakeInfisicalHandler.SecretsResponse(("Application__Value", "unexpected"));
        }
        if (duringLogin) handler.Login = Block;
        else handler.List = Block;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var refresh = provider.RefreshAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => refresh.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));

        aborted.ShouldBeTrue();
        provider.TryGet("Application:Value", out var value).ShouldBeTrue();
        value.ShouldBe("initial");
        handler.ListCount.ShouldBe(1 + (duringLogin ? 0 : 1));
    }

    [Fact]
    public async Task RefreshAsync_Timeout_AbortsRequestAndKeepsPreviousConfiguration()
    {
        var handler = new FakeInfisicalHandler();
        var options = FakeInfisicalHandler.Options();
        using var provider = new InfisicalConfigurationProvider(options, handler);
        await provider.RefreshAsync(TestContext.Current.CancellationToken);
        options.RefreshTimeout = TimeSpan.FromMilliseconds(100);
        var aborted = false;
        handler.List = async (_, token) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { aborted = token.IsCancellationRequested; }
            return FakeInfisicalHandler.SecretsResponse();
        };

        await Should.ThrowAsync<TimeoutException>(() => provider.RefreshAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));

        aborted.ShouldBeTrue();
        provider.TryGet("Application:Value", out var value).ShouldBeTrue();
        value.ShouldBe("initial");
    }

    [Fact]
    public async Task RefreshAsync_CancelledDuringRetry_DoesNotSendAnotherRequest()
    {
        var handler = new FakeInfisicalHandler();
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.List = (_, _) =>
        {
            failed.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        };
        using var provider = new InfisicalConfigurationProvider(FakeInfisicalHandler.Options(), handler);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var refresh = provider.RefreshAsync(cancellation.Token);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => refresh.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));

        handler.ListCount.ShouldBe(1);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task RefreshAsync_TransientFailure_RetriesRead(HttpStatusCode status)
    {
        var handler = new FakeInfisicalHandler();
        handler.List = (_, _) => Task.FromResult(handler.ListCount == 1
            ? new HttpResponseMessage(status)
            : FakeInfisicalHandler.SecretsResponse(("Application__Value", "recovered")));
        using var provider = new InfisicalConfigurationProvider(FakeInfisicalHandler.Options(), handler);

        await provider.RefreshAsync(TestContext.Current.CancellationToken);

        handler.LoginCount.ShouldBe(1);
        handler.ListCount.ShouldBe(2);
        provider.TryGet("Application:Value", out var value).ShouldBeTrue();
        value.ShouldBe("recovered");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RefreshAsync_Unauthorized_DoesNotRetry(bool duringLogin)
    {
        var handler = new FakeInfisicalHandler();
        Task<HttpResponseMessage> Unauthorized(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        if (duringLogin) handler.Login = Unauthorized;
        else handler.List = Unauthorized;
        using var provider = new InfisicalConfigurationProvider(FakeInfisicalHandler.Options(), handler);
        provider.Set("Application:Value", "preserved");

        await Should.ThrowAsync<HttpRequestException>(() => provider.RefreshAsync(TestContext.Current.CancellationToken));

        handler.LoginCount.ShouldBe(1);
        handler.ListCount.ShouldBe(duringLogin ? 0 : 1);
        provider.TryGet("Application:Value", out var value).ShouldBeTrue();
        value.ShouldBe("preserved");
    }

    [Fact]
    public async Task RefreshAsync_DuplicateNormalizedKeys_DoesNotChangeEnvironmentOrConfiguration()
    {
        var key = "MONIXONE_INFISICAL_TEST_" + Guid.NewGuid().ToString("N");
        try
        {
            var handler = new FakeInfisicalHandler { Secrets = [(key + "__Value", "initial")] };
            using var provider = new InfisicalConfigurationProvider(FakeInfisicalHandler.Options(true), handler);
            await provider.RefreshAsync(TestContext.Current.CancellationToken);
            handler.Secrets = [(key + "__Value", "changed"), (key + ":Value", "conflicting")];

            await Should.ThrowAsync<InvalidOperationException>(() => provider.RefreshAsync(TestContext.Current.CancellationToken));

            provider.TryGet(key + ":Value", out var value).ShouldBeTrue();
            value.ShouldBe("initial");
            Environment.GetEnvironmentVariable(key + "__Value").ShouldBe("initial");
            Environment.GetEnvironmentVariable(key + ":Value").ShouldBeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable(key + "__Value", null);
            Environment.SetEnvironmentVariable(key + ":Value", null);
        }
    }

    [Fact]
    public async Task RefreshAsync_PreservesHttpContractAndImportPrecedence()
    {
        var options = FakeInfisicalHandler.Options();
        options.SecretPath = "/folder with spaces/a&b";
        options.Recursive = true;
        var handler = new FakeInfisicalHandler();
        handler.Login = async (request, token) =>
        {
            using var json = await JsonDocument.ParseAsync(await request.Content!.ReadAsStreamAsync(token), cancellationToken: token);
            json.RootElement.GetProperty("clientId").GetString().ShouldBe(options.ClientId);
            json.RootElement.GetProperty("clientSecret").GetString().ShouldBe(options.ClientSecret);
            return FakeInfisicalHandler.Json(new { accessToken = "fresh-token" });
        };
        handler.List = (request, _) =>
        {
            request.RequestUri!.AbsolutePath.ShouldBe("/api/v3/secrets/raw");
            request.Headers.Authorization!.ToString().ShouldBe("Bearer fresh-token");
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            query["workspaceId"].ToString().ShouldBe(options.ProjectId);
            query["environment"].ToString().ShouldBe("dev");
            query["secretPath"].ToString().ShouldBe(options.SecretPath);
            query["recursive"].ToString().ShouldBe("true");
            query["include_imports"].ToString().ShouldBe("true");
            query["viewSecretValue"].ToString().ShouldBe("true");
            query["expandSecretReferences"].ToString().ShouldBe("true");
            return Task.FromResult(FakeInfisicalHandler.Json(new
            {
                secrets = new[] { new { secretKey = "Local", secretValue = "local" } },
                imports = new[] { new { secrets = new[]
                {
                    new { secretKey = "Local", secretValue = "imported-local" },
                    new { secretKey = "Imported", secretValue = "imported" }
                } } }
            }));
        };
        using var provider = new InfisicalConfigurationProvider(options, handler);

        await provider.RefreshAsync(TestContext.Current.CancellationToken);

        provider.TryGet("Local", out var local).ShouldBeTrue();
        local.ShouldBe("local");
        provider.TryGet("Imported", out var imported).ShouldBeTrue();
        imported.ShouldBe("imported");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"secrets\":[null]}")]
    [InlineData("{\"secrets\":[{\"secretKey\":\"Application__Value\"}]}")]
    [InlineData("{\"secrets\":[],\"imports\":[null]}")]
    [InlineData("{\"secrets\":[],\"imports\":[{\"secrets\":[null]}]}")]
    public async Task RefreshAsync_InvalidResponse_DoesNotPublish(string responseBody)
    {
        var handler = new FakeInfisicalHandler
        {
            List = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json")
            })
        };
        using var provider = new InfisicalConfigurationProvider(FakeInfisicalHandler.Options(), handler);
        provider.Set("Application:Value", "preserved");
        var reload = provider.GetReloadToken();

        await Should.ThrowAsync<InvalidOperationException>(() => provider.RefreshAsync(TestContext.Current.CancellationToken));

        provider.TryGet("Application:Value", out var value).ShouldBeTrue();
        value.ShouldBe("preserved");
        reload.HasChanged.ShouldBeFalse();
    }

    [Fact]
    public async Task RefreshAsync_ConcurrentCalls_AreSerialized()
    {
        var handler = new FakeInfisicalHandler();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var overlapped = false;
        handler.List = async (_, token) =>
        {
            if (Interlocked.Increment(ref active) != 1) overlapped = true;
            try
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return FakeInfisicalHandler.SecretsResponse(("Application__Value", "updated"));
            }
            finally { Interlocked.Decrement(ref active); }
        };
        using var provider = new InfisicalConfigurationProvider(FakeInfisicalHandler.Options(), handler);
        var first = provider.RefreshAsync(TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        var second = provider.RefreshAsync(TestContext.Current.CancellationToken);

        release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        overlapped.ShouldBeFalse();
        handler.LoginCount.ShouldBe(2);
        handler.ListCount.ShouldBe(2);
    }

    [Fact]
    public async Task Dispose_CancelsActiveAndQueuedRefresh_WithoutSemaphoreRace()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeInfisicalHandler
        {
            List = async (_, token) =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return FakeInfisicalHandler.SecretsResponse();
            }
        };
        using var provider = new InfisicalConfigurationProvider(FakeInfisicalHandler.Options(), handler);
        var first = provider.RefreshAsync(TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        var second = provider.RefreshAsync(TestContext.Current.CancellationToken);

        provider.Dispose();

        await Should.ThrowAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        await Should.ThrowAsync<OperationCanceledException>(() => second.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ObjectDisposedException>(() => provider.RefreshAsync(TestContext.Current.CancellationToken));
    }

    public sealed class ApplicationOptions
    {
        public string? Value { get; set; }
    }

    private sealed class ProviderSource(InfisicalConfigurationProvider provider) : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => provider;
    }
}
