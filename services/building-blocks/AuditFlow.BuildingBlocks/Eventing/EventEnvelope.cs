using AuditFlow.BuildingBlocks.Correlation;

namespace AuditFlow.BuildingBlocks.Eventing;

/// <summary>Versioned wrapper around every domain event. Payloads carry only what consumers need.</summary>
public sealed record EventEnvelope<TPayload>(
    Guid EventId,
    string EventType,
    int SchemaVersion,
    string EngagementId,
    DateTimeOffset OccurredAt,
    string CorrelationId,
    EventActor? Actor,
    TPayload Payload)
{
    public static EventEnvelope<TPayload> Create(
        string eventType, string engagementId, TPayload payload, EventActor? actor = null, int schemaVersion = 1, string? correlationId = null)
        => new(Guid.NewGuid(), eventType, schemaVersion, engagementId, DateTimeOffset.UtcNow,
               correlationId ?? new CorrelationContext().CorrelationId, actor, payload);
}

public sealed record EventActor(string UserId, string? Name);
