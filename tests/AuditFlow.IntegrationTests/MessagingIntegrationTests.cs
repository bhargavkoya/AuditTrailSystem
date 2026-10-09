using AuditFlow.BuildingBlocks.Data;
using AuditFlow.BuildingBlocks.Eventing;
using AuditFlow.BuildingBlocks.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AuditFlow.IntegrationTests;

[Collection(SqlCollection.Name)]
[Trait("Category", "Integration")]
public class MessagingIntegrationTests(SqlServerFixture sql)
{
    public sealed record Ping(string Text);

    private static OutboxDispatcher NewDispatcher(TestDatabase db, IEventBus bus)
        => new(db.UnitOfWork, bus, new ConfigurationBuilder().Build(), NullLogger<OutboxDispatcher>.Instance);

    private static async Task EnqueueAsync(TestDatabase db, EventEnvelope<Ping> envelope, bool commit)
    {
        await using var uow = await db.UnitOfWork.BeginAsync(CancellationToken.None);
        await new SqlOutbox().EnqueueAsync(uow, envelope, CancellationToken.None);
        if (commit) await uow.CommitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Outbox_row_is_written_atomically_with_the_transaction()
    {
        var db = await sql.CreateDatabaseAsync();

        await EnqueueAsync(db, EventEnvelope<Ping>.Create("Ping", "ENG-1", new Ping("rolled back")), commit: false);
        await EnqueueAsync(db, EventEnvelope<Ping>.Create("Ping", "ENG-1", new Ping("committed")), commit: true);

        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.OutboxMessage"));
    }

    [Fact]
    public async Task Dispatcher_publishes_committed_events_in_order_and_marks_them_published()
    {
        var db = await sql.CreateDatabaseAsync();
        var published = new List<string>();
        var bus = new Mock<IEventBus>();
        bus.Setup(b => b.PublishAsync(It.IsAny<EventEnvelope<System.Text.Json.JsonElement>>(), It.IsAny<CancellationToken>()))
            .Callback<EventEnvelope<System.Text.Json.JsonElement>, CancellationToken>((e, _) => published.Add(e.Payload.GetProperty("text").GetString()!))
            .Returns(Task.CompletedTask);
        await EnqueueAsync(db, EventEnvelope<Ping>.Create("Ping", "ENG-1", new Ping("first")), commit: true);
        await EnqueueAsync(db, EventEnvelope<Ping>.Create("Ping", "ENG-1", new Ping("second")), commit: true);

        var count = await NewDispatcher(db, bus.Object).DispatchBatchAsync(CancellationToken.None);

        Assert.Equal(2, count);
        Assert.Equal(["first", "second"], published);
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.OutboxMessage WHERE PublishedAt IS NULL"));
        Assert.Equal(0, await NewDispatcher(db, bus.Object).DispatchBatchAsync(CancellationToken.None)); // nothing re-published
    }

    [Fact]
    public async Task Failed_publish_leaves_the_event_unpublished_and_counts_the_attempt()
    {
        var db = await sql.CreateDatabaseAsync();
        var bus = new Mock<IEventBus>();
        bus.Setup(b => b.PublishAsync(It.IsAny<EventEnvelope<System.Text.Json.JsonElement>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("bus down"));
        await EnqueueAsync(db, EventEnvelope<Ping>.Create("Ping", "ENG-1", new Ping("x")), commit: true);

        var count = await NewDispatcher(db, bus.Object).DispatchBatchAsync(CancellationToken.None);

        Assert.Equal(0, count);
        Assert.Equal(1, await db.ScalarAsync<int>("SELECT Attempts FROM dbo.OutboxMessage"));
        Assert.Equal("bus down", await db.ScalarAsync<string>("SELECT LastError FROM dbo.OutboxMessage"));
        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.OutboxMessage WHERE PublishedAt IS NULL"));
    }

    private sealed class InsertRowHandler(bool fail) : IEventHandler<Ping>
    {
        public async Task HandleAsync(EventEnvelope<Ping> envelope, IUnitOfWork uow, CancellationToken ct)
        {
            await uow.ExecuteAsync("INSERT INTO dbo.Handled (Text) VALUES (@Text)", new { envelope.Payload.Text }, ct);
            if (fail) throw new InvalidOperationException("handler failed");
        }
    }

    private static EventDispatcher NewConsumer(TestDatabase db, bool failHandler)
    {
        var services = new ServiceCollection();
        services.AddEventHandler<Ping, InsertRowHandler>("Ping");
        services.AddScoped(_ => new InsertRowHandler(failHandler));
        var provider = services.BuildServiceProvider();
        return new EventDispatcher(db.UnitOfWork, new SqlProcessedMessageStore(),
            new EventHandlerRegistry(provider.GetServices<EventHandlerRegistration>()),
            provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<EventDispatcher>.Instance);
    }

    [Fact]
    public async Task Duplicate_delivery_is_processed_exactly_once()
    {
        var db = await sql.CreateDatabaseAsync();
        await db.ExecuteAsync("CREATE TABLE dbo.Handled (Text NVARCHAR(50))");
        var envelope = EventEnvelope<Ping>.Create("Ping", "ENG-1", new Ping("once"));
        var json = System.Text.Json.JsonSerializer.Serialize(envelope, JsonDefaults.Web);
        var consumer = NewConsumer(db, failHandler: false);

        var first = await consumer.DispatchAsync("svc", envelope.EventId, "Ping", json, null, CancellationToken.None);
        var second = await consumer.DispatchAsync("svc", envelope.EventId, "Ping", json, null, CancellationToken.None);

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Handled"));
    }

    [Fact]
    public async Task Handler_failure_rolls_back_work_and_marker_so_a_retry_succeeds()
    {
        var db = await sql.CreateDatabaseAsync();
        await db.ExecuteAsync("CREATE TABLE dbo.Handled (Text NVARCHAR(50))");
        var envelope = EventEnvelope<Ping>.Create("Ping", "ENG-1", new Ping("retry"));
        var json = System.Text.Json.JsonSerializer.Serialize(envelope, JsonDefaults.Web);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewConsumer(db, failHandler: true).DispatchAsync("svc", envelope.EventId, "Ping", json, null, CancellationToken.None));
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Handled"));
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ProcessedMessage"));

        var retried = await NewConsumer(db, failHandler: false).DispatchAsync("svc", envelope.EventId, "Ping", json, null, CancellationToken.None);

        Assert.True(retried);
        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Handled"));
    }
}
