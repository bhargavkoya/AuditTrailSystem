using AuditFlow.BuildingBlocks.Data;

namespace AuditFlow.BuildingBlocks.Eventing;

/// <summary>
/// Publishing abstraction. Business code never calls this directly: it writes to the outbox in the same
/// transaction as its state change, and the outbox dispatcher publishes through this interface after commit.
/// </summary>
public interface IEventBus
{
    Task PublishAsync<TPayload>(EventEnvelope<TPayload> envelope, CancellationToken cancellationToken = default);
}

/// <summary>
/// Consumer contract. Runs inside the unit of work that also records the message as processed, so the handler's
/// writes and the idempotency marker commit (or roll back) together.
/// </summary>
public interface IEventHandler<TPayload>
{
    Task HandleAsync(EventEnvelope<TPayload> envelope, IUnitOfWork uow, CancellationToken cancellationToken);
}
