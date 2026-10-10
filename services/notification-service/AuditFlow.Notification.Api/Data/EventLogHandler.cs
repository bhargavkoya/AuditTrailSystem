using System.Text.Json;
using AuditFlow.BuildingBlocks.Data;
using AuditFlow.BuildingBlocks.Eventing;

namespace AuditFlow.Notification.Api.Data;

/// <summary>
/// Persists every domain event it receives. Runs inside the idempotent consumer transaction, so a redelivered
/// message cannot create a second row (the EventId unique index is a second line of defence).
/// </summary>
public sealed class EventLogHandler : IEventHandler<JsonElement>
{
    private const string InsertSql = """
        INSERT INTO dbo.DomainEventLog (EventId, EventType, EngagementId, Envelope, CorrelationId, OccurredAt)
        VALUES (@EventId, @EventType, @EngagementId, @Envelope, @CorrelationId, @OccurredAt)
        """;

    public Task HandleAsync(EventEnvelope<JsonElement> envelope, IUnitOfWork uow, CancellationToken cancellationToken)
        => uow.ExecuteAsync(InsertSql, new
        {
            envelope.EventId,
            envelope.EventType,
            envelope.EngagementId,
            Envelope = JsonSerializer.Serialize(envelope, JsonDefaults.Web),
            envelope.CorrelationId,
            envelope.OccurredAt
        }, cancellationToken);
}

public sealed record EventLogRow(long Seq, string EventType, string EngagementId, string CorrelationId, DateTimeOffset OccurredAt, DateTimeOffset ReceivedAt);

public interface IEventLogReader
{
    Task<IReadOnlyList<EventLogRow>> RecentAsync(string? engagementId, int take, CancellationToken ct);
}

public sealed class EventLogReader(IDbConnectionFactory connections) : IEventLogReader
{
    public async Task<IReadOnlyList<EventLogRow>> RecentAsync(string? engagementId, int take, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return (await connection.QueryAsync<EventLogRow>("""
            SELECT TOP (@take) Seq, EventType, EngagementId, ISNULL(CorrelationId, N'') AS CorrelationId, OccurredAt, ReceivedAt
            FROM dbo.DomainEventLog
            WHERE (@engagementId IS NULL OR EngagementId = @engagementId)
            ORDER BY Seq DESC
            """, new { take = Math.Clamp(take, 1, 200), engagementId }, ct)).ToList();
    }
}
