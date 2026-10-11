using System.Diagnostics.Metrics;

namespace AuditFlow.BuildingBlocks.Telemetry;

/// <summary>The one custom meter. Instruments are static so any layer can record without DI plumbing.</summary>
public static class AuditFlowMetrics
{
    public const string MeterName = "AuditFlow";

    private static readonly Meter Meter = new(MeterName);
    private static long outboxPending;

    public static readonly Counter<long> OutboxPublishFailures = Meter.CreateCounter<long>("auditflow.outbox.publish_failures");
    public static readonly Counter<long> EventsConsumed = Meter.CreateCounter<long>("auditflow.events.consumed");
    public static readonly Counter<long> EventsFailed = Meter.CreateCounter<long>("auditflow.events.failed");
    public static readonly Counter<long> EventsDuplicates = Meter.CreateCounter<long>("auditflow.events.duplicates");
    public static readonly Counter<long> ConcurrencyConflicts = Meter.CreateCounter<long>("auditflow.concurrency.conflicts");
    public static readonly Counter<long> SseDropped = Meter.CreateCounter<long>("auditflow.sse.dropped");
    public static readonly Counter<long> LifecycleTransitions = Meter.CreateCounter<long>("auditflow.lifecycle.transitions");
    public static readonly Counter<long> BlockedSubmits = Meter.CreateCounter<long>("auditflow.validation.blocked_submits");

    static AuditFlowMetrics()
    {
        Meter.CreateObservableGauge("auditflow.outbox.pending", () => Interlocked.Read(ref outboxPending),
            description: "Unpublished outbox rows, as of the last dispatcher poll");
    }

    /// <summary>Called by services that host an SSE hub; the callback returns the current open stream count.</summary>
    public static void RegisterSseConnections(Func<int> count)
        => Meter.CreateObservableGauge("auditflow.sse.connections", count, description: "Open SSE streams");

    public static void SetOutboxPending(long value) => Interlocked.Exchange(ref outboxPending, value);

    public static void RecordTransition(string from, string to)
        => LifecycleTransitions.Add(1, new KeyValuePair<string, object?>("from", from), new KeyValuePair<string, object?>("to", to));

    public static void RecordConsumed(string eventType) => EventsConsumed.Add(1, EventTag(eventType));
    public static void RecordFailed(string eventType) => EventsFailed.Add(1, EventTag(eventType));
    public static void RecordDuplicate(string eventType) => EventsDuplicates.Add(1, EventTag(eventType));

    public static void RecordConflict(string? resource)
        => ConcurrencyConflicts.Add(1, new KeyValuePair<string, object?>("resource", resource ?? "engagement"));

    private static KeyValuePair<string, object?> EventTag(string eventType) => new("event_type", eventType);
}
