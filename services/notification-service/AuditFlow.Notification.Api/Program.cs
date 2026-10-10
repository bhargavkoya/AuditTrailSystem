using System.Text.Json;
using AuditFlow.BuildingBlocks.Hosting;
using AuditFlow.BuildingBlocks.Messaging;
using AuditFlow.BuildingBlocks.Security;
using AuditFlow.Contracts.Events;
using AuditFlow.Notification.Api.Data;

var builder = WebApplication.CreateBuilder(args);
builder.AddAuditFlowDefaults("notification-service");
builder.AddAuditFlowAuthentication();
builder.AddAuditFlowEventBus();
builder.AddAuditFlowData(typeof(Program).Assembly);
builder.AddAuditFlowMessaging(publishes: false, consumes: true);

// Phase 1: log every event type. Phase 3 adds SSE fan-out to subscribers of the same events.
foreach (var eventType in EventTypes.All)
    builder.Services.AddEventHandler<JsonElement, EventLogHandler>(eventType);
builder.Services.AddSingleton<IEventLogReader, EventLogReader>();

var app = builder.Build();
app.UseAuditFlowDefaults();

// DEV ONLY: lets you see events arriving before SSE exists. Removed in Phase 3.
if (app.Configuration.GetValue("AuditFlow:DevEndpoints:Enabled", false))
{
    app.MapGet("/dev/events/recent", async (IEventLogReader reader, string? engagementId, int? take, CancellationToken ct)
        => await reader.RecentAsync(engagementId, take ?? 50, ct))
        .RequireAuthorization().WithTags("Dev");
}

app.Run();

public partial class Program;
