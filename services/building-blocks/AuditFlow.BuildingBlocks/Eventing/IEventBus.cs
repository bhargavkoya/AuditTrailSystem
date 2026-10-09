namespace AuditFlow.BuildingBlocks.Eventing;

/// <summary>
/// Publishing abstraction. Business code never calls this directly: it writes to the outbox in the same
/// transaction as its state change, and the outbox dispatcher publishes through this interface after commit.
/// </summary>
public interface IEventBus
{
    Task PublishAsync<TPayload>(EventEnvelope<TPayload> envelope, CancellationToken cancellationToken = default);
}

/// <summary>Consumer contract. Implementations must be idempotent (at-least-once delivery).</summary>
public interface IEventHandler<TPayload>
{
    Task HandleAsync(EventEnvelope<TPayload> envelope, CancellationToken cancellationToken);
}
