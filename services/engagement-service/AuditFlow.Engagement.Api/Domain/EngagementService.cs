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
    TimeProvider clock)
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
