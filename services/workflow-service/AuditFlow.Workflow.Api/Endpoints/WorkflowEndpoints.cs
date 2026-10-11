using AuditFlow.BuildingBlocks.Versioning;
using AuditFlow.Contracts.Api;
using AuditFlow.Workflow.Api.Domain;

namespace AuditFlow.Workflow.Api.Endpoints;

public static class WorkflowEndpoints
{
    public static void MapWorkflowEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/engagements/{id}").WithTags("Workflow").RequireAuthorization();

        group.MapGet("/tabs", (string id, WorkflowService service, CancellationToken ct) => service.GetTabsAsync(id, ct));

        group.MapGet("/tabs/{tabNo:int}", (string id, int tabNo, WorkflowService service, CancellationToken ct) => service.GetTabAsync(id, tabNo, ct));

        // Atomic batch: each section carries the version it was loaded with; one stale section rejects the whole request.
        group.MapPatch("/tabs/{tabNo:int}", async (string id, int tabNo, PatchTabRequest request, WorkflowService service, CancellationToken ct) =>
        {
            var patches = (request.Sections ?? []).Select(s => new SectionPatch(s.SectionKey, s.Version, s.Fields)).ToList();
            return Results.Ok(await service.SaveTabAsync(id, tabNo, patches, ct));
        });

        // Single section: the version is the If-Match header (ETag of the last read). 409 carries the current version.
        group.MapPatch("/sections/{sectionKey}", async (
            string id, string sectionKey, PatchSectionRequest request, HttpContext http, WorkflowService service, CancellationToken ct) =>
        {
            var result = await service.SaveSectionAsync(id, sectionKey, http.Request.GetIfMatchToken(), request.Fields, ct);
            http.Response.Headers.ETag = RowVersion.ToETag(result.Section.Version);
            return Results.Ok(result);
        });

        group.MapGet("/validation-summary", (string id, WorkflowService service, CancellationToken ct) => service.GetValidationSummaryAsync(id, ct));

        // Internal: not routed by the gateway.
        var internalGroup = app.MapGroup("/internal/engagements/{id}").WithTags("Internal").RequireAuthorization();
        internalGroup.MapGet("/submission-readiness", (string id, WorkflowService service, CancellationToken ct) => service.GetSubmissionReadinessAsync(id, ct));
        internalGroup.MapGet("/sections/{sectionKey}", (string id, string sectionKey, WorkflowService service, CancellationToken ct) => service.GetSectionPolicyAsync(id, sectionKey, ct));
    }
}
