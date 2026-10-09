using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;

namespace AuditFlow.BuildingBlocks.Eventing;

public sealed class ServiceBusOptions
{
    public const string Section = "ServiceBus";

    public string ConnectionString { get; set; } = string.Empty;
    public string TopicName { get; set; } = "auditflow-events";
}

/// <summary>
/// Publishes envelopes to the shared topic. MessageId = EventId (duplicate detection / consumer idempotency);
/// EventType and EngagementId are application properties so subscriptions can filter on them.
/// </summary>
public sealed class ServiceBusEventBus(ServiceBusClient client, IOptions<ServiceBusOptions> options) : IEventBus, IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ServiceBusSender sender = client.CreateSender(options.Value.TopicName);

    public async Task PublishAsync<TPayload>(EventEnvelope<TPayload> envelope, CancellationToken cancellationToken = default)
    {
        var message = new ServiceBusMessage(JsonSerializer.SerializeToUtf8Bytes(envelope, Json))
        {
            MessageId = envelope.EventId.ToString(),
            CorrelationId = envelope.CorrelationId,
            Subject = envelope.EventType,
            ContentType = "application/json"
        };
        message.ApplicationProperties["EventType"] = envelope.EventType;
        message.ApplicationProperties["EngagementId"] = envelope.EngagementId;
        message.ApplicationProperties["SchemaVersion"] = envelope.SchemaVersion;

        await sender.SendMessageAsync(message, cancellationToken);
    }

    public ValueTask DisposeAsync() => sender.DisposeAsync();
}
