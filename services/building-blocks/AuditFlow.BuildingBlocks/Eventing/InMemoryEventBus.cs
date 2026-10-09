using Microsoft.Extensions.DependencyInjection;

namespace AuditFlow.BuildingBlocks.Eventing;

/// <summary>In-process bus for tests and bus-less runs: dispatches to registered <see cref="IEventHandler{TPayload}"/>s.</summary>
public sealed class InMemoryEventBus(IServiceProvider services) : IEventBus
{
    private readonly List<object> published = [];

    public IReadOnlyList<object> Published => published;

    public async Task PublishAsync<TPayload>(EventEnvelope<TPayload> envelope, CancellationToken cancellationToken = default)
    {
        published.Add(envelope);
        foreach (var handler in services.GetServices<IEventHandler<TPayload>>())
            await handler.HandleAsync(envelope, cancellationToken);
    }
}
