using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AuditFlow.BuildingBlocks.Telemetry;

/// <summary>Tags the request span and the log scope with the engagement and the caller, so a trace or a log search can use either.</summary>
public sealed class RequestSpanTagsMiddleware(RequestDelegate next, ILogger<RequestSpanTagsMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var values = context.Request.RouteValues;
        var engagementId = values.TryGetValue("id", out var id) && id is string s && s.StartsWith("ENG-", StringComparison.Ordinal)
            ? s
            : values.TryGetValue("engagementId", out var eid) ? eid as string : null;
        var actor = context.User.FindFirstValue("sub") ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier);

        var activity = Activity.Current;
        if (engagementId is not null) activity?.SetTag("auditflow.engagement_id", engagementId);
        if (actor is not null) activity?.SetTag("auditflow.actor_id", actor);

        using var scope = engagementId is null ? null : logger.BeginScope(new Dictionary<string, object> { ["EngagementId"] = engagementId });
        await next(context);
    }
}
