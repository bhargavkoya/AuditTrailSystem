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

        // Dispatcher's "publish" span is current here; consumers parent their span on it.
        var activity = Activity.Current;
        if (activity?.IdFormat == ActivityIdFormat.W3C && activity.Id is not null)
        {
            message.ApplicationProperties[TraceContextKeys.TraceParent] = activity.Id;
            if (!string.IsNullOrEmpty(activity.TraceStateString))
                message.ApplicationProperties[TraceContextKeys.TraceState] = activity.TraceStateString;
        }

        await sender.SendMessageAsync(message, cancellationToken);
    }

    public ValueTask DisposeAsync() => sender.DisposeAsync();
}
