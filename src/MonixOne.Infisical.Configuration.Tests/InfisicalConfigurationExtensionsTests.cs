using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace MonixOne.Infisical.Configuration.Tests;

public sealed class InfisicalConfigurationExtensionsTests
{
    private const string ClientIdVariable = "SEA_INFISICAL_TEST_CLIENT_ID";
    private const string ClientSecretVariable = "SEA_INFISICAL_TEST_CLIENT_SECRET";
    private const string ProjectIdVariable = "SEA_INFISICAL_TEST_PROJECT_ID";
    private const string EnvironmentVariable = "SEA_INFISICAL_TEST_ENVIRONMENT";
    private const string RefreshIntervalVariable = "SEA_INFISICAL_TEST_REFRESH_INTERVAL_SECONDS";
    private const string RecursiveVariable = "INFISICAL_RECURSIVE";

    [Fact]
    public void CreateOptions_ConfigurationSection_BindsInfisicalSettings()
    {
        // Arrange
        using var environment = new EnvironmentVariableScope((RecursiveVariable, null));
        var configuration = new ConfigurationManager
        {
            ["Infisical:ClientId"] = "machine-identity-client-id",
            ["Infisical:ClientSecret"] = "machine-identity-client-secret",
            ["Infisical:ProjectId"] = "project-id",
            ["Infisical:EnvironmentSlug"] = "dev",
            ["Infisical:SecretPath"] = "/",
            ["Infisical:RefreshIntervalSeconds"] = "86400",
            ["Infisical:Url"] = "http://infisical01.infra.home.arpa:8888",
            ["Infisical:Recursive"] = "false"
        };

        // Act
        var options = InfisicalConfigurationExtensions.CreateOptions(configuration);
        options.ApplyEnvironmentDefaults();
        options.Validate();

        // Assert
        options.ClientId.ShouldBe("machine-identity-client-id");
        options.ClientSecret.ShouldBe("machine-identity-client-secret");
        options.ProjectId.ShouldBe("project-id");
        options.EnvironmentSlug.ShouldBe("dev");
        options.SecretPath.ShouldBe("/");
        options.Url.ShouldBe("http://infisical01.infra.home.arpa:8888");
        options.Recursive.ShouldBeFalse();
        options.RefreshInterval.ShouldBe(TimeSpan.FromDays(1));
    }

    [Fact]
    public void AddInfisical_DisabledFromConfiguration_DoesNotValidateOrChangeConfiguration()
    {
        // Arrange
        var services = new ServiceCollection();
        var configuration = new ConfigurationManager
        {
            ["Infisical:Enabled"] = "false"
        };
        configuration["Application:ExistingSetting"] = "preserved";
        var initialSourceCount = configuration.Sources.Count;
        var initialServiceCount = services.Count;

        // Act
        var returnedServices = services.AddInfisical(configuration);

        // Assert
        returnedServices.ShouldBeSameAs(services);
        configuration.Sources.Count.ShouldBe(initialSourceCount);
        services.Count.ShouldBe(initialServiceCount);
        configuration["Application:ExistingSetting"].ShouldBe("preserved");
    }

    [Fact]
    public void AddInfisical_Disabled_DoesNotValidateOrChangeConfiguration()
    {
        // Arrange
        using var environment = new EnvironmentVariableScope(
            (ClientIdVariable, null),
            (ClientSecretVariable, null),
            (ProjectIdVariable, null),
            (EnvironmentVariable, null),
            (RefreshIntervalVariable, "0"));
        var services = new ServiceCollection();
        var configuration = new ConfigurationManager();
        configuration["Application:ExistingSetting"] = "preserved";
        var initialSourceCount = configuration.Sources.Count;
        var initialServiceCount = services.Count;

        // Act
        var returnedServices = services.AddInfisical(configuration, options =>
        {
            options.Enabled = false;
            ConfigureEnvironmentVariableNames(options);
        });

        // Assert
        returnedServices.ShouldBeSameAs(services);
        configuration.Sources.Count.ShouldBe(initialSourceCount);
        services.Count.ShouldBe(initialServiceCount);
        configuration["Application:ExistingSetting"].ShouldBe("preserved");
    }

    [Fact]
    public void AddInfisical_MissingRequiredSettings_ThrowsBeforeAddingProvider()
    {
        // Arrange
        using var environment = new EnvironmentVariableScope(
            (ClientIdVariable, null),
            (ClientSecretVariable, null),
            (ProjectIdVariable, null),
            (EnvironmentVariable, null));
        var services = new ServiceCollection();
        var configuration = new ConfigurationManager();
        var initialSourceCount = configuration.Sources.Count;

        // Act
        var exception = Should.Throw<InvalidOperationException>(() =>
            services.AddInfisical(configuration, ConfigureEnvironmentVariableNames));

        // Assert
        exception.Message.ShouldContain(ClientIdVariable);
        exception.Message.ShouldContain(ClientSecretVariable);
        exception.Message.ShouldContain(ProjectIdVariable);
        exception.Message.ShouldContain(EnvironmentVariable);
        configuration.Sources.Count.ShouldBe(initialSourceCount);
    }

    [Fact]
    public void AddInfisical_InvalidRefreshIntervalEnvironmentValue_ThrowsBeforeAddingProvider()
    {
        // Arrange
        using var environment = new EnvironmentVariableScope(
            (ClientIdVariable, "client-id"),
            (ClientSecretVariable, "client-secret"),
            (ProjectIdVariable, "project-id"),
            (EnvironmentVariable, "development"),
            (RefreshIntervalVariable, "0"));
        var services = new ServiceCollection();
        var configuration = new ConfigurationManager();
        var initialSourceCount = configuration.Sources.Count;

        // Act
        var exception = Should.Throw<InvalidOperationException>(() =>
            services.AddInfisical(configuration, ConfigureEnvironmentVariableNames));

        // Assert
        exception.Message.ShouldContain(RefreshIntervalVariable);
        configuration.Sources.Count.ShouldBe(initialSourceCount);
    }

    [Fact]
    public void AddInfisical_ZeroExplicitRefreshInterval_ThrowsBeforeAddingProvider()
    {
        // Arrange
        using var environment = new EnvironmentVariableScope((RefreshIntervalVariable, null));
        var services = new ServiceCollection();
        var configuration = new ConfigurationManager();
        var initialSourceCount = configuration.Sources.Count;

        // Act
        var exception = Should.Throw<InvalidOperationException>(() =>
            services.AddInfisical(configuration, options =>
            {
                ConfigureEnvironmentVariableNames(options);
                options.ClientId = "client-id";
                options.ClientSecret = "client-secret";
                options.ProjectId = "project-id";
                options.EnvironmentSlug = "development";
                options.RefreshInterval = TimeSpan.Zero;
            }));

        // Assert
        exception.Message.ShouldContain("refresh interval must be positive");
        configuration.Sources.Count.ShouldBe(initialSourceCount);
    }

    private static void ConfigureEnvironmentVariableNames(InfisicalConfigurationOptions options)
    {
        options.ClientIdEnvironmentVariable = ClientIdVariable;
        options.ClientSecretEnvironmentVariable = ClientSecretVariable;
        options.ProjectIdEnvironmentVariable = ProjectIdVariable;
        options.EnvironmentSlugEnvironmentVariable = EnvironmentVariable;
        options.RefreshIntervalSecondsEnvironmentVariable = RefreshIntervalVariable;
    }

    [Fact]
    public void CreateOptions_ExplicitInterval_OverridesSectionSecondsAndInvalidEnvironment()
    {
        using var environment = new EnvironmentVariableScope((RefreshIntervalVariable, "invalid"));
        var configuration = new ConfigurationManager { ["Infisical:RefreshIntervalSeconds"] = "86400" };

        var options = InfisicalConfigurationExtensions.CreateOptions(configuration, options =>
        {
            ConfigureEnvironmentVariableNames(options);
            options.RefreshInterval = TimeSpan.FromHours(1);
        });
        options.ApplyEnvironmentDefaults();

        options.RefreshInterval.ShouldBe(TimeSpan.FromHours(1));
        options.RefreshIntervalSeconds.ShouldBeNull();
    }

    [Fact]
    public void CreateOptions_ExplicitSeconds_OverrideSectionInterval()
    {
        var configuration = new ConfigurationManager { ["Infisical:RefreshInterval"] = "1.00:00:00" };

        var options = InfisicalConfigurationExtensions.CreateOptions(configuration, options =>
            options.RefreshIntervalSeconds = 3600);
        options.ApplyEnvironmentDefaults();

        options.RefreshInterval.ShouldBe(TimeSpan.FromHours(1));
    }

    [Fact]
    public void CreateOptions_SectionSeconds_TakePriorityOverEnvironment()
    {
        using var environment = new EnvironmentVariableScope((RefreshIntervalVariable, "86400"));
        var configuration = new ConfigurationManager { ["Infisical:RefreshIntervalSeconds"] = "3600" };
        var options = InfisicalConfigurationExtensions.CreateOptions(configuration, ConfigureEnvironmentVariableNames);

        options.ApplyEnvironmentDefaults();

        options.RefreshInterval.ShouldBe(TimeSpan.FromHours(1));
    }

    [Fact]
    public void CreateOptions_MissingInterval_DefaultsToOneHour_AndEnvironmentCanOverride()
    {
        using var environment = new EnvironmentVariableScope((RefreshIntervalVariable, null));
        var configuration = new ConfigurationManager();
        var defaults = InfisicalConfigurationExtensions.CreateOptions(configuration, ConfigureEnvironmentVariableNames);
        defaults.ApplyEnvironmentDefaults();
        defaults.RefreshInterval.ShouldBe(TimeSpan.FromHours(1));
        Environment.SetEnvironmentVariable(RefreshIntervalVariable, "7200");

        var configured = InfisicalConfigurationExtensions.CreateOptions(configuration, ConfigureEnvironmentVariableNames);
        configured.ApplyEnvironmentDefaults();

        configured.RefreshInterval.ShouldBe(TimeSpan.FromHours(2));
    }

    [Fact]
    public void CreateOptions_ExplicitRecursiveFalse_IsNotOverriddenByEnvironment()
    {
        using var environment = new EnvironmentVariableScope((RecursiveVariable, "true"));
        var configuration = new ConfigurationManager { ["Infisical:Recursive"] = "false" };
        var sectionOptions = InfisicalConfigurationExtensions.CreateOptions(configuration);
        sectionOptions.ApplyEnvironmentDefaults();
        sectionOptions.Recursive.ShouldBeFalse();

        var delegateOptions = InfisicalConfigurationExtensions.CreateOptions(new ConfigurationManager(),
            options => options.Recursive = false);
        delegateOptions.ApplyEnvironmentDefaults();
        delegateOptions.Recursive.ShouldBeFalse();
        var environmentOptions = InfisicalConfigurationExtensions.CreateOptions(new ConfigurationManager());
        environmentOptions.ApplyEnvironmentDefaults();
        environmentOptions.Recursive.ShouldBeTrue();
    }

    [Fact]
    public void Validate_IntervalTooLargeForPeriodicTimer_IsRejected()
    {
        var options = FakeInfisicalHandler.Options();
        options.RefreshInterval = TimeSpan.FromDays(50);

        Should.Throw<InvalidOperationException>(options.Validate).Message.ShouldContain("refresh interval");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4294967295)]
    public void Validate_InvalidTimeout_IsRejected(long milliseconds)
    {
        var options = FakeInfisicalHandler.Options();
        options.RefreshTimeout = TimeSpan.FromMilliseconds(milliseconds);

        Should.Throw<InvalidOperationException>(options.Validate).Message.ShouldContain("refresh timeout");
    }

    /// <summary>
    /// Restores process-level variables after each public configuration API test.
    /// </summary>
    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly Dictionary<string, string?> _previousValues = new(StringComparer.Ordinal);

        public EnvironmentVariableScope(params (string Name, string? Value)[] values)
        {
            foreach (var (name, value) in values)
            {
                _previousValues[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in _previousValues)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}
