using System.Diagnostics;
using System.Text.Json;
using AuditFlow.BuildingBlocks.Data;
using AuditFlow.BuildingBlocks.Eventing;
using AuditFlow.BuildingBlocks.Telemetry;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AuditFlow.BuildingBlocks.Messaging;

/// <summary>Writes an event into the caller's transaction. It is published only after that transaction commits.</summary>
public interface IOutbox
{
    Task EnqueueAsync<TPayload>(IUnitOfWork uow, EventEnvelope<TPayload> envelope, CancellationToken cancellationToken);
}

public sealed class SqlOutbox : IOutbox
{
    private const string InsertSql = """
        INSERT INTO dbo.OutboxMessage (Id, EventType, EngagementId, Envelope, CorrelationId, OccurredAt, TraceParent, TraceState)
        VALUES (@Id, @EventType, @EngagementId, @Envelope, @CorrelationId, @OccurredAt, @TraceParent, @TraceState)
        """;

    public Task EnqueueAsync<TPayload>(IUnitOfWork uow, EventEnvelope<TPayload> envelope, CancellationToken cancellationToken)
    {
        // The ambient span is the request (or consumer) that caused this event; remember it for the dispatcher.
        var current = Activity.Current;
        return uow.ExecuteAsync(InsertSql, new
        {
            Id = envelope.EventId,
            envelope.EventType,
            envelope.EngagementId,
            Envelope = JsonSerializer.Serialize(envelope, JsonDefaults.Web),
            envelope.CorrelationId,
            envelope.OccurredAt,
            TraceParent = current?.IdFormat == ActivityIdFormat.W3C ? current.Id : null,
            TraceState = current?.TraceStateString
        }, cancellationToken);
    }
}

/// <summary>Trace-context keys carried on Service Bus application properties.</summary>
public static class TraceContextKeys
{
    public const string TraceParent = "traceparent";
    public const string TraceState = "tracestate";
}

/// <summary>
/// Polls unpublished outbox rows in order and publishes them through <see cref="IEventBus"/>.
/// At-least-once: a crash between publish and mark causes a re-publish, which consumers de-duplicate by message id.
/// A failing row is retried with exponential backoff (1s, 2s, 4s ... capped at 30s, up to 100 attempts) and blocks the
/// rows behind it so per-engagement ordering is preserved.
/// </summary>
public sealed class OutboxDispatcher(
    IUnitOfWorkFactory uowFactory,
    IEventBus bus,
    IConfiguration configuration,
    TimeProvider clock,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    public const int MaxAttempts = 100;
    private const int BatchSize = 50;
    private const int MaxBackoffSeconds = 30;

    private const string ClaimSql = """
        SELECT TOP (@Batch) Id, Envelope, Attempts, NextAttemptAt, TraceParent, TraceState
        FROM dbo.OutboxMessage WITH (UPDLOCK, READPAST, ROWLOCK)
        WHERE PublishedAt IS NULL AND Attempts < @MaxAttempts
        ORDER BY Seq
        """;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollMs = configuration.GetValue("Messaging:OutboxPollMs", 500);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var published = await DispatchBatchAsync(stoppingToken);
                if (published == 0)
                    await Task.Delay(pollMs, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox dispatch loop failed; retrying");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    /// <summary>One poll. Returns how many messages were published. Public for tests.</summary>
    public async Task<int> DispatchBatchAsync(CancellationToken ct)
    {
        await using var uow = await uowFactory.BeginAsync(ct);
        var rows = (await uow.QueryAsync<OutboxRow>(ClaimSql, new { Batch = BatchSize, MaxAttempts }, ct)).ToList();

        var now = clock.GetUtcNow();
        if (rows.Count > 0 && rows[0].NextAttemptAt > now)
            return 0; // the oldest event is backing off; do not let newer ones overtake it

        // Prepare the rows in order, stopping at the first one that cannot even be read (it is retried first, like before).
        var items = new List<(OutboxRow Row, OutgoingEvent Event, Activity? Span)>();
        OutboxRow? unreadable = null;
        Exception? unreadableError = null;
        foreach (var row in rows)
        {
            try
            {
                items.Add(Prepare(row));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                unreadable = row;
                unreadableError = ex;
                break;
            }
        }

        // One round trip for the whole poll. The send is all-or-nothing, so a failure leaves every row unpublished and
        // the head row carries the attempt count and backoff; ordering is preserved.
        var published = 0;
        var sendFailed = false;
        if (items.Count > 0)
        {
            try
            {
                await bus.PublishBatchAsync(items.Select(i => i.Event).ToList(), ct);
                await uow.ExecuteAsync("UPDATE dbo.OutboxMessage SET PublishedAt = @now WHERE Id IN @Ids",
                    new { Ids = items.Select(i => i.Row.Id).ToArray(), now = clock.GetUtcNow() }, ct);
                published = items.Count;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sendFailed = true;
                await RecordFailureAsync(uow, items[0].Row, ex, ct);
            }
            finally
            {
                for (var i = items.Count - 1; i >= 0; i--) items[i].Span?.Dispose();
            }
        }

        if (unreadable is not null && !sendFailed)
            await RecordFailureAsync(uow, unreadable, unreadableError!, ct);

        AuditFlowMetrics.SetOutboxPending(await uow.QuerySingleOrDefaultAsync<int>(
            "SELECT COUNT(*) FROM dbo.OutboxMessage WHERE PublishedAt IS NULL", null, ct));
        await uow.CommitAsync(ct);
        return published;
    }

    /// <summary>Child of the originating request; its id travels with the message as traceparent.</summary>
    private static (OutboxRow Row, OutgoingEvent Event, Activity? Span) Prepare(OutboxRow row)
    {
        var envelope = JsonSerializer.Deserialize<EventEnvelope<JsonElement>>(row.Envelope, JsonDefaults.Web)!;
        ActivityContext.TryParse(row.TraceParent, row.TraceState, isRemote: true, out var parent);
        var span = EventDispatcher.Source.StartActivity($"publish {envelope.EventType}", ActivityKind.Producer, parent);
        span?.SetTag("auditflow.event_type", envelope.EventType);
        span?.SetTag("auditflow.engagement_id", envelope.EngagementId);
        span?.SetTag("auditflow.correlation_id", envelope.CorrelationId);
        // Without a listener there is no span: still pass the stored context on so consumers link to the request.
        return (row, new OutgoingEvent(envelope, span?.Id ?? row.TraceParent, span?.TraceStateString ?? row.TraceState), span);
    }

    private async Task RecordFailureAsync(IUnitOfWork uow, OutboxRow row, Exception ex, CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(Math.Min(MaxBackoffSeconds, Math.Pow(2, Math.Min(row.Attempts, 10))));
        AuditFlowMetrics.OutboxPublishFailures.Add(1);
        logger.LogWarning("Publishing outbox message {Id} failed (attempt {Attempt}); retrying in {Backoff}s: {Error}",
            row.Id, row.Attempts + 1, backoff.TotalSeconds, ex.Message);
        await uow.ExecuteAsync(
            "UPDATE dbo.OutboxMessage SET Attempts = Attempts + 1, LastError = LEFT(@Error, 1000), NextAttemptAt = @next WHERE Id = @Id",
            new { row.Id, Error = ex.Message, next = clock.GetUtcNow().Add(backoff) }, ct);
    }

    private sealed record OutboxRow(
        Guid Id, string Envelope, int Attempts, DateTimeOffset? NextAttemptAt, string? TraceParent, string? TraceState);
}
