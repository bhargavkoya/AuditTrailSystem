using System.Reflection;
using AuditFlow.BuildingBlocks.Correlation;
using AuditFlow.BuildingBlocks.Data;
using AuditFlow.BuildingBlocks.Errors;
using AuditFlow.BuildingBlocks.Eventing;
using AuditFlow.BuildingBlocks.Health;
using AuditFlow.BuildingBlocks.Messaging;
using AuditFlow.BuildingBlocks.Security;
using AuditFlow.BuildingBlocks.Telemetry;
using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace AuditFlow.BuildingBlocks.Hosting;

/// <summary>Shared wiring for every service: correlation, telemetry, health, Swagger, errors, data, messaging, event bus.</summary>
public static class ServiceDefaultsExtensions
{
    public static WebApplicationBuilder AddAuditFlowDefaults(this WebApplicationBuilder builder, string serviceName)
    {
        builder.Services.AddSingleton(new ServiceInfo(serviceName));
        builder.Services.AddSingleton<ICorrelationContext, CorrelationContext>();
        builder.Services.AddTransient<CorrelationIdHandler>();
        builder.Services.ConfigureHttpClientDefaults(http => http.AddHttpMessageHandler<CorrelationIdHandler>());

        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<AuditFlowExceptionHandler>();

        builder.AddAuditFlowTelemetry(serviceName);

        var health = builder.Services.AddHealthChecks();
        var sql = builder.Configuration.GetConnectionString("Default");
        if (!string.IsNullOrWhiteSpace(sql))
            health.Add(new HealthCheckRegistration("sqlserver", _ => new SqlServerHealthCheck(sql), null, ["ready"]));

        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(o => o.SwaggerDoc("v1", new() { Title = $"AuditFlow {serviceName}", Version = "v1" }));

        return builder;
    }

    /// <summary>
    /// Dapper plumbing + DbUp migrations (this assembly's messaging tables plus the given assemblies' embedded .sql scripts).
    /// Call before <see cref="AddAuditFlowMessaging"/> so migrations run before the outbox dispatcher starts.
    /// </summary>
    public static WebApplicationBuilder AddAuditFlowData(this WebApplicationBuilder builder, params Assembly[] migrationAssemblies)
    {
        var connectionString = builder.Configuration.GetConnectionString("Default")
                               ?? throw new InvalidOperationException("ConnectionStrings:Default is required.");

        builder.Services.AddSingleton<IDbConnectionFactory>(new SqlConnectionFactory(connectionString));
        builder.Services.AddSingleton<IUnitOfWorkFactory, SqlUnitOfWorkFactory>();

        builder.Services.AddSingleton(new MigrationAssembly(typeof(ServiceDefaultsExtensions).Assembly));
        foreach (var assembly in migrationAssemblies)
            builder.Services.AddSingleton(new MigrationAssembly(assembly));

        builder.Services.AddHostedService(sp => new MigrationHostedService(
            connectionString,
            sp.GetServices<MigrationAssembly>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<MigrationHostedService>>()));

        return builder;
    }

    /// <summary>
    /// Outbox, idempotent consumer pipeline and (optionally) the Service Bus subscriber.
    /// <paramref name="publishes"/> starts the outbox dispatcher; <paramref name="consumes"/> starts the subscriber
    /// (needs a Service Bus connection string and ServiceBus:SubscriptionName).
    /// </summary>
    public static WebApplicationBuilder AddAuditFlowMessaging(this WebApplicationBuilder builder, bool publishes, bool consumes)
    {
        builder.Services.AddSingleton<IOutbox, SqlOutbox>();
        builder.Services.AddSingleton<IProcessedMessageStore, SqlProcessedMessageStore>();
        builder.Services.AddSingleton<EventHandlerRegistry>();
        builder.Services.AddSingleton<IEventDispatcher, EventDispatcher>();

        if (publishes)
            builder.Services.AddHostedService<OutboxDispatcher>();

        var serviceBusConfigured = !string.IsNullOrWhiteSpace(builder.Configuration[$"{ServiceBusOptions.Section}:ConnectionString"]);
        if (consumes && serviceBusConfigured)
            builder.Services.AddHostedService<ServiceBusSubscriberHost>();

        return builder;
    }

    /// <summary>
    /// Registers the event bus. With a Service Bus connection string configured it uses the emulator/Azure;
    /// otherwise the in-memory bus (tests, bus-less runs).
    /// </summary>
    public static WebApplicationBuilder AddAuditFlowEventBus(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<ServiceBusOptions>(builder.Configuration.GetSection(ServiceBusOptions.Section));
        var connectionString = builder.Configuration[$"{ServiceBusOptions.Section}:ConnectionString"];

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            builder.Services.AddSingleton<IEventBus, InMemoryEventBus>();
        }
        else
        {
            builder.Services.AddSingleton(_ => new ServiceBusClient(connectionString));
            builder.Services.AddSingleton<IEventBus, ServiceBusEventBus>();
        }
        return builder;
    }

    public static WebApplication UseAuditFlowDefaults(this WebApplication app)
    {
        var service = app.Services.GetRequiredService<ServiceInfo>().Name;

        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseExceptionHandler();
        app.UseSwagger();
        app.UseSwaggerUI();

        if (app.Services.GetService<AuthenticationMarker>() is not null)
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }

        app.MapGet("/", () => Results.Ok(new { service, docs = "/swagger", health = "/health" })).ExcludeFromDescription();
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = (ctx, report) => HealthResponseWriter.WriteAsync(service, ctx, report)
        });
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });

        return app;
    }
}

public sealed record ServiceInfo(string Name);
