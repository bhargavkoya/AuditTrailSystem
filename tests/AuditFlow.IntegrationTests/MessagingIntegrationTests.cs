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

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    private static OutboxDispatcher NewDispatcher(TestDatabase db, IEventBus bus, TimeProvider? clock = null)
        => new(db.UnitOfWork, bus, new ConfigurationBuilder().Build(), clock ?? TimeProvider.System, NullLogger<OutboxDispatcher>.Instance);

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

    [Fact]
    public async Task Failing_event_backs_off_and_newer_events_cannot_overtake_it()
    {
        var db = await sql.CreateDatabaseAsync();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var delivered = new List<string>();
        var busDown = true;
        var bus = new Mock<IEventBus>();
        bus.Setup(b => b.PublishAsync(It.IsAny<EventEnvelope<System.Text.Json.JsonElement>>(), It.IsAny<CancellationToken>()))
            .Returns<EventEnvelope<System.Text.Json.JsonElement>, CancellationToken>((e, _) =>
            {
                if (busDown) throw new InvalidOperationException("bus down");
                delivered.Add(e.Payload.GetProperty("text").GetString()!);
                return Task.CompletedTask;
            });
        await EnqueueAsync(db, EventEnvelope<Ping>.Create("Ping", "ENG-1", new Ping("first")), commit: true);
        await EnqueueAsync(db, EventEnvelope<Ping>.Create("Ping", "ENG-1", new Ping("second")), commit: true);
        var dispatcher = NewDispatcher(db, bus.Object, clock);

        Assert.Equal(0, await dispatcher.DispatchBatchAsync(default));                 // attempt 1 fails
        Assert.Equal(0, await dispatcher.DispatchBatchAsync(default));                 // still backing off: bus not even called
        bus.Verify(b => b.PublishAsync(It.IsAny<EventEnvelope<System.Text.Json.JsonElement>>(), It.IsAny<CancellationToken>()), Times.Once);

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(0, await dispatcher.DispatchBatchAsync(default));                 // attempt 2 fails, backoff doubles
        Assert.Equal(2, await db.ScalarAsync<int>("SELECT Attempts FROM dbo.OutboxMessage WHERE LastError IS NOT NULL"));

        busDown = false;
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(2, await dispatcher.DispatchBatchAsync(default));                 // recovers, both published
        Assert.Equal(["first", "second"], delivered);                                  // in the original order
    }

    [Fact]
    public async Task Backoff_is_capped_so_a_long_outage_never_strands_an_event()
    {
        var db = await sql.CreateDatabaseAsync();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var bus = new Mock<IEventBus>();
        bus.Setup(b => b.PublishAsync(It.IsAny<EventEnvelope<System.Text.Json.JsonElement>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("bus down"));
        await EnqueueAsync(db, EventEnvelope<Ping>.Create("Ping", "ENG-1", new Ping("x")), commit: true);
        var dispatcher = NewDispatcher(db, bus.Object, clock);

        for (var i = 0; i < 20; i++)
        {
            await dispatcher.DispatchBatchAsync(default);
            clock.Advance(TimeSpan.FromSeconds(31));                                   // always past the capped backoff
        }

        Assert.Equal(20, await db.ScalarAsync<int>("SELECT Attempts FROM dbo.OutboxMessage"));
        Assert.True(20 < OutboxDispatcher.MaxAttempts);                                // still being retried
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
