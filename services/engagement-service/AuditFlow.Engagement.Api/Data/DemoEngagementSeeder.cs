using AuditFlow.BuildingBlocks.Data;
using AuditFlow.BuildingBlocks.Messaging;
using AuditFlow.Contracts.Api;
using AuditFlow.Engagement.Api.Domain;
using static AuditFlow.Contracts.Api.DemoData;

namespace AuditFlow.Engagement.Api.Data;

/// <summary>
/// Seeds one engagement per lifecycle status on first run. Rows are inserted directly in their target status
/// (deliberately bypassing the state machine) and each one emits EngagementCreated (seeded=true, with its status)
/// so later services can provision themselves from the same events.
/// </summary>
public sealed class DemoEngagementSeeder(
    IEngagementRepository repository,
    IUnitOfWorkFactory unitOfWork,
    IOutbox outbox,
    TimeProvider clock,
    ILogger<DemoEngagementSeeder> logger) : IDataSeeder
{
    private sealed record Seed(
        string Name, string Period, string Region, EngagementStatus Status, DemoUser Owner, DemoUser Reviewer, DemoUser[] Others, int DaysAgo);

    private static readonly Seed[] Seeds =
    [
        new("EMEA Equities Q1 audit", "Quarterly", "EMEA", EngagementStatus.Draft, Alice, Rachel, [Bob, Victor], 1),
        new("APAC FX controls review", "Half-Yearly", "APAC", EngagementStatus.InProgress, Alice, Rohan, [], 3),
        new("AMER Derivatives annual audit", "Annual", "AMER", EngagementStatus.UnderReview, Bob, Rachel, [Alice, Victor], 6),
        new("EMEA Fixed Income Q4 audit", "Quarterly", "EMEA", EngagementStatus.Approved, Alice, Rachel, [], 20),
        new("APAC Equities half-year review", "Half-Yearly", "APAC", EngagementStatus.Rejected, Alice, Rohan, [Bob, Victor], 9)
    ];

    public async Task SeedAsync(CancellationToken ct)
    {
        if (await repository.AnyAsync(ct))
            return;

        var now = clock.GetUtcNow();
        foreach (var seed in Seeds)
        {
            var created = now.AddDays(-seed.DaysAgo);
            var participants = new List<ParticipantRow>
            {
                new(seed.Owner.UserId, seed.Owner.HomeRole.ToString(), seed.Owner.DisplayName),
                new(seed.Reviewer.UserId, nameof(EngagementRole.Reviewer), seed.Reviewer.DisplayName)
            };
            participants.AddRange(seed.Others.Select(u => new ParticipantRow(u.UserId, u.HomeRole.ToString(), u.DisplayName)));

            await using var uow = await unitOfWork.BeginAsync(ct);
            var id = await repository.NextIdAsync(uow, ct);
            var submitted = seed.Status is EngagementStatus.UnderReview or EngagementStatus.Approved or EngagementStatus.Rejected ? 1 : 0;
            var engagement = new NewEngagement(
                id, seed.Name, seed.Period, seed.Region, seed.Owner.UserId, seed.Reviewer.UserId,
                seed.Status, submitted, created, created.AddHours(2), participants);

            await repository.InsertAsync(uow, engagement, ct);
            await outbox.EnqueueAsync(uow, EngagementService.CreatedEvent(
                engagement, new ConfigurationRequest(["Equities"], new Dictionary<string, bool> { ["includeReconciliation"] = true }), seeded: true), ct);
            await uow.CommitAsync(ct);
        }

        logger.LogInformation("Seeded {Count} demo engagements", Seeds.Length);
    }
}
