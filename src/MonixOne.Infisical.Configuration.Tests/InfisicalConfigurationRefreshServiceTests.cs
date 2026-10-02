using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace MonixOne.Infisical.Configuration.Tests;

public sealed class InfisicalConfigurationRefreshServiceTests
{
    [Fact]
    public async Task BackgroundRefresh_LogsEmptyResponseAndRecoversOnNextTick()
    {
        var handler = new FakeInfisicalHandler { Secrets = [] };
        var options = FakeInfisicalHandler.Options();
        options.RefreshInterval = TimeSpan.FromMilliseconds(25);
        using var provider = new InfisicalConfigurationProvider(options, handler);
        provider.Set("Application:Value", "preserved");
        var logger = new RefreshLogger();
        using var service = new InfisicalConfigurationRefreshService(provider, options, logger);

        await service.StartAsync(TestContext.Current.CancellationToken);
        var error = await logger.Error.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        error.ShouldBeOfType<InvalidOperationException>().Message.ShouldContain("no secrets");
        provider.TryGet("Application:Value", out var retained).ShouldBeTrue();
        retained.ShouldBe("preserved");
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = provider.GetReloadToken().RegisterChangeCallback(_ => changed.TrySetResult(), null);
        handler.Secrets = [("Application__Value", "recovered")];

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        provider.TryGet("Application:Value", out var updated).ShouldBeTrue();
        updated.ShouldBe("recovered");
        handler.ListCount.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task StopAsync_AbortsActiveHttpRequestWithoutLoggingShutdownAsError()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeInfisicalHandler
        {
            List = async (_, token) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return FakeInfisicalHandler.SecretsResponse();
            }
        };
        var options = FakeInfisicalHandler.Options();
        options.RefreshInterval = TimeSpan.FromMilliseconds(25);
        using var provider = new InfisicalConfigurationProvider(options, handler);
        var logger = new RefreshLogger();
        using var service = new InfisicalConfigurationRefreshService(provider, options, logger);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        await service.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        service.ExecuteTask!.IsCompletedSuccessfully.ShouldBeTrue();
        logger.Error.Task.IsCompleted.ShouldBeFalse();
        handler.ListCount.ShouldBe(1);
    }

    private sealed class RefreshLogger : ILogger<InfisicalConfigurationRefreshService>
    {
        public TaskCompletionSource<Exception> Error { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error && exception is not null) Error.TrySetResult(exception);
        }
    }
}
