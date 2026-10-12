using System.Diagnostics.Metrics;
using AuditFlow.BuildingBlocks.Data;
using AuditFlow.BuildingBlocks.Errors;
using AuditFlow.BuildingBlocks.Messaging;
using AuditFlow.BuildingBlocks.Telemetry;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AuditFlow.BuildingBlocks.Tests;

/// <summary>Collects measurements from the AuditFlow meter. The meter is static, so tests filter by a unique tag value.</summary>
internal sealed class MeterCapture : IDisposable
{
    private readonly MeterListener listener = new();
    private readonly List<(string Instrument, long Value, Dictionary<string, object?> Tags)> items = [];

    public MeterCapture()
    {
        listener.InstrumentPublished = (i, l) =>
        {
            if (i.Meter.Name == AuditFlowMetrics.MeterName) l.EnableMeasurementEvents(i);
        };
        listener.SetMeasurementEventCallback<long>((i, v, tags, _) =>
        {
            lock (items) items.Add((i.Name, v, tags.ToArray().ToDictionary(t => t.Key, t => t.Value)));
        });
        listener.Start();
    }

    public long Sum(string instrument, string tagKey, string tagValue)
    {
        lock (items)
            return items.Where(m => m.Instrument == instrument && Equals(m.Tags.GetValueOrDefault(tagKey), tagValue)).Sum(m => m.Value);
    }

    public long Sum(string instrument)
    {
        lock (items) return items.Where(m => m.Instrument == instrument).Sum(m => m.Value);
    }

    public void Dispose() => listener.Dispose();
}

public class ConsumerMetricsTests
{
    private static (EventDispatcher Dispatcher, Mock<IProcessedMessageStore> Store, Mock<IUnitOfWork> Uow) Build(string eventType, bool throws)
    {
        var uow = new Mock<IUnitOfWork>();
        uow.Setup(u => u.DisposeAsync()).Returns(ValueTask.CompletedTask);
        var factory = new Mock<IUnitOfWorkFactory>();
        factory.Setup(f => f.BeginAsync(It.IsAny<CancellationToken>())).ReturnsAsync(uow.Object);
        var registry = new EventHandlerRegistry([new EventHandlerRegistration(eventType, (_, _, _, _) =>
            throws ? throw new InvalidOperationException("boom") : Task.CompletedTask)]);
        var store = new Mock<IProcessedMessageStore>();
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return (new EventDispatcher(factory.Object, store.Object, registry, scopes, NullLogger<EventDispatcher>.Instance), store, uow);
    }

    [Fact]
    public async Task Successful_delivery_counts_as_consumed()
    {
        using var capture = new MeterCapture();
        var (dispatcher, store, _) = Build("MetricsOk", throws: false);
        store.Setup(s => s.TryMarkAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<IUnitOfWork>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await dispatcher.DispatchAsync("svc", Guid.NewGuid(), "MetricsOk", "{}", null, CancellationToken.None);

        Assert.Equal(1, capture.Sum("auditflow.events.consumed", "event_type", "MetricsOk"));
        Assert.Equal(0, capture.Sum("auditflow.events.failed", "event_type", "MetricsOk"));
    }

    [Fact]
    public async Task Duplicate_delivery_counts_as_duplicate_not_consumed()
    {
        using var capture = new MeterCapture();
        var (dispatcher, store, _) = Build("MetricsDup", throws: false);
        store.Setup(s => s.TryMarkAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<IUnitOfWork>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await dispatcher.DispatchAsync("svc", Guid.NewGuid(), "MetricsDup", "{}", null, CancellationToken.None);

        Assert.Equal(1, capture.Sum("auditflow.events.duplicates", "event_type", "MetricsDup"));
        Assert.Equal(0, capture.Sum("auditflow.events.consumed", "event_type", "MetricsDup"));
    }

    [Fact]
    public async Task Handler_failure_counts_as_failed_not_consumed()
    {
        using var capture = new MeterCapture();
        var (dispatcher, store, _) = Build("MetricsFail", throws: true);
        store.Setup(s => s.TryMarkAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<IUnitOfWork>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dispatcher.DispatchAsync("svc", Guid.NewGuid(), "MetricsFail", "{}", null, CancellationToken.None));

        Assert.Equal(1, capture.Sum("auditflow.events.failed", "event_type", "MetricsFail"));
        Assert.Equal(0, capture.Sum("auditflow.events.consumed", "event_type", "MetricsFail"));
    }
}

public class ConflictMetricsTests
{
    private static Task Handle(Exception ex)
    {
        var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        return new AuditFlowExceptionHandler(NullLogger<AuditFlowExceptionHandler>.Instance)
            .TryHandleAsync(context, ex, CancellationToken.None).AsTask();
    }

    [Fact]
    public async Task Row_version_conflict_is_counted_by_resource()
    {
        using var capture = new MeterCapture();

        await Handle(new ConcurrencyException("v2", "metrics-section-key"));

        Assert.Equal(1, capture.Sum("auditflow.concurrency.conflicts", "resource", "metrics-section-key"));
    }

    [Fact]
    public async Task Blocked_submit_is_counted()
    {
        using var capture = new MeterCapture();

        await Handle(new SubmissionBlockedException([], "not ready"));

        Assert.True(capture.Sum("auditflow.validation.blocked_submits") >= 1);
    }

    [Fact]
    public void Lifecycle_transition_is_tagged_with_from_and_to()
    {
        using var capture = new MeterCapture();

        AuditFlowMetrics.RecordTransition("MetricsFrom", "MetricsTo");

        Assert.Equal(1, capture.Sum("auditflow.lifecycle.transitions", "from", "MetricsFrom"));
        Assert.Equal(1, capture.Sum("auditflow.lifecycle.transitions", "to", "MetricsTo"));
    }
}
