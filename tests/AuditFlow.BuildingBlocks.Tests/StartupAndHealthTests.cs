using AuditFlow.BuildingBlocks.Eventing;
using AuditFlow.BuildingBlocks.Health;
using AuditFlow.BuildingBlocks.Hosting;
using AuditFlow.BuildingBlocks.Messaging;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AuditFlow.BuildingBlocks.Tests;

public class StartupConfigTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private static IHostEnvironment Env(string name)
    {
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(name);
        return env.Object;
    }

    [Fact]
    public void Development_falls_back_to_the_local_default()
    {
        var url = StartupConfig.RequireUrl(Config(), Env(Environments.Development), "Services:Workflow:BaseUrl", "http://localhost:5003");

        Assert.Equal(new Uri("http://localhost:5003"), url);
    }

    [Fact]
    public void Configured_value_wins_over_the_default()
    {
        var url = StartupConfig.RequireUrl(
            Config(("Services:Workflow:BaseUrl", "http://workflow-service:8080")), Env(Environments.Development),
            "Services:Workflow:BaseUrl", "http://localhost:5003");

        Assert.Equal(new Uri("http://workflow-service:8080"), url);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Outside_development_a_missing_value_stops_startup_and_names_the_key(string environment)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            StartupConfig.RequireUrl(Config(), Env(environment), "Services:Workflow:BaseUrl", "http://localhost:5003"));

        Assert.Contains("Services:Workflow:BaseUrl", ex.Message);
        Assert.Contains("Services__Workflow__BaseUrl", ex.Message);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://host/")]
    [InlineData("/relative")]
    public void A_value_that_is_not_an_http_url_is_rejected(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            StartupConfig.RequireUrl(Config(("Auth:MetadataAddress", value)), Env(Environments.Production), "Auth:MetadataAddress"));

        Assert.Contains("absolute http(s) URL", ex.Message);
    }

    [Fact]
    public void A_blank_value_counts_as_missing()
    {
        Assert.Throws<InvalidOperationException>(() =>
            StartupConfig.Require(Config(("Some:Key", "  ")), Env(Environments.Production), "Some:Key"));
    }
}

public class ServiceBusHealthCheckTests
{
    private static Task<HealthCheckResult> Check(Func<CancellationToken, Task> probe, TimeSpan? timeout = null)
        => new ServiceBusHealthCheck(probe, timeout ?? TimeSpan.FromSeconds(5)).CheckHealthAsync(new HealthCheckContext());

    [Fact]
    public async Task Healthy_when_the_probe_succeeds()
        => Assert.Equal(HealthStatus.Healthy, (await Check(_ => Task.CompletedTask)).Status);

    [Fact]
    public async Task Unhealthy_when_the_probe_throws()
    {
        var result = await Check(_ => throw new InvalidOperationException("broker down"));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.NotNull(result.Exception);
    }

    [Fact]
    public async Task Unhealthy_when_the_probe_hangs_past_the_timeout()
    {
        var result = await Check(ct => Task.Delay(Timeout.Infinite, ct), TimeSpan.FromMilliseconds(50));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }
}

public class SubscriberStartupTests
{
    private const string FakeConnection = "Endpoint=sb://example.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=dGVzdA==";

    private static ServiceBusSubscriberHost Host(string subscription, bool withHandlers)
    {
        var registrations = withHandlers
            ? new[] { new EventHandlerRegistration("Ping", (_, _, _, _) => Task.CompletedTask) }
            : [];
        return new ServiceBusSubscriberHost(
            new ServiceBusClient(FakeConnection),
            Options.Create(new ServiceBusOptions { SubscriptionName = subscription }),
            new EventHandlerRegistry(registrations),
            Mock.Of<IEventDispatcher>(),
            NullLogger<ServiceBusSubscriberHost>.Instance);
    }

    [Fact]
    public async Task Handlers_without_a_subscription_stop_startup()
    {
        var host = Host(subscription: "", withHandlers: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(CancellationToken.None));

        Assert.Contains("ServiceBus:SubscriptionName", ex.Message);
    }

    [Fact]
    public async Task A_service_with_no_handlers_stays_idle_without_a_subscription()
    {
        var host = Host(subscription: "", withHandlers: false);

        await host.StartAsync(CancellationToken.None);
        await host.StopAsync(CancellationToken.None);
    }
}
