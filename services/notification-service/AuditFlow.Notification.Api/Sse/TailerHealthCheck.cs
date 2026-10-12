using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AuditFlow.Notification.Api.Sse;

/// <summary>Readiness: the tailer is still polling the event log. A stuck tailer means browsers silently stop getting events.</summary>
public sealed class TailerHealthCheck(EventLogTailer tailer, TimeProvider clock) : IHealthCheck
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(10);

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var age = clock.GetUtcNow() - tailer.LastPollAt;
        return Task.FromResult(age > MaxAge
            ? HealthCheckResult.Unhealthy($"Event log tailer has not polled for {age.TotalSeconds:F0}s")
            : HealthCheckResult.Healthy());
    }
}
