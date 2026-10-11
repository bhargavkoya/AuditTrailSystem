using AuditFlow.Attachment.Api.Domain;
using AuditFlow.Contracts.Api;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;

namespace AuditFlow.Attachment.Api.Endpoints;

public static class AttachmentEndpoints
{
    public static void MapAttachmentEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/engagements/{id}").WithTags("Attachments").RequireAuthorization();

        // Bearer-token API (no cookies), so antiforgery does not apply to the multipart endpoints.
        group.MapPost("/attachments", async (string id, IFormFile file, AttachmentService service, CancellationToken ct) =>
                Results.Created($"/engagements/{id}/attachments", await Upload(service, id, AttachmentScope.Engagement, null, null, file, ct)))
            .DisableAntiforgery().WithMetadata(UploadLimit());

        group.MapPost("/tabs/{tabNo:int}/attachments", async (string id, int tabNo, IFormFile file, AttachmentService service, CancellationToken ct) =>
                Results.Created($"/engagements/{id}/attachments", await Upload(service, id, AttachmentScope.Tab, tabNo, null, file, ct)))
            .DisableAntiforgery().WithMetadata(UploadLimit());

        group.MapPost("/sections/{sectionKey}/attachments", async (string id, string sectionKey, IFormFile file, AttachmentService service, CancellationToken ct) =>
                Results.Created($"/engagements/{id}/attachments", await Upload(service, id, AttachmentScope.Section, null, sectionKey, file, ct)))
            .DisableAntiforgery().WithMetadata(UploadLimit());

        group.MapGet("/attachments", (string id, AttachmentService service, CancellationToken ct) => service.ListAsync(id, ct));

        group.MapGet("/attachments/{attachmentId:guid}/download", async (string id, Guid attachmentId, AttachmentService service, HttpContext http, CancellationToken ct) =>
        {
            var download = await service.DownloadAsync(id, attachmentId, ct);
            // Never trust the recorded content type: serve opaque bytes as a download and forbid sniffing.
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(download.Content, "application/octet-stream", download.FileName);
        });
    }

    private static Task<AttachmentDto> Upload(
        AttachmentService service, string id, AttachmentScope scope, int? tabNo, string? sectionKey, IFormFile file, CancellationToken ct)
        => service.UploadAsync(id, scope, tabNo, sectionKey, new UploadedFile(file.FileName, file.ContentType, file.Length, file.OpenReadStream()), ct);

    // Slightly above the 10 MB file limit to leave room for multipart framing; the exact limit is enforced on the file.
    private static IRequestSizeLimitMetadata UploadLimit() => new RequestSizeLimitAttribute(UploadRules.MaxBytes + 1024 * 1024);
}
