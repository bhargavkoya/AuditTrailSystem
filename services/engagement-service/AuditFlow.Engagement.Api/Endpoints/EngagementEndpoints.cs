using AuditFlow.BuildingBlocks.Versioning;
using AuditFlow.Contracts.Api;
using AuditFlow.Engagement.Api.Data;
using AuditFlow.Engagement.Api.Domain;

namespace AuditFlow.Engagement.Api.Endpoints;

public static class EngagementEndpoints
{
    public static void MapEngagementEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/engagements").WithTags("Engagements").RequireAuthorization();

        // Literal route; declared so it is never captured by /{id}.
        group.MapGet("/creation-options", (EngagementService service) => service.GetCreationOptions());

        group.MapPost("/", async (CreateEngagementRequest request, EngagementService service, HttpContext http, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(request, ct);
            http.Response.Headers.ETag = RowVersion.ToETag(created.Version);
            return Results.Created($"/engagements/{created.EngagementId}", created);
        });

        // scope: owned | assigned | reviewing | participating (default); status: any lifecycle status. Case-insensitive.
        group.MapGet("/", (EngagementService service, string? scope, string? status, string? q, int? page, int? pageSize, CancellationToken ct)
            => service.ListAsync(
                ParseOrDefault(scope, "scope", ListScope.Participating),
                string.IsNullOrWhiteSpace(status) ? null : ParseOrDefault<EngagementStatus>(status, "status", default),
                q, page ?? 1, pageSize ?? 20, ct));

        group.MapGet("/{id}", async (string id, EngagementService service, HttpContext http, CancellationToken ct) =>
        {
            var engagement = await service.GetAsync(id, ct);
            http.Response.Headers.ETag = RowVersion.ToETag(engagement.Version);
            return Results.Ok(engagement);
        });

        group.MapGet("/{id}/participants", (string id, EngagementService service, CancellationToken ct)
            => service.GetParticipantsAsync(id, ct));

        // Internal: not routed by the gateway. Other services ask "what may this caller do here right now?".
        app.MapGet("/internal/engagements/{id}/access", (string id, EngagementService service, CancellationToken ct)
            => service.GetAccessAsync(id, ct))
            .RequireAuthorization().WithTags("Internal");
    }

    private static T ParseOrDefault<T>(string? value, string field, T fallback) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return Enum.TryParse<T>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new AuditFlow.BuildingBlocks.Errors.ValidationFailedException(field, $"Unknown {field} '{value}'.");
    }
}
