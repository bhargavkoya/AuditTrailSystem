using System.Text.Json;
using AuditFlow.BuildingBlocks.Eventing;
using AuditFlow.BuildingBlocks.Messaging;
using AuditFlow.Contracts.Events;
using AuditFlow.Notification.Api.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AuditFlow.IntegrationTests;

[Collection(SqlCollection.Name)]
[Trait("Category", "Integration")]
public class NotificationIntegrationTests(SqlServerFixture sql)
{
    private static EventDispatcher Consumer(TestDatabase db)
    {
        var services = new ServiceCollection();
        foreach (var type in EventTypes.All)
            services.AddEventHandler<JsonElement, EventLogHandler>(type);
        var provider = services.BuildServiceProvider();
        return new EventDispatcher(db.UnitOfWork, new SqlProcessedMessageStore(),
            new EventHandlerRegistry(provider.GetServices<EventHandlerRegistration>()),
            provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<EventDispatcher>.Instance);
    }

    private static string Json(EventEnvelope<object> e) => JsonSerializer.Serialize(e, JsonDefaults.Web);

    [Fact]
    public async Task Every_event_type_is_logged_once_even_when_delivered_twice()
    {
        var db = await sql.CreateDatabaseAsync(typeof(EventLogHandler).Assembly);
        var consumer = Consumer(db);

        foreach (var type in EventTypes.All)
        {
            var envelope = EventEnvelope<object>.Create(type, "ENG-1", new { note = type }, correlationId: "corr-1");
            var json = Json(envelope);
            Assert.True(await consumer.DispatchAsync("notification", envelope.EventId, type, json, "corr-1", default));
            Assert.False(await consumer.DispatchAsync("notification", envelope.EventId, type, json, "corr-1", default)); // redelivery
        }

        Assert.Equal(EventTypes.All.Count, await db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.DomainEventLog"));
        Assert.Equal(EventTypes.All.Count, await db.ScalarAsync<int>("SELECT COUNT(DISTINCT EventId) FROM dbo.DomainEventLog"));
        Assert.Equal("corr-1", await db.ScalarAsync<string>("SELECT TOP 1 CorrelationId FROM dbo.DomainEventLog"));
    }

    [Fact]
    public async Task Reader_returns_rows_after_a_sequence_oldest_first_and_can_filter_by_engagement()
    {
        var db = await sql.CreateDatabaseAsync(typeof(EventLogHandler).Assembly);
        var consumer = Consumer(db);
        foreach (var (type, eng) in new[] { (EventTypes.EngagementCreated, "ENG-1"), (EventTypes.SectionUpdated, "ENG-2"), (EventTypes.SectionUpdated, "ENG-1") })
        {
            var e = EventEnvelope<object>.Create(type, eng, new { });
            await consumer.DispatchAsync("notification", e.EventId, type, Json(e), null, default);
        }
        var reader = new EventLogReader(db.Connections);

        var all = await reader.ReadAfterAsync(null, 0, 10, default);
        var eng1 = await reader.ReadAfterAsync("ENG-1", 0, 10, default);
        var afterFirst = await reader.ReadAfterAsync(null, all[0].Seq, 10, default);

        Assert.Equal(3, all.Count);
        Assert.True(all[0].Seq < all[1].Seq);
        Assert.Equal(2, eng1.Count);
        Assert.All(eng1, r => Assert.Equal("ENG-1", r.EngagementId));
        Assert.Equal(2, afterFirst.Count);
        Assert.Equal(all[2].Seq, await reader.MaxSeqAsync(default));
        Assert.Contains("\"eventType\":\"SectionUpdated\"", all[2].Envelope);
    }

    [Fact]
    public async Task An_empty_log_has_sequence_zero()
    {
        var db = await sql.CreateDatabaseAsync(typeof(EventLogHandler).Assembly);

        Assert.Equal(0, await new EventLogReader(db.Connections).MaxSeqAsync(default));
    }
}
