using AuditFlow.BuildingBlocks.Data;
using AuditFlow.BuildingBlocks.Errors;
using AuditFlow.BuildingBlocks.Eventing;
using AuditFlow.BuildingBlocks.Messaging;
using AuditFlow.BuildingBlocks.Security;
using AuditFlow.BuildingBlocks.Versioning;
using AuditFlow.Contracts.Api;
using AuditFlow.Contracts.Events;
using AuditFlow.Engagement.Api.Clients;
using AuditFlow.Engagement.Api.Data;
using Microsoft.Extensions.Options;

namespace AuditFlow.Engagement.Api.Domain;

public sealed class EngagementService(
    IEngagementRepository repository,
    IUserDirectoryClient directory,
    IUnitOfWorkFactory unitOfWork,
    IOutbox outbox,
    IOptions<CreationCatalogue> catalogue,
    ICurrentUser currentUser,
    TimeProvider clock,
    IWorkflowReadinessClient readiness)
{
    public CreationOptionsDto GetCreationOptions() => catalogue.Value.ToDto();

    public async Task<EngagementDetailDto> CreateAsync(CreateEngagementRequest request, CancellationToken ct)
    {
        if (!PermissionPolicy.CanCreateEngagement(currentUser.HomeRole))
            throw new ForbiddenException("Only an Auditor or Co-Auditor can create engagements.");

        var transition = EngagementStateMachine.Evaluate(null, Trigger.Create, new TransitionContext(currentUser.HomeRole));
        if (!transition.Allowed)
            throw new ForbiddenException(transition.Reason!);

        var errors = CreateEngagementValidator.Validate(request, catalogue.Value, currentUser.UserId);
        if (errors.Count > 0)
            throw new ValidationFailedException(errors);

        var participantIds = request.ParticipantUserIds ?? [];
        var users = await directory.LookupAsync([currentUser.UserId, request.ReviewerUserId, .. participantIds], ct);
        ValidateUsers(request, participantIds, users);

        var now = clock.GetUtcNow();
        var participants = new List<ParticipantRow>
        {
            new(currentUser.UserId, currentUser.HomeRole.ToString(), users[currentUser.UserId].DisplayName),
            new(request.ReviewerUserId, nameof(EngagementRole.Reviewer), users[request.ReviewerUserId].DisplayName)
        };
        // A participant's engagement role is their home role (Auditor / Co-Auditor / Viewer).
        participants.AddRange(participantIds.Select(id => new ParticipantRow(id, users[id].HomeRole.ToString(), users[id].DisplayName)));

        string engagementId;
        await using (var uow = await unitOfWork.BeginAsync(ct))
        {
            engagementId = await repository.NextIdAsync(uow, ct);
            var engagement = new NewEngagement(
                engagementId, request.Name.Trim(), request.PeriodType, request.Region,
                currentUser.UserId, request.ReviewerUserId, EngagementStatus.Draft, SubmissionCount: 0, now, now, participants);

            await repository.InsertAsync(uow, engagement, ct);
            await outbox.EnqueueAsync(uow, CreatedEvent(engagement, request.Configuration, seeded: false), ct);
            await uow.CommitAsync(ct);
        }

        return await GetAsync(engagementId, ct);
    }

    /// <summary>
    /// Draft-only edit of name / period / region / reviewer. The version (If-Match) must match; 409 otherwise.
    /// A request that changes nothing is a no-op: no version bump, no event.
    /// </summary>
    public async Task<EngagementDetailDto> UpdateAsync(string engagementId, UpdateEngagementRequest request, string ifMatch, CancellationToken ct)
    {
        var (engagement, role) = await LoadAsync(engagementId, ct);
        if (!PermissionPolicy.CanEditDetails(role, engagement.StatusValue))
            throw new ForbiddenException("Engagement details can only be edited by an Auditor or Co-Auditor while the engagement is a Draft.");

        var errors = UpdateEngagementValidator.Validate(request, catalogue.Value, engagement.OwnerUserId);
        if (errors.Count > 0) throw new ValidationFailedException(errors);
        var expectedVersion = RowVersion.FromToken(ifMatch);

        var name = request.Name?.Trim() ?? engagement.Name;
        var periodType = request.PeriodType ?? engagement.PeriodType;
        var region = request.Region ?? engagement.Region;
        var reviewerId = request.ReviewerUserId ?? engagement.ReviewerUserId;

        var changed = new List<string>();
        if (name != engagement.Name) changed.Add("name");
        if (periodType != engagement.PeriodType) changed.Add("periodType");
        if (region != engagement.Region) changed.Add("region");
        if (reviewerId != engagement.ReviewerUserId) changed.Add("reviewerUserId");
        if (changed.Count == 0) return await GetAsync(engagementId, ct);

        ParticipantRow? newReviewer = null;
        if (changed.Contains("reviewerUserId"))
        {
            var users = await directory.LookupAsync([reviewerId], ct);
            if (!users.TryGetValue(reviewerId, out var user) || user.HomeRole != EngagementRole.Reviewer)
                throw new ValidationFailedException("reviewerUserId", "The selected user is not a Reviewer.");
            var participants = await repository.GetParticipantsAsync(engagementId, ct);
            if (participants.Any(p => p.UserId == reviewerId))
                throw new ValidationFailedException("reviewerUserId", "The selected user already participates in this engagement.");
            newReviewer = new ParticipantRow(reviewerId, nameof(EngagementRole.Reviewer), user.DisplayName);
        }

        var now = clock.GetUtcNow();
        await using (var uow = await unitOfWork.BeginAsync(ct))
        {
            var version = await repository.UpdateDetailsAsync(uow, engagementId, new DetailsUpdate(name, periodType, region, reviewerId), expectedVersion, now, ct);
            if (version is null)
            {
                var current = await repository.GetRowAsync(uow, engagementId, ct);
                if (current is not null && current.StatusValue != EngagementStatus.Draft)
                    throw new ForbiddenException("The engagement is no longer a Draft; its details are locked.");
                throw new ConcurrencyException(current is null ? null : RowVersion.ToToken(current.RowVer));
            }

            if (newReviewer is not null)
                await repository.ReplaceReviewerAsync(uow, engagementId, engagement.ReviewerUserId, newReviewer, now, ct);

            await outbox.EnqueueAsync(uow, EventEnvelope<EngagementUpdatedPayload>.Create(
                EventTypes.EngagementUpdated, engagementId,
                new EngagementUpdatedPayload(
                    changed, RowVersion.ToToken(version),
                    changed.Contains("periodType") ? periodType : null, changed.Contains("region") ? region : null),
                new EventActor(currentUser.UserId.ToString(), currentUser.Name)), ct);
            await uow.CommitAsync(ct);
        }

        return await GetAsync(engagementId, ct);
    }

    /// <summary>
    /// InProgress -> Under Review. Permission and legal-state checks come from the state machine; the workflow-service
    /// decides readiness (zero blocking errors). <paramref name="ifMatch"/> is optional: ordinary saves touch the engagement's
    /// activity time, so a strict version check would reject most submits. The guard is <c>WHERE Status = 'InProgress'</c>.
    /// </summary>
    public async Task<EngagementDetailDto> SubmitAsync(string engagementId, string? ifMatch, CancellationToken ct)
    {
        var (engagement, role) = await LoadAsync(engagementId, ct);
        var expectedVersion = string.IsNullOrWhiteSpace(ifMatch) ? null : RowVersion.FromToken(ifMatch);

        // Who/what first (403), then readiness (422).
        var permitted = EngagementStateMachine.Evaluate(engagement.StatusValue, Trigger.Submit, new TransitionContext(role, ValidationPassed: true));
        if (!permitted.Allowed) throw new ForbiddenException(permitted.Reason!);

        var readinessResult = await readiness.GetReadinessAsync(engagementId, ct);
        var decision = EngagementStateMachine.Evaluate(
            engagement.StatusValue, Trigger.Submit, new TransitionContext(role, ValidationPassed: readinessResult.ReadyToSubmit));
        if (!decision.Allowed)
            throw new SubmissionBlockedException(readinessResult.Errors, decision.Reason!);

        var now = clock.GetUtcNow();
        await using (var uow = await unitOfWork.BeginAsync(ct))
        {
            var outcome = await repository.SubmitAsync(uow, engagementId, expectedVersion, now, ct);
            if (outcome is null)
            {
                var current = await repository.GetRowAsync(uow, engagementId, ct);
                if (current is not null && current.StatusValue != EngagementStatus.InProgress)
                    throw new ForbiddenException($"The engagement is {current.StatusValue} and can no longer be submitted.");
                throw new ConcurrencyException(current is null ? null : RowVersion.ToToken(current.RowVer));
            }

            var actor = new EventActor(currentUser.UserId.ToString(), currentUser.Name);
            var version = RowVersion.ToToken(outcome.RowVer);
            await outbox.EnqueueAsync(uow, EventEnvelope<EngagementSubmittedPayload>.Create(
                EventTypes.EngagementSubmitted, engagementId,
                new EngagementSubmittedPayload(currentUser.UserId, currentUser.Name, outcome.SubmissionCount), actor), ct);
            await outbox.EnqueueAsync(uow, EventEnvelope<EngagementStatusChangedPayload>.Create(
                EventTypes.EngagementStatusChanged, engagementId,
                new EngagementStatusChangedPayload(EngagementStatus.InProgress, EngagementStatus.UnderReview, nameof(Trigger.Submit), version), actor), ct);
            await uow.CommitAsync(ct);
        }

        return await GetAsync(engagementId, ct);
    }

    public static EventEnvelope<EngagementCreatedPayload> CreatedEvent(NewEngagement e, ConfigurationRequest configuration, bool seeded)
        => EventEnvelope<EngagementCreatedPayload>.Create(
            EventTypes.EngagementCreated, e.EngagementId,
            new EngagementCreatedPayload(
                e.Name, e.PeriodType, e.Region, e.Status, seeded, e.OwnerUserId, e.ReviewerUserId,
                e.Participants.Select(p => new ParticipantInfo(p.UserId, p.RoleValue, p.DisplayName)).ToList(),
                new ConfigurationInfo(configuration.ConfigTypes, configuration.Options ?? new Dictionary<string, bool>())),
            actor: new EventActor(e.OwnerUserId.ToString(), e.Participants.FirstOrDefault(p => p.UserId == e.OwnerUserId)?.DisplayName));

    private static void ValidateUsers(CreateEngagementRequest request, IReadOnlyList<Guid> participantIds, IReadOnlyDictionary<Guid, UserSummaryDto> users)
    {
        var errors = new Dictionary<string, string[]>();

        if (!users.TryGetValue(request.ReviewerUserId, out var reviewer))
            errors["reviewerUserId"] = ["Reviewer not found."];
        else if (reviewer.HomeRole != EngagementRole.Reviewer)
            errors["reviewerUserId"] = ["The selected user is not a Reviewer."];

        var participantErrors = new List<string>();
        foreach (var id in participantIds)
        {
            if (!users.TryGetValue(id, out var participant)) participantErrors.Add($"User {id} not found.");
            else if (participant.HomeRole == EngagementRole.Reviewer)
                participantErrors.Add($"{participant.DisplayName} is a Reviewer; reviewers are set through reviewerUserId.");
        }
        if (participantErrors.Count > 0)
            errors["participantUserIds"] = participantErrors.ToArray();

        if (errors.Count > 0)
            throw new ValidationFailedException(errors);
    }

    public async Task<EngagementDetailDto> GetAsync(string engagementId, CancellationToken ct)
    {
        var (engagement, role) = await LoadAsync(engagementId, ct);
        var participants = await repository.GetParticipantsAsync(engagementId, ct);
        var status = engagement.StatusValue;

        PersonDto Person(Guid id) => new(id, participants.FirstOrDefault(p => p.UserId == id)?.DisplayName ?? string.Empty);

        return new EngagementDetailDto(
            engagement.EngagementId, engagement.Name, engagement.Region, engagement.PeriodType, status,
            Person(engagement.OwnerUserId), Person(engagement.ReviewerUserId), role,
            PermissionPolicy.AllowedActionsFor(role, status), PermissionPolicy.CapabilitiesFor(role, status),
            RowVersion.ToToken(engagement.RowVer), engagement.CreatedAt, engagement.LastActivityAt);
    }

    public async Task<IReadOnlyList<ParticipantDto>> GetParticipantsAsync(string engagementId, CancellationToken ct)
    {
        await LoadAsync(engagementId, ct); // 404 / 403 checks
        return (await repository.GetParticipantsAsync(engagementId, ct)).Select(p => new ParticipantDto(p.UserId, p.DisplayName, p.RoleValue)).ToList();
    }

    public async Task<EngagementAccessDto> GetAccessAsync(string engagementId, CancellationToken ct)
    {
        var (engagement, role) = await LoadAsync(engagementId, ct);
        return new EngagementAccessDto(
            engagement.EngagementId, role, engagement.StatusValue, RowVersion.ToToken(engagement.RowVer),
            PermissionPolicy.CapabilitiesFor(role, engagement.StatusValue));
    }

    public async Task<EngagementListDto> ListAsync(ListScope scope, EngagementStatus? status, string? search, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var result = await repository.ListAsync(new ListQuery(currentUser.UserId, scope, status, search, page, pageSize), ct);

        var items = result.Items.Select(r =>
        {
            var rowStatus = Enum.Parse<EngagementStatus>(r.Status);
            var role = Enum.Parse<EngagementRole>(r.MyRole);
            return new EngagementSummaryDto(
                r.EngagementId, r.Name, r.Region, r.PeriodType, new PersonDto(r.ReviewerUserId, r.ReviewerName),
                rowStatus, r.LastActivityAt, role, PermissionPolicy.AllowedActionsFor(role, rowStatus));
        }).ToList();

        // Always return every status so the cards can render zeros.
        var counts = Enum.GetNames<EngagementStatus>().ToDictionary(s => s, s => result.Counts.GetValueOrDefault(s));
        return new EngagementListDto(items, counts, result.Total, page, pageSize);
    }

    private async Task<(EngagementRow Engagement, EngagementRole Role)> LoadAsync(string engagementId, CancellationToken ct)
    {
        var found = await repository.GetAsync(engagementId, currentUser.UserId, ct)
                    ?? throw new NotFoundException($"Engagement {engagementId} not found.");
        return found.MyRole is { } role
            ? (found.Engagement, role)
            : throw new ForbiddenException("You are not a participant of this engagement.");
    }
}
