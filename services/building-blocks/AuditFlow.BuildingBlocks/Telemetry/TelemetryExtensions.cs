using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AuditFlow.BuildingBlocks.Telemetry;

public static class TelemetryExtensions
{
    /// <summary>
    /// OpenTelemetry tracing + metrics. Exporter choice:
    /// APPLICATIONINSIGHTS_CONNECTION_STRING -> Azure Application Insights;
    /// OTEL_EXPORTER_OTLP_ENDPOINT -> OTLP; otherwise console (so everything runs without an Azure account).
    /// </summary>
    public static WebApplicationBuilder AddAuditFlowTelemetry(this WebApplicationBuilder builder, string serviceName)
    {
        var appInsights = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        var otlp = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var consoleEnabled = builder.Configuration.GetValue("AuditFlow:Telemetry:Console", false);

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithTracing(t =>
            {
                t.AddSource("AuditFlow.*")
                 .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
                 .AddHttpClientInstrumentation();

                if (!string.IsNullOrWhiteSpace(appInsights)) t.AddAzureMonitorTraceExporter(o => o.ConnectionString = appInsights);
                else if (!string.IsNullOrWhiteSpace(otlp)) t.AddOtlpExporter();
                else if (consoleEnabled) t.AddConsoleExporter();
            })
            .WithMetrics(m =>
            {
                m.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();

                if (!string.IsNullOrWhiteSpace(appInsights)) m.AddAzureMonitorMetricExporter(o => o.ConnectionString = appInsights);
                else if (!string.IsNullOrWhiteSpace(otlp)) m.AddOtlpExporter();
            });

        return builder;
    }
}
