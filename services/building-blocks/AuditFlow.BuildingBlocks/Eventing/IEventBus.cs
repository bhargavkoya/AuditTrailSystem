using System.Text.Json;
using AuditFlow.BuildingBlocks.Data;

namespace AuditFlow.BuildingBlocks.Eventing;

/// <summary>
/// Publishing abstraction. Business code never calls this directly: it writes to the outbox in the same
/// transaction as its state change, and the outbox dispatcher publishes through this interface after commit.
/// </summary>
public interface IEventBus
{
    Task PublishAsync<TPayload>(EventEnvelope<TPayload> envelope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes events in order with as few broker round trips as possible. All-or-nothing: if it throws, none of them
    /// may be assumed delivered (the outbox retries the whole batch; consumers de-duplicate by message id).
    /// </summary>
    Task PublishBatchAsync(IReadOnlyList<OutgoingEvent> events, CancellationToken cancellationToken = default);
}

/// <summary>An event read back from the outbox, with the trace context to stamp on the message.</summary>
public sealed record OutgoingEvent(EventEnvelope<JsonElement> Envelope, string? TraceParent, string? TraceState);

/// <summary>
/// Consumer contract. Runs inside the unit of work that also records the message as processed, so the handler's
/// writes and the idempotency marker commit (or roll back) together.
/// </summary>
public interface IEventHandler<TPayload>
{
    Task HandleAsync(EventEnvelope<TPayload> envelope, IUnitOfWork uow, CancellationToken cancellationToken);
}
