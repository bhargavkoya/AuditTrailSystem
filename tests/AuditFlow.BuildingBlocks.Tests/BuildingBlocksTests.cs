using AuditFlow.BuildingBlocks.Correlation;
using AuditFlow.BuildingBlocks.Data;
using AuditFlow.BuildingBlocks.Errors;
using AuditFlow.BuildingBlocks.Eventing;
using AuditFlow.BuildingBlocks.Messaging;
using AuditFlow.BuildingBlocks.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AuditFlow.BuildingBlocks.Tests;

public class EventEnvelopeTests
{
    public sealed record Ping(string Text);

    [Fact]
    public void Envelope_picks_up_the_ambient_correlation_id()
    {
        using var _ = CorrelationContext.Begin("corr-123");

        var envelope = EventEnvelope<Ping>.Create("Ping", "ENG-1", new Ping("hi"));

        Assert.Equal("corr-123", envelope.CorrelationId);
        Assert.Equal(1, envelope.SchemaVersion);
    }

    [Fact]
    public async Task InMemoryBus_records_published_envelopes()
    {
        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);

        await bus.PublishAsync(EventEnvelope<Ping>.Create("Ping", "ENG-1", new Ping("hi")));

        Assert.Single(bus.Published);
    }
}

public class EventDispatcherTests
{
    public sealed record Ping(string Text);

    private sealed class Fixture
    {
        public Mock<IUnitOfWork> Uow { get; } = new();
        public Mock<IUnitOfWorkFactory> UowFactory { get; } = new();
        public Mock<IProcessedMessageStore> Store { get; } = new();
        public List<string> Handled { get; } = [];
        public EventDispatcher Dispatcher { get; }

        public Fixture(bool handlerThrows = false)
        {
            UowFactory.Setup(f => f.BeginAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Uow.Object);
            Uow.Setup(u => u.DisposeAsync()).Returns(ValueTask.CompletedTask);

            var registration = new EventHandlerRegistration("Ping", (_, json, _, _) =>
            {
                if (handlerThrows) throw new InvalidOperationException("boom");
                Handled.Add(json);
                return Task.CompletedTask;
            });
            var registry = new EventHandlerRegistry([registration]);
            var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
            Dispatcher = new EventDispatcher(UowFactory.Object, Store.Object, registry, scopes, NullLogger<EventDispatcher>.Instance);
        }
    }

    [Fact]
    public async Task First_delivery_runs_handlers_and_commits()
    {
        var f = new Fixture();
        var id = Guid.NewGuid();
        f.Store.Setup(s => s.TryMarkAsync("svc", id, f.Uow.Object, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var handled = await f.Dispatcher.DispatchAsync("svc", id, "Ping", "{}", "corr", CancellationToken.None);

        Assert.True(handled);
        Assert.Single(f.Handled);
        f.Uow.Verify(u => u.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Duplicate_delivery_is_skipped_without_running_handlers_or_committing()
    {
        var f = new Fixture();
        var id = Guid.NewGuid();
        f.Store.Setup(s => s.TryMarkAsync("svc", id, f.Uow.Object, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var handled = await f.Dispatcher.DispatchAsync("svc", id, "Ping", "{}", null, CancellationToken.None);

        Assert.False(handled);
        Assert.Empty(f.Handled);
        f.Uow.Verify(u => u.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handler_failure_does_not_commit_so_the_idempotency_marker_rolls_back()
    {
        var f = new Fixture(handlerThrows: true);
        var id = Guid.NewGuid();
        f.Store.Setup(s => s.TryMarkAsync("svc", id, f.Uow.Object, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            f.Dispatcher.DispatchAsync("svc", id, "Ping", "{}", null, CancellationToken.None));

        f.Uow.Verify(u => u.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Unregistered_event_types_are_ignored()
    {
        var f = new Fixture();

        var handled = await f.Dispatcher.DispatchAsync("svc", Guid.NewGuid(), "Unknown", "{}", null, CancellationToken.None);

        Assert.False(handled);
        f.UowFactory.Verify(u => u.BeginAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}

public class RowVersionTests
{
    [Fact]
    public void Token_round_trips()
    {
        byte[] version = [0, 0, 0, 0, 0, 0, 7, 209];

        var token = RowVersion.ToToken(version);

        Assert.Equal(version, RowVersion.FromToken(token));
        Assert.Equal(version, RowVersion.FromToken(RowVersion.ToETag(token)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64 !!")]
    public void Missing_or_malformed_token_is_a_validation_error(string? token)
    {
        Assert.Throws<ValidationFailedException>(() => RowVersion.FromToken(token));
    }
}

public class CorrelationTests
{
    [Fact]
    public async Task Middleware_generates_an_id_when_missing_and_exposes_it_downstream()
    {
        string? seenInside = null;
        var middleware = new CorrelationIdMiddleware(_ =>
        {
            seenInside = new CorrelationContext().CorrelationId;
            return Task.CompletedTask;
        }, NullLogger<CorrelationIdMiddleware>.Instance);
        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context);

        Assert.False(string.IsNullOrEmpty(seenInside));
        Assert.Equal(seenInside, context.Request.Headers[CorrelationHeaders.CorrelationId].ToString());
    }

    [Fact]
    public async Task Middleware_preserves_an_incoming_id()
    {
        string? seenInside = null;
        var middleware = new CorrelationIdMiddleware(_ =>
        {
            seenInside = new CorrelationContext().CorrelationId;
            return Task.CompletedTask;
        }, NullLogger<CorrelationIdMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationHeaders.CorrelationId] = "abc";

        await middleware.InvokeAsync(context);

        Assert.Equal("abc", seenInside);
    }

    [Fact]
    public async Task Handler_forwards_the_ambient_id_on_outgoing_calls()
    {
        HttpRequestMessage? sent = null;
        var inner = new StubHandler(r => sent = r);
        var correlation = new Mock<ICorrelationContext>();
        correlation.SetupGet(c => c.CorrelationId).Returns("corr-9");
        using var client = new HttpClient(new CorrelationIdHandler(correlation.Object) { InnerHandler = inner });

        await client.GetAsync("http://example.test/");

        Assert.Equal("corr-9", sent!.Headers.GetValues(CorrelationHeaders.CorrelationId).Single());
    }

    private sealed class StubHandler(Action<HttpRequestMessage> capture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            capture(request);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
