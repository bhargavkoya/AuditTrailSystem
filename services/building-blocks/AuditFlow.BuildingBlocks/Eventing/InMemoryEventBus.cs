using Microsoft.Extensions.Logging;

namespace AuditFlow.BuildingBlocks.Eventing;

/// <summary>
/// Bus-less mode (no Service Bus connection string) and tests: records what was published. Nothing is delivered
/// to consumers; delivery needs the real bus or emulator.
/// </summary>
public sealed class InMemoryEventBus(ILogger<InMemoryEventBus> logger) : IEventBus
{
    private readonly List<object> published = [];

    public IReadOnlyList<object> Published => published;

    public Task PublishAsync<TPayload>(EventEnvelope<TPayload> envelope, CancellationToken cancellationToken = default)
    {
        published.Add(envelope);
        logger.LogInformation("In-memory bus recorded {EventType} for {EngagementId} (not delivered)", envelope.EventType, envelope.EngagementId);
        return Task.CompletedTask;
    }
}
