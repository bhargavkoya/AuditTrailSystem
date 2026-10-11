using System.Text.RegularExpressions;
using AuditFlow.Attachment.Api.Clients;
using AuditFlow.Attachment.Api.Data;
using AuditFlow.Attachment.Api.Storage;
using AuditFlow.BuildingBlocks.Access;
using AuditFlow.BuildingBlocks.Data;
using AuditFlow.BuildingBlocks.Errors;
using AuditFlow.BuildingBlocks.Eventing;
using AuditFlow.BuildingBlocks.Security;
using AuditFlow.Contracts.Api;

namespace AuditFlow.Attachment.Api.Domain;

public sealed record UploadedFile(string? FileName, string? ContentType, long Length, Stream Content);

public sealed record FileDownload(Stream Content, string FileName);

public sealed partial class AttachmentService(
    IAttachmentRepository repository,
    AttachmentWriter writer,
    IFileStore store,
    IWorkflowClient workflow,
    IEngagementAccessClient accessClient,
    IUnitOfWorkFactory unitOfWork,
    ICurrentUser currentUser,
    ILogger<AttachmentService> logger)
{
    [GeneratedRegex("^[A-Za-z0-9-]{1,50}$")]
    private static partial Regex SectionKeyPattern();

    /// <summary>
    /// Requires attachment.upload (so Viewers, Reviewers and locked engagements are refused). A new upload at the same
    /// scope replaces the previous file: only the latest is kept.
    /// </summary>
    public async Task<AttachmentDto> UploadAsync(
        string engagementId, AttachmentScope scope, int? tabNo, string? sectionKey, UploadedFile file, CancellationToken ct)
    {
        await accessClient.RequireAsync(engagementId, Capabilities.Attach, ct);

        UploadRules.ValidateSize(file.Length);
        await ValidateScopeAsync(engagementId, scope, tabNo, sectionKey, ct);

        var request = new NewAttachment(
            engagementId, scope, scope == AttachmentScope.Tab ? tabNo : null, scope == AttachmentScope.Section ? sectionKey : null,
            UploadRules.SanitizeFileName(file.FileName), UploadRules.NormalizeContentType(file.ContentType), file.Length,
            currentUser.UserId, currentUser.Name);

        StoredAttachment stored;
        await using (var uow = await unitOfWork.BeginAsync(ct))
        {
            stored = await writer.AddAsync(uow, request, file.Content, new EventActor(currentUser.UserId.ToString(), currentUser.Name), ct);
            try
            {
                await uow.CommitAsync(ct);
            }
            catch
            {
                await store.DeleteAsync(stored.StorageKey, CancellationToken.None);
                throw;
            }
        }

        await RemoveReplacedFileAsync(stored.ReplacedStorageKey);
        return stored.Dto;
    }

    /// <summary>Current attachments only. Requires attachment.view, which Viewers do not have.</summary>
    public async Task<IReadOnlyList<AttachmentDto>> ListAsync(string engagementId, CancellationToken ct)
    {
        await accessClient.RequireAsync(engagementId, Capabilities.AttachView, ct);
        return await repository.ListCurrentAsync(engagementId, ct);
    }

    public async Task<FileDownload> DownloadAsync(string engagementId, Guid attachmentId, CancellationToken ct)
    {
        await accessClient.RequireAsync(engagementId, Capabilities.AttachView, ct);

        var found = await repository.GetCurrentAsync(engagementId, attachmentId, ct)
                    ?? throw new NotFoundException("Attachment not found.");
        var stream = await store.OpenReadAsync(found.StorageKey, ct)
                     ?? throw new NotFoundException("The attachment file is no longer available.");
        return new FileDownload(stream, found.Dto.FileName);
    }

    private async Task ValidateScopeAsync(string engagementId, AttachmentScope scope, int? tabNo, string? sectionKey, CancellationToken ct)
    {
        switch (scope)
        {
            case AttachmentScope.Engagement:
                return;

            case AttachmentScope.Tab:
                UploadRules.ValidateTab(tabNo ?? 0);
                return;

            case AttachmentScope.Section:
                if (sectionKey is null || !SectionKeyPattern().IsMatch(sectionKey))
                    throw new ValidationFailedException("sectionKey", "Invalid section.");

                var policy = await workflow.GetSectionPolicyAsync(engagementId, sectionKey, ct);
                if (!policy.Exists) throw new NotFoundException($"Section {sectionKey} does not exist.");
                if (!policy.Visible) throw new ValidationFailedException("sectionKey", "This section is not available for the current configuration.");
                if (!policy.AttachmentAllowed) throw new ValidationFailedException("sectionKey", "This section does not accept attachments.");
                return;
        }
    }

    private async Task RemoveReplacedFileAsync(string? storageKey)
    {
        if (storageKey is null) return;
        try
        {
            await store.DeleteAsync(storageKey, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete the replaced file {StorageKey}; it is orphaned but no longer referenced as current", storageKey);
        }
    }
}
