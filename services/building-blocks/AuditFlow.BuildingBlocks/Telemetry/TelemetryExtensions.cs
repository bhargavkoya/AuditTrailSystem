using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AuditFlow.BuildingBlocks.Telemetry;

public static class TelemetryExtensions
{
    /// <summary>
    /// OpenTelemetry traces, metrics and logs. Exporter choice (same for all three signals):
    /// APPLICATIONINSIGHTS_CONNECTION_STRING -> Azure Application Insights;
    /// OTEL_EXPORTER_OTLP_ENDPOINT -> OTLP (e.g. the local Aspire dashboard); otherwise nothing is exported, and
    /// the console carries logs (with scopes) so everything runs without an Azure account.
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
                 .AddHttpClientInstrumentation()
                 .AddSqlClientInstrumentation(); // sees Dapper's commands, so no Dapper-specific wrapper is needed

                if (!string.IsNullOrWhiteSpace(appInsights)) t.AddAzureMonitorTraceExporter(o => o.ConnectionString = appInsights);
                else if (!string.IsNullOrWhiteSpace(otlp)) t.AddOtlpExporter();
                else if (consoleEnabled) t.AddConsoleExporter();
            })
            .WithMetrics(m =>
            {
                // "System.Runtime" is the built-in .NET 8 meter (thread-pool queue length, GC) used by the load test.
                m.AddMeter(AuditFlowMetrics.MeterName, "System.Runtime")
                 .AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();

                if (!string.IsNullOrWhiteSpace(appInsights)) m.AddAzureMonitorMetricExporter(o => o.ConnectionString = appInsights);
                else if (!string.IsNullOrWhiteSpace(otlp)) m.AddOtlpExporter();
            });

        // Two sources of log volume that cost real CPU under load and say nothing useful:
        //  - the framework logs an Error with a stack trace for EVERY exception, even the ones AuditFlowExceptionHandler turns
        //    into expected 403/404/409 responses (genuinely unhandled exceptions are still logged by our handler);
        //  - four Information lines per outgoing HttpClient call (the HTTP spans already record each call).
        builder.Logging.AddFilter("Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware", LogLevel.None);
        builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

        // Console keeps scopes ({CorrelationId}, {EngagementId}) so a grep by id works with no backend at all.
        builder.Logging.AddSimpleConsole(o =>
        {
            o.IncludeScopes = true;
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        });

        // Log records carry the active TraceId/SpanId, which is what joins a log line to its trace in the backend.
        builder.Logging.AddOpenTelemetry(o =>
        {
            o.IncludeScopes = true;
            o.IncludeFormattedMessage = true;
            o.ParseStateValues = true;
            o.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName));

            if (!string.IsNullOrWhiteSpace(appInsights)) o.AddAzureMonitorLogExporter(x => x.ConnectionString = appInsights);
            else if (!string.IsNullOrWhiteSpace(otlp)) o.AddOtlpExporter();
        });

        return builder;
    }
}
