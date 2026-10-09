using AuditFlow.BuildingBlocks.Correlation;
using AuditFlow.BuildingBlocks.Eventing;
using AuditFlow.BuildingBlocks.Health;
using AuditFlow.BuildingBlocks.Telemetry;
using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AuditFlow.BuildingBlocks.Hosting;

/// <summary>One-line wiring shared by every service: correlation, telemetry, health, Swagger, event bus.</summary>
public static class ServiceDefaultsExtensions
{
    public static WebApplicationBuilder AddAuditFlowDefaults(this WebApplicationBuilder builder, string serviceName)
    {
        builder.Services.AddSingleton(new ServiceInfo(serviceName));
        builder.Services.AddSingleton<ICorrelationContext, CorrelationContext>();
        builder.Services.AddTransient<CorrelationIdHandler>();
        builder.Services.ConfigureHttpClientDefaults(http => http.AddHttpMessageHandler<CorrelationIdHandler>());

        builder.AddAuditFlowTelemetry(serviceName);

        var health = builder.Services.AddHealthChecks();
        var sql = builder.Configuration.GetConnectionString("SqlServer");
        if (!string.IsNullOrWhiteSpace(sql))
            health.Add(new HealthCheckRegistration("sqlserver", _ => new SqlServerHealthCheck(sql), null, ["ready"]));

        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(o => o.SwaggerDoc("v1", new() { Title = $"AuditFlow {serviceName}", Version = "v1" }));

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
        app.UseSwagger();
        app.UseSwaggerUI();

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
