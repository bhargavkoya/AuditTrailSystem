using AuditFlow.BuildingBlocks.Access;
using AuditFlow.Contracts.Api;
using AuditFlow.Notification.Api.Sse;

namespace AuditFlow.Notification.Api.Endpoints;

public static class StreamEndpoints
{
    public static void MapStreamEndpoints(this WebApplication app)
    {
        // One long-lived stream per engagement being viewed. The caller's capabilities (from engagement-service) are
        // fixed at connect time and decide which events are forwarded.
        app.MapGet("/engagements/{id}/events/stream", async (
            string id, HttpContext http, IEngagementAccessClient access, SseSession session, TimeProvider clock, CancellationToken ct) =>
        {
            var granted = await access.RequireAsync(id, Capabilities.View, ct);

            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();

            await session.RunAsync(id, granted.Capabilities, ParseLastEventId(http), TokenExpiry(http, clock), http.Response.Body, ct);
        }).RequireAuthorization().WithTags("Stream");
    }

    // EventSource sends Last-Event-ID on reconnect; fetch-based clients can send the header or ?lastEventId=.
    private static long? ParseLastEventId(HttpContext http)
    {
        var raw = http.Request.Headers["Last-Event-ID"].ToString();
        if (string.IsNullOrWhiteSpace(raw)) raw = http.Request.Query["lastEventId"].ToString();
        return long.TryParse(raw, out var id) && id >= 0 ? id : null;
    }

    private static DateTimeOffset TokenExpiry(HttpContext http, TimeProvider clock)
        => long.TryParse(http.User.FindFirst("exp")?.Value, out var exp)
            ? DateTimeOffset.FromUnixTimeSeconds(exp)
            : clock.GetUtcNow().AddMinutes(60);
}
