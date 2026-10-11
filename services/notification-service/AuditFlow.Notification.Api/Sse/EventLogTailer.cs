using AuditFlow.Notification.Api.Data;
using Microsoft.Extensions.Options;

namespace AuditFlow.Notification.Api.Sse;

/// <summary>
/// Feeds the hub from <c>DomainEventLog</c>. Only committed rows are ever read, so nothing is pushed before it is durable,
/// and live delivery and replay share one source. Identity values can commit slightly out of order under concurrent
/// consumers, so every poll re-reads a window behind the last published row and skips what it already sent.
/// </summary>
public sealed class EventLogTailer(
    IEventLogReader reader, EventHub hub, IOptions<SseOptions> options, ILogger<EventLogTailer> logger, TimeProvider? clock = null)
    : BackgroundService
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private long lastPollTicks = (clock ?? TimeProvider.System).GetUtcNow().UtcTicks;

    /// <summary>When the tailer last completed a poll (or was created). Read by the readiness check.</summary>
    public DateTimeOffset LastPollAt => new(Interlocked.Read(ref lastPollTicks), TimeSpan.Zero);

    private readonly Queue<long> publishedOrder = new();
    private readonly HashSet<long> published = [];
    private long lastSeq;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Only live traffic: earlier events are reachable through Last-Event-ID replay.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                lastSeq = await reader.MaxSeqAsync(stoppingToken);
                break;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Event log not ready ({Message}); retrying", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }

        var o = options.Value;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var sent = await PollAsync(stoppingToken);
                if (sent == 0) await Task.Delay(o.TailerPollMs, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Event log tailer failed; retrying");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    /// <summary>One poll; returns how many new frames were published. Public for tests.</summary>
    public async Task<int> PollAsync(CancellationToken ct)
    {
        var o = options.Value;
        var rows = await reader.ReadAfterAsync(null, Math.Max(0, lastSeq - o.TailerOverlap), 500, ct);
        Interlocked.Exchange(ref lastPollTicks, clock.GetUtcNow().UtcTicks);

        var count = 0;
        foreach (var row in rows)
        {
            if (!published.Add(row.Seq)) continue;
            publishedOrder.Enqueue(row.Seq);
            hub.Publish(row);
            lastSeq = Math.Max(lastSeq, row.Seq);
            count++;
        }

        while (publishedOrder.Count > o.TailerOverlap * 4)
            published.Remove(publishedOrder.Dequeue());
        return count;
    }
}
