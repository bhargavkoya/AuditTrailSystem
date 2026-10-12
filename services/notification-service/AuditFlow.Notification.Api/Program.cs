using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using AuditFlow.BuildingBlocks.Hosting;
using AuditFlow.BuildingBlocks.Messaging;
using AuditFlow.BuildingBlocks.Security;
using AuditFlow.BuildingBlocks.Access;
using AuditFlow.Contracts.Events;
using AuditFlow.Notification.Api.Data;
using AuditFlow.Notification.Api.Endpoints;
using AuditFlow.Notification.Api.Sse;

var builder = WebApplication.CreateBuilder(args);
builder.AddAuditFlowDefaults("notification-service");
builder.AddAuditFlowAuthentication();
builder.AddAuditFlowEventBus();
builder.AddAuditFlowData(typeof(Program).Assembly);
builder.AddAuditFlowMessaging(publishes: false, consumes: true);
builder.AddEngagementAccessClient();   // the stream asks engagement-service what the caller may see

// Every event type is persisted to DomainEventLog; the tailer turns committed rows into SSE frames.
foreach (var eventType in EventTypes.All)
    builder.Services.AddEventHandler<JsonElement, EventLogHandler>(eventType);
builder.Services.AddSingleton<IEventLogReader, EventLogReader>();

builder.Services.Configure<SseOptions>(builder.Configuration.GetSection(SseOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<EventHub>();
builder.Services.AddSingleton<SseSession>();
builder.Services.AddSingleton<EventLogTailer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EventLogTailer>());
builder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
    "event-log-tailer", sp => new TailerHealthCheck(sp.GetRequiredService<EventLogTailer>(), sp.GetRequiredService<TimeProvider>()),
    null, ["ready"]));

var app = builder.Build();
app.Services.GetRequiredService<EventHub>().RegisterMetrics();
app.UseAuditFlowDefaults();
app.MapStreamEndpoints();
app.Run();

public partial class Program;
