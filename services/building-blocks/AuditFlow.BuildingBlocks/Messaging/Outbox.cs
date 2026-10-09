using System.Text.Json;
using AuditFlow.BuildingBlocks.Data;
using AuditFlow.BuildingBlocks.Eventing;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        INSERT INTO dbo.OutboxMessage (Id, EventType, EngagementId, Envelope, CorrelationId, OccurredAt)
        VALUES (@Id, @EventType, @EngagementId, @Envelope, @CorrelationId, @OccurredAt)
        """;

    public Task EnqueueAsync<TPayload>(IUnitOfWork uow, EventEnvelope<TPayload> envelope, CancellationToken cancellationToken)
        => uow.ExecuteAsync(InsertSql, new
        {
            Id = envelope.EventId,
            envelope.EventType,
            envelope.EngagementId,
            Envelope = JsonSerializer.Serialize(envelope, JsonDefaults.Web),
            envelope.CorrelationId,
            envelope.OccurredAt
        }, cancellationToken);
}

/// <summary>
/// Polls unpublished outbox rows in order and publishes them through <see cref="IEventBus"/>.
/// At-least-once: a crash between publish and mark causes a re-publish, which consumers de-duplicate by message id.
/// </summary>
public sealed class OutboxDispatcher(
    IUnitOfWorkFactory uowFactory,
    IEventBus bus,
    IConfiguration configuration,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private const int MaxAttempts = 10;
    private const int BatchSize = 50;

    private const string ClaimSql = """
        SELECT TOP (@Batch) Id, Envelope, Attempts
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

        var published = 0;
        foreach (var row in rows)
        {
            try
            {
                var envelope = JsonSerializer.Deserialize<EventEnvelope<JsonElement>>(row.Envelope, JsonDefaults.Web)!;
                await bus.PublishAsync(envelope, ct);
                await uow.ExecuteAsync("UPDATE dbo.OutboxMessage SET PublishedAt = SYSDATETIMEOFFSET() WHERE Id = @Id", new { row.Id }, ct);
                published++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Publishing outbox message {Id} failed (attempt {Attempt})", row.Id, row.Attempts + 1);
                await uow.ExecuteAsync(
                    "UPDATE dbo.OutboxMessage SET Attempts = Attempts + 1, LastError = LEFT(@Error, 1000) WHERE Id = @Id",
                    new { row.Id, Error = ex.Message }, ct);
                break; // keep ordering: retry this row first on the next poll
            }
        }

        await uow.CommitAsync(ct);
        return published;
    }

    private sealed record OutboxRow(Guid Id, string Envelope, int Attempts);
}
