using System.Diagnostics;
using System.Text.Json;
using AuditFlow.BuildingBlocks.Correlation;
using AuditFlow.BuildingBlocks.Data;
using AuditFlow.BuildingBlocks.Eventing;
using Azure.Messaging.ServiceBus;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuditFlow.BuildingBlocks.Messaging;

/// <summary>Maps an event type to a typed handler invocation.</summary>
public sealed record EventHandlerRegistration(
    string EventType,
    Func<IServiceProvider, string, IUnitOfWork, CancellationToken, Task> Invoke);

public sealed class EventHandlerRegistry(IEnumerable<EventHandlerRegistration> registrations)
{
    private readonly ILookup<string, EventHandlerRegistration> byType = registrations.ToLookup(r => r.EventType);

    public bool IsEmpty => !byType.Any();

    public IReadOnlyList<EventHandlerRegistration> For(string eventType) => byType[eventType].ToList();
}

public static class EventHandlerRegistrationExtensions
{
    public static IServiceCollection AddEventHandler<TPayload, THandler>(this IServiceCollection services, string eventType)
        where THandler : class, IEventHandler<TPayload>
    {
        services.AddScoped<THandler>();
        services.AddSingleton(new EventHandlerRegistration(eventType, async (sp, json, uow, ct) =>
        {
            var envelope = JsonSerializer.Deserialize<EventEnvelope<TPayload>>(json, JsonDefaults.Web)
                           ?? throw new InvalidOperationException($"Could not deserialize {eventType}");
            await sp.GetRequiredService<THandler>().HandleAsync(envelope, uow, ct);
        }));
        return services;
    }
}

/// <summary>Records that a consumer handled a message id. Returns false when it was already handled.</summary>
public interface IProcessedMessageStore
{
    Task<bool> TryMarkAsync(string consumerName, Guid messageId, IUnitOfWork uow, CancellationToken cancellationToken);
}

public sealed class SqlProcessedMessageStore : IProcessedMessageStore
{
    private const int PrimaryKeyViolation = 2627;

    public async Task<bool> TryMarkAsync(string consumerName, Guid messageId, IUnitOfWork uow, CancellationToken cancellationToken)
    {
        try
        {
            await uow.ExecuteAsync(
                "INSERT INTO dbo.ProcessedMessage (ConsumerName, MessageId, ProcessedAt) VALUES (@consumerName, @messageId, SYSDATETIMEOFFSET())",
                new { consumerName, messageId }, cancellationToken);
            return true;
        }
        catch (SqlException ex) when (ex.Number == PrimaryKeyViolation)
        {
            return false;
        }
    }
}

public interface IEventDispatcher
{
    /// <summary>Returns true when handlers ran, false when the message was ignored or a duplicate.</summary>
    Task<bool> DispatchAsync(string consumerName, Guid messageId, string eventType, string json, string? correlationId, CancellationToken cancellationToken);
}

/// <summary>
/// Idempotent consumer pipeline: in one transaction, mark the message processed and run the handlers.
/// A duplicate delivery hits the primary key and is skipped; a handler failure rolls back the marker so a retry reprocesses.
/// </summary>
public sealed class EventDispatcher(
    IUnitOfWorkFactory uowFactory,
    IProcessedMessageStore store,
    EventHandlerRegistry registry,
    IServiceScopeFactory scopes,
    ILogger<EventDispatcher> logger) : IEventDispatcher
{
    public static readonly ActivitySource Source = new("AuditFlow.Messaging");

    public async Task<bool> DispatchAsync(string consumerName, Guid messageId, string eventType, string json, string? correlationId, CancellationToken cancellationToken)
    {
        var registrations = registry.For(eventType);
        if (registrations.Count == 0)
            return false;

        using var correlation = CorrelationContext.Begin(correlationId ?? string.Empty);
        using var activity = Source.StartActivity($"consume {eventType}", ActivityKind.Consumer);
        activity?.SetTag("auditflow.correlation_id", correlationId);
        activity?.SetTag("messaging.message_id", messageId);

        await using var uow = await uowFactory.BeginAsync(cancellationToken);
        if (!await store.TryMarkAsync(consumerName, messageId, uow, cancellationToken))
        {
            logger.LogInformation("Skipping duplicate {EventType} {MessageId} for {Consumer}", eventType, messageId, consumerName);
            return false;
        }

        await using var scope = scopes.CreateAsyncScope();
        foreach (var registration in registrations)
            await registration.Invoke(scope.ServiceProvider, json, uow, cancellationToken);

        await uow.CommitAsync(cancellationToken);
        return true;
    }
}

/// <summary>Consumes this service's Service Bus subscription and feeds the dispatcher. Idle when nothing is registered.</summary>
public sealed class ServiceBusSubscriberHost(
    ServiceBusClient client,
    IOptions<ServiceBusOptions> options,
    EventHandlerRegistry registry,
    IEventDispatcher dispatcher,
    ILogger<ServiceBusSubscriberHost> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (registry.IsEmpty || string.IsNullOrWhiteSpace(o.SubscriptionName))
        {
            logger.LogInformation("No event handlers or subscription configured; subscriber idle");
            return;
        }

        // The emulator / namespace may still be loading its topology. A processor started before the subscription
        // exists does not recover reliably, so wait until the subscription is reachable first.
        await WaitForSubscriptionAsync(o, stoppingToken);
        if (stoppingToken.IsCancellationRequested) return;

        await using var processor = client.CreateProcessor(o.TopicName, o.SubscriptionName, new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false,
            MaxConcurrentCalls = 4
        });

        processor.ProcessMessageAsync += args => HandleAsync(args, o.SubscriptionName);
        processor.ProcessErrorAsync += args =>
        {
            logger.LogError(args.Exception, "Service Bus error in {Source} ({EntityPath})", args.ErrorSource, args.EntityPath);
            return Task.CompletedTask;
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await processor.StartProcessingAsync(stoppingToken);
                logger.LogInformation("Consuming {Topic}/{Subscription}", o.TopicName, o.SubscriptionName);
                break;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Service Bus not ready ({Message}); retrying in 5s", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        try { await Task.Delay(Timeout.Infinite, stoppingToken); } catch (OperationCanceledException) { }
        await processor.StopProcessingAsync(CancellationToken.None);
    }

    private async Task WaitForSubscriptionAsync(ServiceBusOptions o, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var receiver = client.CreateReceiver(o.TopicName, o.SubscriptionName);
                await receiver.PeekMessageAsync(cancellationToken: ct);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogInformation("Waiting for subscription {Topic}/{Subscription} ({Reason})", o.TopicName, o.SubscriptionName, ex.GetType().Name);
                try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task HandleAsync(ProcessMessageEventArgs args, string consumerName)
    {
        var message = args.Message;
        var eventType = message.Subject ?? message.ApplicationProperties.GetValueOrDefault("EventType")?.ToString() ?? string.Empty;
        try
        {
            await dispatcher.DispatchAsync(consumerName, Guid.Parse(message.MessageId), eventType, message.Body.ToString(),
                message.CorrelationId, args.CancellationToken);
            await args.CompleteMessageAsync(message, args.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Handling {EventType} {MessageId} failed (delivery {Count})", eventType, message.MessageId, message.DeliveryCount);
            await args.AbandonMessageAsync(message, cancellationToken: args.CancellationToken);
        }
    }
}
