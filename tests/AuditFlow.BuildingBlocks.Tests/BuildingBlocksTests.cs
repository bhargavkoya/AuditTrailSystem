using AuditFlow.BuildingBlocks.Correlation;
using AuditFlow.BuildingBlocks.Eventing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AuditFlow.BuildingBlocks.Tests;

public class InMemoryEventBusTests
{
    public sealed record Ping(string Text);

    [Fact]
    public async Task Publish_dispatches_to_registered_handlers_and_records_the_envelope()
    {
        var handler = new Mock<IEventHandler<Ping>>();
        var services = new ServiceCollection().AddSingleton(handler.Object).BuildServiceProvider();
        var bus = new InMemoryEventBus(services);
        var envelope = EventEnvelope<Ping>.Create("Ping", "ENG-1", new Ping("hi"));

        await bus.PublishAsync(envelope);

        handler.Verify(h => h.HandleAsync(envelope, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Single(bus.Published);
    }

    [Fact]
    public async Task Publish_without_handlers_does_not_throw()
    {
        var bus = new InMemoryEventBus(new ServiceCollection().BuildServiceProvider());

        await bus.PublishAsync(EventEnvelope<Ping>.Create("Ping", "ENG-1", new Ping("hi")));

        Assert.Single(bus.Published);
    }

    [Fact]
    public void Envelope_picks_up_the_ambient_correlation_id()
    {
        using var _ = CorrelationContext.Begin("corr-123");

        var envelope = EventEnvelope<Ping>.Create("Ping", "ENG-1", new Ping("hi"));

        Assert.Equal("corr-123", envelope.CorrelationId);
        Assert.Equal(1, envelope.SchemaVersion);
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
