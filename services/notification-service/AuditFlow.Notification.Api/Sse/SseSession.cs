using System.Text;
using AuditFlow.Notification.Api.Data;
using Microsoft.Extensions.Options;

namespace AuditFlow.Notification.Api.Sse;

/// <summary>
/// One SSE connection: replay after Last-Event-ID, then live frames, heartbeats, and the control events
/// <c>ready</c> (stream is open; refetch to close the gap since your last read), <c>resync</c> (you missed too much; refetch)
/// and <c>reauth</c> (your token expired). Writes to a plain stream so it is testable without HTTP.
/// </summary>
public sealed class SseSession(EventHub hub, IEventLogReader log, IOptions<SseOptions> options, TimeProvider clock)
{
    public async Task RunAsync(
        string engagementId,
        IReadOnlyCollection<string> capabilities,
        long? lastEventId,
        DateTimeOffset tokenExpiresAt,
        Stream output,
        CancellationToken ct)
    {
        var o = options.Value;
        using var subscription = hub.Subscribe(engagementId); // subscribe before replaying so nothing falls in the gap
        var sent = new SentTracker(o.ReplayCap * 4);

        await WriteAsync(output, "retry: 3000\n\n", ct);

        var max = await log.MaxSeqAsync(ct);
        if (lastEventId is null)
        {
            await WriteAsync(output, ControlFrame("ready", max), ct);
        }
        else
        {
            var replay = lastEventId > max
                ? null // the client is ahead of the log (e.g. it was reset): cannot reason about the gap
                : await log.ReadAfterAsync(engagementId, lastEventId.Value, o.ReplayCap + 1, ct);
            if (replay is null || replay.Count > o.ReplayCap)
            {
                await WriteAsync(output, ControlFrame("resync", max), ct);
            }
            else
            {
                foreach (var frame in replay)
                {
                    if (!sent.TryAdd(frame.Seq) || !SseFilter.Allows(frame.EventType, capabilities)) continue;
                    await WriteAsync(output, Format(frame), ct);
                }
            }
        }

        var heartbeat = TimeSpan.FromSeconds(Math.Max(0.05, o.HeartbeatSeconds));
        while (!ct.IsCancellationRequested)
        {
            if (clock.GetUtcNow() >= tokenExpiresAt)
            {
                await WriteAsync(output, "event: reauth\ndata: {}\n\n", ct);
                return;
            }
            if (subscription.Overflowed)
            {
                await WriteAsync(output, ControlFrame("resync", await log.MaxSeqAsync(ct)), ct);
                return;
            }

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(heartbeat);
            try
            {
                if (!await subscription.Reader.WaitToReadAsync(wait.Token)) return;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await WriteAsync(output, ": heartbeat\n\n", ct);
                continue;
            }

            while (subscription.Reader.TryRead(out var frame))
            {
                if (!sent.TryAdd(frame.Seq) || !SseFilter.Allows(frame.EventType, capabilities)) continue;
                await WriteAsync(output, Format(frame), ct);
            }
        }
    }

    public static string Format(SseFrame frame)
        => $"id: {frame.Seq}\nevent: {frame.EventType}\ndata: {frame.Envelope.ReplaceLineEndings(" ")}\n\n";

    private static string ControlFrame(string name, long seq) => $"id: {seq}\nevent: {name}\ndata: {{\"seq\":{seq}}}\n\n";

    private static async Task WriteAsync(Stream output, string text, CancellationToken ct)
    {
        await output.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
        await output.FlushAsync(ct);
    }

    /// <summary>Remembers recently sent sequence numbers so replay and live delivery never repeat a frame.</summary>
    private sealed class SentTracker(int capacity)
    {
        private readonly HashSet<long> seen = [];
        private readonly Queue<long> order = new();

        public bool TryAdd(long seq)
        {
            if (!seen.Add(seq)) return false;
            order.Enqueue(seq);
            while (order.Count > capacity) seen.Remove(order.Dequeue());
            return true;
        }

    }
}
