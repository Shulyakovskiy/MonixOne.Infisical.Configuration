using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace MonixOne.Infisical.Configuration.Tests;

public sealed class InfisicalHttpCancellationTests
{
    [Fact]
    public async Task HostedService_Stop_AbortsRealHttpRequest_AndPreservesConfiguration()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var api = builder.Build();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lists = 0;
        api.MapPost("/api/v1/auth/universal-auth/login", () => Results.Json(new { accessToken = "test-token" }));
        api.MapGet("/api/v3/secrets/raw", async (HttpContext context) =>
        {
            if (Interlocked.Increment(ref lists) == 1)
            {
                await context.Response.WriteAsJsonAsync(new
                {
                    secrets = new[] { new { secretKey = "Application__Value", secretValue = "preserved" } }
                }, cancellationToken: context.RequestAborted);
                return;
            }

            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { aborted.TrySetResult(); }
        });
        await api.StartAsync(TestContext.Current.CancellationToken);
        using var configuration = new ConfigurationManager();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfisical(configuration, options =>
        {
            options.ClientId = "test-client-id";
            options.ClientSecret = "test-client-secret";
            options.ProjectId = "test-project-id";
            options.EnvironmentSlug = "dev";
            options.Url = api.Urls.Single();
            options.SetProcessEnvironment = false;
            options.RefreshInterval = TimeSpan.FromMilliseconds(25);
        });
        using var serviceProvider = services.BuildServiceProvider();
        var refreshService = serviceProvider.GetServices<IHostedService>().Single();
        await refreshService.StartAsync(TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        await refreshService.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        await aborted.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        configuration["Application:Value"].ShouldBe("preserved");
        Volatile.Read(ref lists).ShouldBe(2);
        await api.StopAsync(TestContext.Current.CancellationToken);
    }
}
