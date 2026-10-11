using System.Text.Json;
using AuditFlow.BuildingBlocks.Access;
using AuditFlow.BuildingBlocks.Eventing;
using AuditFlow.BuildingBlocks.Messaging;
using AuditFlow.BuildingBlocks.Security;
using AuditFlow.Contracts.Api;
using AuditFlow.Contracts.Events;
using AuditFlow.Engagement.Api.Clients;
using AuditFlow.Engagement.Api.Data;
using AuditFlow.Engagement.Api.Domain;
using AuditFlow.Engagement.Api.Handlers;
using AuditFlow.Review.Api.Clients;
using AuditFlow.Review.Api.Data;
using AuditFlow.Review.Api.Domain;
using AuditFlow.Review.Api.Handlers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using static AuditFlow.Contracts.Api.DemoData;

namespace AuditFlow.IntegrationTests;

/// <summary>
/// The whole review lifecycle across the engagement and review databases, with the broker replaced by relaying outbox
/// rows by hand: submit -> review -> reject -> rework -> resubmit -> approve.
/// </summary>
[Collection(SqlCollection.Name)]
[Trait("Category", "Integration")]
public class ReviewLifecycleTests(SqlServerFixture sql)
{
    private const string Eng = "ENG-1002"; // seeded In Progress: owner Alice, reviewer Rohan

    private sealed class World
    {
        public required TestDatabase EngagementDb { get; init; }
        public required TestDatabase ReviewDb { get; init; }
        public required EngagementRepository EngagementRepo { get; init; }
        public required ReviewRepository ReviewRepo { get; init; }
        private readonly HashSet<Guid> relayed = [];

        private static Mock<ICurrentUser> Current(DemoUser user)
        {
            var current = new Mock<ICurrentUser>();
            current.SetupGet(u => u.UserId).Returns(user.UserId);
            current.SetupGet(u => u.Name).Returns(user.DisplayName);
            current.SetupGet(u => u.HomeRole).Returns(user.HomeRole);
            return current;
        }

        public EngagementService EngagementFor(DemoUser user)
        {
            var directory = new Mock<IUserDirectoryClient>();
            var workflow = new Mock<IWorkflowReadinessClient>();
            workflow.Setup(w => w.GetReadinessAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new SubmissionReadinessDto(true, []));
            return new EngagementService(EngagementRepo, directory.Object, EngagementDb.UnitOfWork, new SqlOutbox(),
                Options.Create(new CreationCatalogue()), Current(user).Object, TimeProvider.System, workflow.Object);
        }

        public ReviewService ReviewFor(DemoUser user)
        {
            var access = new Mock<IEngagementAccessClient>();
            access.Setup(a => a.GetAccessAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(
                new EngagementAccessDto(Eng, EngagementRole.Reviewer, EngagementStatus.UnderReview, "v",
                    [Capabilities.View, Capabilities.CommentView, Capabilities.Comment, Capabilities.Review]));
            var engagements = new Mock<IEngagementClient>();
            var view = new Mock<IWorkflowViewClient>();
            return new ReviewService(ReviewRepo, engagements.Object, view.Object, access.Object, ReviewDb.UnitOfWork, new SqlOutbox(),
                Current(user).Object, TimeProvider.System, NullLogger<ReviewService>.Instance);
        }

        /// <summary>Delivers every not-yet-relayed outbox event of a type, in order, as the broker would.</summary>
        public async Task RelayAsync<T>(TestDatabase from, string eventType, Func<EventEnvelope<T>, Task> deliver)
        {
            await using var connection = await from.Connections.OpenAsync(CancellationToken.None);
            var rows = await Dapper.SqlMapper.QueryAsync<string>(connection,
                "SELECT Envelope FROM dbo.OutboxMessage WHERE EventType = @eventType ORDER BY Seq", new { eventType });
            foreach (var json in rows)
            {
                var envelope = JsonSerializer.Deserialize<EventEnvelope<T>>(json, JsonDefaults.Web)!;
                if (relayed.Add(envelope.EventId)) await deliver(envelope);
            }
        }

        public Task SubmittedToReviewAsync() => RelayAsync<EngagementSubmittedPayload>(EngagementDb, EventTypes.EngagementSubmitted, async e =>
        {
            await using var uow = await ReviewDb.UnitOfWork.BeginAsync(CancellationToken.None);
            await new EngagementSubmittedHandler(ReviewRepo).HandleAsync(e, uow, CancellationToken.None);
            await uow.CommitAsync(CancellationToken.None);
        });

        public async Task DecisionsToEngagementAsync()
        {
            await RelayAsync<ReviewApprovedPayload>(ReviewDb, EventTypes.ReviewApproved, e => ApplyAsync(Trigger.Approve, e.EngagementId, e.Payload.ReviewerId, e.Actor));
            await RelayAsync<ReviewRejectedPayload>(ReviewDb, EventTypes.ReviewRejected, e => ApplyAsync(Trigger.Reject, e.EngagementId, e.Payload.ReviewerId, e.Actor));
        }

        private async Task ApplyAsync(Trigger trigger, string engagementId, Guid reviewerId, EventActor? actor)
        {
            await using var uow = await EngagementDb.UnitOfWork.BeginAsync(CancellationToken.None);
            await new ReviewDecisionApplier(EngagementRepo, new SqlOutbox(), TimeProvider.System, NullLogger<ReviewDecisionApplier>.Instance)
                .ApplyAsync(engagementId, trigger, reviewerId, actor, uow, CancellationToken.None);
            await uow.CommitAsync(CancellationToken.None);
        }

        public async Task EditAsync(DemoUser editor)
        {
            await using var uow = await EngagementDb.UnitOfWork.BeginAsync(CancellationToken.None);
            await new WorkActivityHandler(EngagementRepo, new SqlOutbox(), TimeProvider.System, NullLogger<WorkActivityHandler>.Instance)
                .HandleAsync(Eng, new EventActor(editor.UserId.ToString(), editor.DisplayName), EventTypes.SectionUpdated, uow, CancellationToken.None);
            await uow.CommitAsync(CancellationToken.None);
        }

        public Task<string> StatusAsync() => EngagementDb.ScalarAsync<string>("SELECT Status FROM dbo.Engagements WHERE EngagementId = @Eng", new { Eng });
    }

    private async Task<World> NewWorldAsync()
    {
        var engagementDb = await sql.CreateDatabaseAsync(typeof(EngagementRepository).Assembly);
        var repo = new EngagementRepository(engagementDb.Connections);
        await new DemoEngagementSeeder(repo, engagementDb.UnitOfWork, new SqlOutbox(), TimeProvider.System, NullLogger<DemoEngagementSeeder>.Instance)
            .SeedAsync(CancellationToken.None);
        var reviewDb = await sql.CreateDatabaseAsync(typeof(ReviewRepository).Assembly);
        return new World { EngagementDb = engagementDb, ReviewDb = reviewDb, EngagementRepo = repo, ReviewRepo = new ReviewRepository(reviewDb.Connections) };
    }

    [Fact]
    public async Task Submit_reject_rework_resubmit_approve_walks_the_full_lifecycle_and_keeps_both_cycles()
    {
        var world = await NewWorldAsync();

        // Cycle 1: submit, comment, reject.
        Assert.Equal(EngagementStatus.UnderReview, (await world.EngagementFor(Alice).SubmitAsync(Eng, null, default)).Status);
        await world.SubmittedToReviewAsync();
        await world.ReviewFor(Rohan).AddCommentAsync(Eng, new AddCommentRequest(2, "Source data is incomplete"), default);
        await world.ReviewFor(Rohan).RejectAsync(Eng, new DecisionRequest("Please complete tab 2"), default);
        await world.DecisionsToEngagementAsync();
        Assert.Equal("Rejected", await world.StatusAsync());

        // Rework: the first edit reopens it, then resubmit as cycle 2.
        await world.EditAsync(Alice);
        Assert.Equal("InProgress", await world.StatusAsync());
        await world.EngagementFor(Alice).SubmitAsync(Eng, null, default);
        await world.SubmittedToReviewAsync();
        Assert.Equal(2, await world.ReviewRepo.LatestCycleAsync(Eng, default));

        // Cycle 2: approve.
        await world.ReviewFor(Rohan).ApproveAsync(Eng, new DecisionRequest("Looks good now"), default);
        await world.DecisionsToEngagementAsync();

        Assert.Equal("Approved", await world.StatusAsync());
        var history = await world.ReviewRepo.GetDecisionsAsync(Eng, default);
        Assert.Equal(["Rejected", "Approved"], history.Select(h => h.Decision));
        Assert.Equal([1, 2], history.Select(h => h.Cycle));
        Assert.Single(await world.ReviewRepo.GetCommentsAsync(Eng, default));

        // The audit trail of status changes: Submit, Reject, StartRework, Submit, Approve.
        var reasons = (await world.EngagementDb.ScalarAsync<string>("""
            SELECT STRING_AGG(JSON_VALUE(Envelope, '$.payload.reason'), ',') WITHIN GROUP (ORDER BY Seq)
            FROM dbo.OutboxMessage WHERE EventType = 'EngagementStatusChanged' AND EngagementId = @Eng
            """, new { Eng })).Split(',');
        Assert.Equal(["Submit", "Reject", "StartRework", "Submit", "Approve"], reasons);
    }

    [Fact]
    public async Task An_approved_engagement_can_no_longer_be_submitted_or_decided()
    {
        var world = await NewWorldAsync();
        await world.EngagementFor(Alice).SubmitAsync(Eng, null, default);
        await world.SubmittedToReviewAsync();
        await world.ReviewFor(Rohan).ApproveAsync(Eng, null, default);
        await world.DecisionsToEngagementAsync();

        await Assert.ThrowsAsync<AuditFlow.BuildingBlocks.Errors.ForbiddenException>(() => world.EngagementFor(Alice).SubmitAsync(Eng, null, default));
        await Assert.ThrowsAsync<AuditFlow.BuildingBlocks.Errors.ConflictException>(() => world.ReviewFor(Rohan).RejectAsync(Eng, new DecisionRequest("too late"), default));
        Assert.Equal("Approved", await world.StatusAsync());
    }
}
