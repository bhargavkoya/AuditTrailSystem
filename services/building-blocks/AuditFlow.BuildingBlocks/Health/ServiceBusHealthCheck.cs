using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AuditFlow.BuildingBlocks.Health;

/// <summary>Readiness: the broker answers for this service's subscription (a peek, which consumes nothing).</summary>
public sealed class ServiceBusHealthCheck(Func<CancellationToken, Task> probe, TimeSpan timeout) : IHealthCheck
{
    public static ServiceBusHealthCheck ForSubscription(ServiceBusClient client, string topic, string subscription)
        => new(async ct =>
        {
            await using var receiver = client.CreateReceiver(topic, subscription);
            await receiver.PeekMessageAsync(cancellationToken: ct);
        }, TimeSpan.FromSeconds(5));

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await probe(cts.Token);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("Service Bus unreachable", ex);
        }
    }
}
