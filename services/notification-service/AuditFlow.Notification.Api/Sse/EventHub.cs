using System.Collections.Concurrent;
using System.Threading.Channels;
using AuditFlow.Contracts.Api;
using AuditFlow.Contracts.Events;

namespace AuditFlow.Notification.Api.Sse;

public sealed class SseOptions
{
    public const string Section = "Sse";

    public int HeartbeatSeconds { get; set; } = 15;
    /// <summary>Most events replayed after a reconnect; a longer gap becomes a "resync".</summary>
    public int ReplayCap { get; set; } = 500;
    /// <summary>Events buffered per connected client; a slower client is told to "resync".</summary>
    public int ChannelCapacity { get; set; } = 256;
    public int TailerPollMs { get; set; } = 250;
    /// <summary>Rows before the last published one that the tailer re-reads, to catch identity values committed out of order.</summary>
    public int TailerOverlap { get; set; } = 200;
}

/// <summary>One committed row of the event log, ready to be written as an SSE frame.</summary>
public sealed record SseFrame(long Seq, string EventType, string EngagementId, string Envelope);

/// <summary>Which events a caller may receive. Viewers never see attachment or comment activity.</summary>
public static class SseFilter
{
    public static bool Allows(string eventType, IReadOnlyCollection<string> capabilities) => eventType switch
    {
        EventTypes.AttachmentAdded => capabilities.Contains(Capabilities.AttachView),
        EventTypes.ReviewerCommentAdded => capabilities.Contains(Capabilities.CommentView),
        _ => true
    };
}

/// <summary>A connected browser. Bounded: a client that cannot keep up gets a resync instead of unbounded memory growth.</summary>
public sealed class SseSubscription : IDisposable
{
    private readonly Channel<SseFrame> channel;
    private readonly Action<SseSubscription> onDispose;
    private volatile bool overflowed;

    internal SseSubscription(string engagementId, int capacity, Action<SseSubscription> onDispose)
    {
        EngagementId = engagementId;
        this.onDispose = onDispose;
        channel = Channel.CreateBounded<SseFrame>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait, // TryWrite then fails when full (DropWrite would report success)
            SingleReader = true
        });
    }

    public string EngagementId { get; }
    public ChannelReader<SseFrame> Reader => channel.Reader;
    public bool Overflowed => overflowed;

    internal void Offer(SseFrame frame)
    {
        if (!channel.Writer.TryWrite(frame)) overflowed = true;
    }

    public void Dispose()
    {
        onDispose(this);
        channel.Writer.TryComplete();
    }
}

/// <summary>In-memory fan-out of committed events to the browsers watching an engagement (one process; see ADR 0008).</summary>
public sealed class EventHub(Microsoft.Extensions.Options.IOptions<SseOptions> options)
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<SseSubscription, byte>> subscribers = new();

    public int SubscriberCount => subscribers.Values.Sum(s => s.Count);

    public SseSubscription Subscribe(string engagementId)
    {
        var subscription = new SseSubscription(engagementId, options.Value.ChannelCapacity, Remove);
        subscribers.GetOrAdd(engagementId, _ => new()).TryAdd(subscription, 0);
        return subscription;
    }

    public void Publish(SseFrame frame)
    {
        if (!subscribers.TryGetValue(frame.EngagementId, out var group)) return;
        foreach (var subscription in group.Keys) subscription.Offer(frame);
    }

    private void Remove(SseSubscription subscription)
    {
        if (subscribers.TryGetValue(subscription.EngagementId, out var group)) group.TryRemove(subscription, out _);
    }
}
