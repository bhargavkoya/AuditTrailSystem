using AuditFlow.BuildingBlocks.Data;
using AuditFlow.Notification.Api.Sse;

namespace AuditFlow.Notification.Api.Data;

public interface IEventLogReader
{
    /// <summary>Committed rows with <c>Seq &gt; afterSeq</c>, oldest first, optionally for one engagement.</summary>
    Task<IReadOnlyList<SseFrame>> ReadAfterAsync(string? engagementId, long afterSeq, int take, CancellationToken ct);

    /// <summary>The highest sequence number in the log (0 when empty).</summary>
    Task<long> MaxSeqAsync(CancellationToken ct);
}

public sealed class EventLogReader(IDbConnectionFactory connections) : IEventLogReader
{
    public async Task<IReadOnlyList<SseFrame>> ReadAfterAsync(string? engagementId, long afterSeq, int take, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return (await connection.QueryAsync<SseFrame>("""
            SELECT TOP (@take) Seq, EventType, EngagementId, Envelope
            FROM dbo.DomainEventLog
            WHERE Seq > @afterSeq AND (@engagementId IS NULL OR EngagementId = @engagementId)
            ORDER BY Seq
            """, new { take = Math.Clamp(take, 1, 1000), afterSeq, engagementId }, ct)).ToList();
    }

    public async Task<long> MaxSeqAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<long?>("SELECT MAX(Seq) FROM dbo.DomainEventLog", null, ct) ?? 0;
    }
}
