using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace AuditFlow.BuildingBlocks.Telemetry;

/// <summary>Tags the request span with the engagement and the caller so a trace can be found by either.</summary>
public sealed class RequestSpanTagsMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        var activity = Activity.Current;
        if (activity is not null)
        {
            var values = context.Request.RouteValues;
            var engagementId = values.TryGetValue("id", out var id) && id is string s && s.StartsWith("ENG-", StringComparison.Ordinal)
                ? s
                : values.TryGetValue("engagementId", out var eid) ? eid as string : null;
            if (engagementId is not null) activity.SetTag("auditflow.engagement_id", engagementId);

            var actor = context.User.FindFirstValue("sub") ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (actor is not null) activity.SetTag("auditflow.actor_id", actor);
        }
        return next(context);
    }
}
