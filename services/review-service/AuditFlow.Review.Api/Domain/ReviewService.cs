using AuditFlow.BuildingBlocks.Access;
using AuditFlow.BuildingBlocks.Data;
using AuditFlow.BuildingBlocks.Errors;
using AuditFlow.BuildingBlocks.Eventing;
using AuditFlow.BuildingBlocks.Messaging;
using AuditFlow.BuildingBlocks.Security;
using AuditFlow.Contracts.Api;
using AuditFlow.Contracts.Events;
using AuditFlow.Review.Api.Clients;
using AuditFlow.Review.Api.Data;

namespace AuditFlow.Review.Api.Domain;

public static class ReviewRules
{
    public const int MaxTextLength = 2000;
    public const int TabCount = 5;
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";

    /// <summary>Tab-level comments only: a tab 1-5 and a non-empty body within the limit.</summary>
    public static IDictionary<string, string[]> ValidateComment(AddCommentRequest? request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request is null) { errors["body"] = ["A comment is required."]; return errors; }
        if (request.TabNo is < 1 or > TabCount) errors["tabNo"] = [$"Comments belong to one of the {TabCount} tabs."];
        if (string.IsNullOrWhiteSpace(request.Body)) errors["body"] = ["A comment is required."];
        else if (request.Body.Trim().Length > MaxTextLength) errors["body"] = [$"A comment can have at most {MaxTextLength} characters."];
        return errors;
    }
}

/// <summary>
/// The review screen and review actions. Permissions come from engagement-service (comment.add / review.decide are only
/// granted to the assigned Reviewer while Under Review); this service owns comments, decisions and history.
/// </summary>
public sealed class ReviewService(
    IReviewRepository repository,
    IEngagementClient engagements,
    IWorkflowViewClient workflow,
    IEngagementAccessClient accessClient,
    IUnitOfWorkFactory unitOfWork,
    IOutbox outbox,
    ICurrentUser currentUser,
    TimeProvider clock,
    ILogger<ReviewService> logger)
{
    public async Task<ReviewViewDto> GetAsync(string engagementId, CancellationToken ct)
    {
        var engagement = await engagements.GetAsync(engagementId, ct);
        if (!engagement.Capabilities.Contains(Capabilities.CommentView))
            throw new ForbiddenException("Viewers cannot open the review screen.");

        var cycle = await repository.LatestCycleAsync(engagementId, ct) ?? 0;
        var commentsTask = repository.GetCommentsAsync(engagementId, ct);
        var historyTask = repository.GetDecisionsAsync(engagementId, ct);
        var tabsTask = LoadTabsAsync(engagementId, ct);
        var validationTask = LoadValidationAsync(engagementId, ct);
        await Task.WhenAll(commentsTask, historyTask, tabsTask, validationTask);

        var history = historyTask.Result;
        var decidedThisCycle = history.Any(d => d.Cycle == cycle);
        var canComment = cycle > 0 && engagement.Capabilities.Contains(Capabilities.Comment);
        var canDecide = cycle > 0 && !decidedThisCycle && engagement.Capabilities.Contains(Capabilities.Review);

        return new ReviewViewDto(engagement, cycle, canComment, canDecide, tabsTask.Result, validationTask.Result, commentsTask.Result, history);
    }

    public async Task<TabCommentDto> AddCommentAsync(string engagementId, AddCommentRequest request, CancellationToken ct)
    {
        await accessClient.RequireAsync(engagementId, Capabilities.Comment, ct);
        var errors = ReviewRules.ValidateComment(request);
        if (errors.Count > 0) throw new ValidationFailedException(errors);

        var now = clock.GetUtcNow();
        var comment = new NewComment(
            Guid.NewGuid(), engagementId, 0, request.TabNo, currentUser.UserId, currentUser.Name, request.Body.Trim(), now);

        await using var uow = await unitOfWork.BeginAsync(ct);
        var cycle = await repository.LatestCycleAsync(uow, engagementId, ct)
                    ?? throw new ConflictException("The review has not been opened yet; try again in a moment.");
        comment = comment with { Cycle = cycle };

        await repository.InsertCommentAsync(uow, comment, ct);
        await outbox.EnqueueAsync(uow, EventEnvelope<ReviewerCommentAddedPayload>.Create(
            EventTypes.ReviewerCommentAdded, engagementId,
            new ReviewerCommentAddedPayload(comment.CommentId, comment.TabNo, comment.AuthorUserId, cycle),
            new EventActor(currentUser.UserId.ToString(), currentUser.Name)), ct);
        await uow.CommitAsync(ct);

        return new TabCommentDto(comment.CommentId, comment.TabNo, cycle, comment.AuthorUserId, comment.AuthorName, comment.Body, now);
    }

    public Task<DecisionResultDto> ApproveAsync(string engagementId, DecisionRequest? request, CancellationToken ct)
        => DecideAsync(engagementId, approved: true, request?.Comment, ct);

    /// <summary>Rejection needs a non-empty comment (400); the database CHECK is the backstop.</summary>
    public Task<DecisionResultDto> RejectAsync(string engagementId, DecisionRequest? request, CancellationToken ct)
        => DecideAsync(engagementId, approved: false, request?.Comment, ct);

    private async Task<DecisionResultDto> DecideAsync(string engagementId, bool approved, string? rawComment, CancellationToken ct)
    {
        await accessClient.RequireAsync(engagementId, Capabilities.Review, ct);

        var comment = string.IsNullOrWhiteSpace(rawComment) ? null : rawComment.Trim();
        if (!approved && comment is null)
            throw new BadHttpRequestException("A comment is mandatory when rejecting.");
        if (comment is { Length: > ReviewRules.MaxTextLength })
            throw new BadHttpRequestException($"A comment can have at most {ReviewRules.MaxTextLength} characters.");

        var decisionText = approved ? ReviewRules.Approved : ReviewRules.Rejected;
        var actor = new EventActor(currentUser.UserId.ToString(), currentUser.Name);

        await using var uow = await unitOfWork.BeginAsync(ct);
        var cycle = await repository.LatestCycleAsync(uow, engagementId, ct)
                    ?? throw new ConflictException("The review has not been opened yet; try again in a moment.");

        var decisionId = Guid.NewGuid();
        var inserted = await repository.TryInsertDecisionAsync(uow, new NewDecision(
            decisionId, engagementId, cycle, decisionText, currentUser.UserId, currentUser.Name, comment, clock.GetUtcNow()), ct);
        if (!inserted)
            throw new ConflictException("A decision has already been recorded for this review cycle.");

        if (approved)
            await outbox.EnqueueAsync(uow, EventEnvelope<ReviewApprovedPayload>.Create(
                EventTypes.ReviewApproved, engagementId, new ReviewApprovedPayload(decisionId, currentUser.UserId, cycle), actor), ct);
        else
            await outbox.EnqueueAsync(uow, EventEnvelope<ReviewRejectedPayload>.Create(
                EventTypes.ReviewRejected, engagementId, new ReviewRejectedPayload(decisionId, currentUser.UserId, cycle), actor), ct);
        await uow.CommitAsync(ct);

        return new DecisionResultDto(decisionId, cycle, decisionText);
    }

    // ---- Composition: a failing upstream degrades the affected tab / panel instead of failing the whole screen ----

    private async Task<IReadOnlyList<ReviewTabDto>> LoadTabsAsync(string engagementId, CancellationToken ct)
    {
        TabsDto tabs;
        try { tabs = await workflow.GetTabsAsync(engagementId, ct); }
        catch (Exception ex) when (IsUpstreamFailure(ex))
        {
            logger.LogWarning(ex, "Tab list unavailable for {EngagementId}", engagementId);
            return Enumerable.Range(1, ReviewRules.TabCount).Select(n => new ReviewTabDto(n, $"Tab {n}", false, null)).ToList();
        }

        var loaded = await Task.WhenAll(tabs.Tabs.Select(async t =>
        {
            try { return new ReviewTabDto(t.TabNo, t.Title, true, await workflow.GetTabAsync(engagementId, t.TabNo, ct)); }
            catch (Exception ex) when (IsUpstreamFailure(ex))
            {
                logger.LogWarning(ex, "Tab {TabNo} unavailable for {EngagementId}", t.TabNo, engagementId);
                return new ReviewTabDto(t.TabNo, t.Title, false, null);
            }
        }));
        return loaded.OrderBy(t => t.TabNo).ToList();
    }

    private async Task<ValidationSummaryDto?> LoadValidationAsync(string engagementId, CancellationToken ct)
    {
        try { return await workflow.GetValidationAsync(engagementId, ct); }
        catch (Exception ex) when (IsUpstreamFailure(ex))
        {
            logger.LogWarning(ex, "Validation summary unavailable for {EngagementId}", engagementId);
            return null;
        }
    }

    private static bool IsUpstreamFailure(Exception ex)
        => ex is HttpRequestException or TaskCanceledException or NotFoundException;
}
