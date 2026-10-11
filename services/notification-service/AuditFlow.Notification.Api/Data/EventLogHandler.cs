using System.Text.Json;
using AuditFlow.BuildingBlocks.Data;
using AuditFlow.BuildingBlocks.Eventing;

namespace AuditFlow.Notification.Api.Data;

/// <summary>
/// Persists every domain event it receives. Runs inside the idempotent consumer transaction, so a redelivered
/// message cannot create a second row (the EventId unique index is a second line of defence).
/// </summary>
public sealed class EventLogHandler : IEventHandler<JsonElement>
{
    private const string InsertSql = """
        INSERT INTO dbo.DomainEventLog (EventId, EventType, EngagementId, Envelope, CorrelationId, OccurredAt)
        VALUES (@EventId, @EventType, @EngagementId, @Envelope, @CorrelationId, @OccurredAt)
        """;

    public Task HandleAsync(EventEnvelope<JsonElement> envelope, IUnitOfWork uow, CancellationToken cancellationToken)
        => uow.ExecuteAsync(InsertSql, new
        {
            envelope.EventId,
            envelope.EventType,
            envelope.EngagementId,
            Envelope = JsonSerializer.Serialize(envelope, JsonDefaults.Web),
            envelope.CorrelationId,
            envelope.OccurredAt
        }, cancellationToken);
}

