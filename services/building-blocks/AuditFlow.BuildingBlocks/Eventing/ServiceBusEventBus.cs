using System.Diagnostics;
using System.Text.Json;
using AuditFlow.BuildingBlocks.Messaging;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;

namespace AuditFlow.BuildingBlocks.Eventing;

public sealed class ServiceBusOptions
{
    public const string Section = "ServiceBus";

    public string ConnectionString { get; set; } = string.Empty;
    public string TopicName { get; set; } = "auditflow-events";

    /// <summary>Subscription this service consumes from. Empty = the service does not consume.</summary>
    public string SubscriptionName { get; set; } = string.Empty;

    /// <summary>
    /// Must match MaxDeliveryCount on the subscription (infra/local/servicebus/Config.json, the Azure subscription). On that
    /// delivery a failure is logged as dead-lettered and counted; the broker then moves the message to the dead-letter queue.
    /// </summary>
    public int MaxDeliveryCount { get; set; } = 5;
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// Publishes envelopes to the shared topic. MessageId = EventId (duplicate detection / consumer idempotency);
/// Subject = EventType (subscription correlation filters); EngagementId is an application property.
/// </summary>
public sealed class ServiceBusEventBus(ServiceBusClient client, IOptions<ServiceBusOptions> options) : IEventBus, IAsyncDisposable
{
    private readonly ServiceBusSender sender = client.CreateSender(options.Value.TopicName);

    public async Task PublishAsync<TPayload>(EventEnvelope<TPayload> envelope, CancellationToken cancellationToken = default)
    {
        // A "publish" span is current when the caller made one; consumers parent their span on it.
        var activity = Activity.Current;
        var message = Build(envelope, activity?.IdFormat == ActivityIdFormat.W3C ? activity.Id : null, activity?.TraceStateString);
        await sender.SendMessageAsync(message, cancellationToken);
    }

    public async Task PublishBatchAsync(IReadOnlyList<OutgoingEvent> events, CancellationToken cancellationToken = default)
    {
        var batch = await sender.CreateMessageBatchAsync(cancellationToken);
        try
        {
            foreach (var e in events)
            {
                var message = Build(e.Envelope, e.TraceParent, e.TraceState);
                if (batch.TryAddMessage(message)) continue;

                if (batch.Count == 0) throw new InvalidOperationException($"Event {e.Envelope.EventId} is too large for a Service Bus message.");
                await sender.SendMessagesAsync(batch, cancellationToken);
                batch.Dispose();
                batch = await sender.CreateMessageBatchAsync(cancellationToken);
                if (!batch.TryAddMessage(message)) throw new InvalidOperationException($"Event {e.Envelope.EventId} is too large for a Service Bus message.");
            }

            if (batch.Count > 0) await sender.SendMessagesAsync(batch, cancellationToken);
        }
        finally
        {
            batch.Dispose();
        }
    }

    private static ServiceBusMessage Build<TPayload>(EventEnvelope<TPayload> envelope, string? traceParent, string? traceState)
    {
        var message = new ServiceBusMessage(JsonSerializer.SerializeToUtf8Bytes(envelope, JsonDefaults.Web))
        {
            MessageId = envelope.EventId.ToString(),
            CorrelationId = envelope.CorrelationId,
            Subject = envelope.EventType,
            ContentType = "application/json"
        };
        message.ApplicationProperties["EventType"] = envelope.EventType;
        message.ApplicationProperties["EngagementId"] = envelope.EngagementId;
        message.ApplicationProperties["SchemaVersion"] = envelope.SchemaVersion;

        if (!string.IsNullOrEmpty(traceParent))
        {
            message.ApplicationProperties[TraceContextKeys.TraceParent] = traceParent;
            if (!string.IsNullOrEmpty(traceState)) message.ApplicationProperties[TraceContextKeys.TraceState] = traceState;
        }

        return message;
    }

    public ValueTask DisposeAsync() => sender.DisposeAsync();
}
