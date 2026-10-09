using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AuditFlow.BuildingBlocks.Correlation;

/// <summary>
/// Reads or creates X-Correlation-Id, echoes it on the response, and flows it into the log scope,
/// the current trace span and the ambient <see cref="CorrelationContext"/>.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[CorrelationHeaders.CorrelationId].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            correlationId = Guid.NewGuid().ToString("N");
            // Written back so a reverse proxy forwards the generated id downstream.
            context.Request.Headers[CorrelationHeaders.CorrelationId] = correlationId;
        }

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[CorrelationHeaders.CorrelationId] = correlationId;
            return Task.CompletedTask;
        });

        Activity.Current?.SetTag("auditflow.correlation_id", correlationId);

        using var correlationScope = CorrelationContext.Begin(correlationId);
        using var logScope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        await next(context);
    }
}

/// <summary>Copies the ambient correlation id onto outgoing HttpClient calls.</summary>
public sealed class CorrelationIdHandler(ICorrelationContext correlation) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(correlation.CorrelationId) && !request.Headers.Contains(CorrelationHeaders.CorrelationId))
            request.Headers.TryAddWithoutValidation(CorrelationHeaders.CorrelationId, correlation.CorrelationId);

        return base.SendAsync(request, cancellationToken);
    }
}
