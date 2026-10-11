using System.Text.Json;
using AuditFlow.BuildingBlocks.Access;
using AuditFlow.BuildingBlocks.Errors;
using AuditFlow.BuildingBlocks.Eventing;
using AuditFlow.BuildingBlocks.Messaging;
using AuditFlow.BuildingBlocks.Security;
using AuditFlow.Contracts.Api;
using AuditFlow.Contracts.Events;
using AuditFlow.Review.Api.Clients;
using AuditFlow.Review.Api.Data;
using AuditFlow.Review.Api.Domain;
using AuditFlow.Review.Api.Handlers;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using static AuditFlow.Contracts.Api.DemoData;

namespace AuditFlow.IntegrationTests;

[Collection(SqlCollection.Name)]
[Trait("Category", "Integration")]
public class ReviewIntegrationTests(SqlServerFixture sql)
{
    private const string Eng = "ENG-9001";

    private static readonly string[] ReviewerCaps =
        [Capabilities.View, Capabilities.AttachView, Capabilities.CommentView, Capabilities.Comment, Capabilities.Review];

    private sealed class Env
    {
        public required TestDatabase Db { get; init; }
        public required ReviewRepository Repository { get; init; }

        /// <summary>Capabilities the (mocked) engagement-service grants; reviewer by default.</summary>
        public string[] Caps { get; set; } = ReviewerCaps;
        public EngagementRole Role { get; set; } = EngagementRole.Reviewer;
        public Func<int, TabViewDto>? TabFactory { get; set; }
        public bool TabListDown { get; set; }
        public bool ValidationDown { get; set; }
        public HashSet<int> DownTabs { get; } = [];

        public ReviewService ServiceFor(DemoUser user)
        {
            var engagements = new Mock<IEngagementClient>();
            engagements.Setup(e => e.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Detail(user));

            var view = new Mock<IWorkflowViewClient>();
            view.Setup(w => w.GetTabsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() => TabListDown
                ? throw new HttpRequestException("down")
                : Task.FromResult(new TabsDto(true, Enumerable.Range(1, 5).Select(n => new TabSummaryDto(n, $"Tab {n}", new IssueCountsDto(0, 0, 0))).ToList())));
            view.Setup(w => w.GetTabAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns((string _, int n, CancellationToken _) => DownTabs.Contains(n)
                ? throw new HttpRequestException("down")
                : Task.FromResult(new TabViewDto(n, $"Tab {n}", false, [], [], null)));
            view.Setup(w => w.GetValidationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() => ValidationDown
                ? throw new HttpRequestException("down")
                : Task.FromResult(new ValidationSummaryDto(new IssueCountsDto(0, 1, 0), true, [], [])));

            var access = new Mock<IEngagementAccessClient>();
            access.Setup(a => a.GetAccessAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string id, CancellationToken _) => new EngagementAccessDto(id, Role, EngagementStatus.UnderReview, "v", Caps));

            var current = new Mock<ICurrentUser>();
            current.SetupGet(u => u.UserId).Returns(user.UserId);
            current.SetupGet(u => u.Name).Returns(user.DisplayName);
            current.SetupGet(u => u.HomeRole).Returns(user.HomeRole);

            return new ReviewService(Repository, engagements.Object, view.Object, access.Object, Db.UnitOfWork, new SqlOutbox(),
                current.Object, TimeProvider.System, NullLogger<ReviewService>.Instance);
        }

        private EngagementDetailDto Detail(DemoUser user) => new(
            Eng, "Test engagement", "EMEA", "Quarterly", EngagementStatus.UnderReview,
            new PersonDto(Alice.UserId, Alice.DisplayName), new PersonDto(Rachel.UserId, Rachel.DisplayName),
            Role, [], Caps, "v", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        public async Task SubmitAsync(int cycle, EventActor? actor = null)
        {
            var envelope = EventEnvelope<EngagementSubmittedPayload>.Create(
                EventTypes.EngagementSubmitted, Eng, new EngagementSubmittedPayload(Alice.UserId, Alice.DisplayName, cycle), actor);
            await RunAsync("review", envelope, new EngagementSubmittedHandler(Repository));
        }

        // Mirrors the idempotent consumer pipeline: processed-marker + handler in one transaction.
        public async Task<bool> RunAsync<T>(string consumer, EventEnvelope<T> envelope, IEventHandler<T> handler)
        {
            await using var uow = await Db.UnitOfWork.BeginAsync(CancellationToken.None);
            if (!await new SqlProcessedMessageStore().TryMarkAsync(consumer, envelope.EventId, uow, CancellationToken.None)) return false;
            await handler.HandleAsync(envelope, uow, CancellationToken.None);
            await uow.CommitAsync(CancellationToken.None);
            return true;
        }

        public Task<int> EventsAsync(string type) => Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.OutboxMessage WHERE EventType = @type", new { type });

        public async Task<JsonElement> LastEventAsync(string type)
        {
            var envelope = await Db.ScalarAsync<string>("SELECT TOP 1 Envelope FROM dbo.OutboxMessage WHERE EventType = @type ORDER BY Seq DESC", new { type });
            return JsonDocument.Parse(envelope).RootElement.Clone();
        }
    }

    private async Task<Env> NewEnvAsync(bool submitted = true)
    {
        var db = await sql.CreateDatabaseAsync(typeof(ReviewRepository).Assembly);
        var env = new Env { Db = db, Repository = new ReviewRepository(db.Connections) };
        if (submitted) await env.SubmitAsync(1);
        return env;
    }

    // ---- Cycles ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Submitting_opens_a_review_cycle_and_a_redelivery_does_not_duplicate_it()
    {
        var env = await NewEnvAsync(submitted: false);
        var envelope = EventEnvelope<EngagementSubmittedPayload>.Create(
            EventTypes.EngagementSubmitted, Eng, new EngagementSubmittedPayload(Alice.UserId, Alice.DisplayName, 1));
        var handler = new EngagementSubmittedHandler(env.Repository);

        Assert.True(await env.RunAsync("review", envelope, handler));
        Assert.False(await env.RunAsync("review", envelope, handler));

        Assert.Equal(1, await env.Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReviewCycles WHERE EngagementId = @Eng", new { Eng }));
    }

    [Fact]
    public async Task The_same_cycle_arriving_under_a_new_message_id_is_still_a_single_row()
    {
        var env = await NewEnvAsync();

        await env.SubmitAsync(1);

        Assert.Equal(1, await env.Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReviewCycles WHERE EngagementId = @Eng", new { Eng }));
    }

    // ---- Comments ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_reviewer_comment_is_stored_on_the_current_cycle_and_the_event_carries_no_text()
    {
        var env = await NewEnvAsync();

        var comment = await env.ServiceFor(Rachel).AddCommentAsync(Eng, new AddCommentRequest(3, "  Please explain the gap.  "), default);

        Assert.Equal("Please explain the gap.", comment.Body);
        Assert.Equal(1, comment.Cycle);
        var evt = await env.LastEventAsync(EventTypes.ReviewerCommentAdded);
        Assert.Equal(3, evt.GetProperty("payload").GetProperty("tabNo").GetInt32());
        Assert.DoesNotContain("explain", evt.GetRawText());
    }

    [Theory]
    [InlineData(0, "x")]
    [InlineData(6, "x")]
    [InlineData(2, "")]
    [InlineData(2, "   ")]
    public async Task Invalid_comments_are_rejected(int tab, string body)
    {
        var env = await NewEnvAsync();

        await Assert.ThrowsAsync<ValidationFailedException>(() => env.ServiceFor(Rachel).AddCommentAsync(Eng, new AddCommentRequest(tab, body), default));

        Assert.Equal(0, await env.Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.TabComments"));
    }

    [Fact]
    public async Task A_comment_over_the_length_limit_is_rejected()
    {
        var env = await NewEnvAsync();

        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            env.ServiceFor(Rachel).AddCommentAsync(Eng, new AddCommentRequest(1, new string('x', ReviewRules.MaxTextLength + 1)), default));
    }

    [Fact]
    public async Task Without_the_comment_capability_commenting_is_forbidden()
    {
        var env = await NewEnvAsync();
        env.Caps = [Capabilities.View, Capabilities.CommentView]; // e.g. an auditor, or the wrong status

        await Assert.ThrowsAsync<ForbiddenException>(() => env.ServiceFor(Alice).AddCommentAsync(Eng, new AddCommentRequest(1, "hello"), default));
    }

    [Fact]
    public async Task Commenting_before_the_review_cycle_exists_is_a_conflict()
    {
        var env = await NewEnvAsync(submitted: false);

        await Assert.ThrowsAsync<ConflictException>(() => env.ServiceFor(Rachel).AddCommentAsync(Eng, new AddCommentRequest(1, "early"), default));
    }

    // ---- Decisions ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Rejecting_without_a_comment_is_a_bad_request_and_records_nothing()
    {
        var env = await NewEnvAsync();
        var service = env.ServiceFor(Rachel);

        await Assert.ThrowsAsync<BadHttpRequestException>(() => service.RejectAsync(Eng, new DecisionRequest(null), default));
        await Assert.ThrowsAsync<BadHttpRequestException>(() => service.RejectAsync(Eng, new DecisionRequest("   "), default));
        await Assert.ThrowsAsync<BadHttpRequestException>(() => service.RejectAsync(Eng, null, default));

        Assert.Equal(0, await env.Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReviewDecisions"));
        Assert.Equal(0, await env.EventsAsync(EventTypes.ReviewRejected));
    }

    [Fact]
    public async Task Rejecting_with_a_comment_records_the_decision_and_publishes_an_event_without_the_text()
    {
        var env = await NewEnvAsync();

        var result = await env.ServiceFor(Rachel).RejectAsync(Eng, new DecisionRequest("Missing source data"), default);

        Assert.Equal("Rejected", result.Decision);
        Assert.Equal("Missing source data", await env.Db.ScalarAsync<string>("SELECT Comment FROM dbo.ReviewDecisions WHERE DecisionId = @Id", new { Id = result.DecisionId }));
        var evt = await env.LastEventAsync(EventTypes.ReviewRejected);
        Assert.Equal(Rachel.UserId, evt.GetProperty("payload").GetProperty("reviewerId").GetGuid());
        Assert.Equal(result.DecisionId, evt.GetProperty("payload").GetProperty("decisionId").GetGuid());
        Assert.DoesNotContain("source data", evt.GetRawText());
    }

    [Fact]
    public async Task Approving_needs_no_comment_and_publishes_ReviewApproved()
    {
        var env = await NewEnvAsync();

        var result = await env.ServiceFor(Rachel).ApproveAsync(Eng, null, default);

        Assert.Equal("Approved", result.Decision);
        Assert.Equal(1, await env.EventsAsync(EventTypes.ReviewApproved));
    }

    [Fact]
    public async Task A_second_decision_for_the_same_cycle_is_a_conflict_and_emits_no_second_event()
    {
        var env = await NewEnvAsync();
        var service = env.ServiceFor(Rachel);
        await service.ApproveAsync(Eng, null, default);

        await Assert.ThrowsAsync<ConflictException>(() => service.RejectAsync(Eng, new DecisionRequest("changed my mind"), default));

        Assert.Equal(1, await env.Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReviewDecisions"));
        Assert.Equal(0, await env.EventsAsync(EventTypes.ReviewRejected));
    }

    [Fact]
    public async Task Two_simultaneous_decisions_produce_exactly_one()
    {
        var env = await NewEnvAsync();

        var results = await Task.WhenAll(Attempt(env.ServiceFor(Rachel).ApproveAsync(Eng, null, default)), Attempt(env.ServiceFor(Rachel).RejectAsync(Eng, new DecisionRequest("no"), default)));

        Assert.Single(results, ok => ok);
        Assert.Equal(1, await env.Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReviewDecisions"));

        static async Task<bool> Attempt(Task<DecisionResultDto> task)
        {
            try { await task; return true; }
            catch (ConflictException) { return false; }
        }
    }

    [Fact]
    public async Task Without_the_review_capability_deciding_is_forbidden()
    {
        var env = await NewEnvAsync();
        env.Caps = [Capabilities.View, Capabilities.CommentView]; // auditor, viewer, or not Under Review

        await Assert.ThrowsAsync<ForbiddenException>(() => env.ServiceFor(Alice).ApproveAsync(Eng, null, default));
        await Assert.ThrowsAsync<ForbiddenException>(() => env.ServiceFor(Alice).RejectAsync(Eng, new DecisionRequest("no"), default));
    }

    [Fact]
    public async Task A_new_cycle_after_a_rejection_can_be_decided_again()
    {
        var env = await NewEnvAsync();
        await env.ServiceFor(Rachel).RejectAsync(Eng, new DecisionRequest("rework"), default);
        await env.SubmitAsync(2);

        var second = await env.ServiceFor(Rachel).ApproveAsync(Eng, null, default);

        Assert.Equal(2, second.Cycle);
        var view = await env.ServiceFor(Rachel).GetAsync(Eng, default);
        Assert.Equal(["Rejected", "Approved"], view.History.Select(h => h.Decision));
    }

    [Fact]
    public async Task The_database_itself_refuses_a_rejection_without_a_comment()
    {
        var env = await NewEnvAsync();

        await Assert.ThrowsAsync<SqlException>(() => env.Db.ExecuteAsync($"""
            INSERT INTO dbo.ReviewDecisions (DecisionId, EngagementId, Cycle, Decision, ReviewerUserId, ReviewerName, Comment, DecidedAt)
            VALUES (NEWID(), '{Eng}', 1, N'Rejected', NEWID(), N'x', N'   ', SYSDATETIMEOFFSET())
            """));
    }

    // ---- The composed review screen ----------------------------------------------------------------------------

    [Fact]
    public async Task The_reviewer_sees_five_read_only_tabs_the_summary_and_the_actions_the_backend_allows()
    {
        var env = await NewEnvAsync();

        var view = await env.ServiceFor(Rachel).GetAsync(Eng, default);

        Assert.Equal(5, view.Tabs.Count);
        Assert.All(view.Tabs, t => Assert.True(t.Available));
        Assert.NotNull(view.Validation);
        Assert.True(view.CanComment);
        Assert.True(view.CanDecide);
        Assert.Equal(1, view.Cycle);
    }

    [Fact]
    public async Task After_a_decision_the_cycle_can_no_longer_be_decided()
    {
        var env = await NewEnvAsync();
        await env.ServiceFor(Rachel).ApproveAsync(Eng, null, default);

        var view = await env.ServiceFor(Rachel).GetAsync(Eng, default);

        Assert.False(view.CanDecide);
        Assert.Single(view.History);
    }

    [Fact]
    public async Task Before_the_first_submission_the_screen_is_read_only()
    {
        var env = await NewEnvAsync(submitted: false);

        var view = await env.ServiceFor(Rachel).GetAsync(Eng, default);

        Assert.Equal(0, view.Cycle);
        Assert.False(view.CanComment);
        Assert.False(view.CanDecide);
    }

    [Fact]
    public async Task An_auditor_reads_the_review_but_gets_no_comment_or_decision_controls()
    {
        var env = await NewEnvAsync();
        await env.ServiceFor(Rachel).AddCommentAsync(Eng, new AddCommentRequest(2, "Check balances"), default);
        env.Role = EngagementRole.Auditor;
        env.Caps = [Capabilities.View, Capabilities.AttachView, Capabilities.CommentView];

        var view = await env.ServiceFor(Alice).GetAsync(Eng, default);

        Assert.False(view.CanComment);
        Assert.False(view.CanDecide);
        Assert.Single(view.Comments);
    }

    [Fact]
    public async Task A_viewer_cannot_open_the_review_screen()
    {
        var env = await NewEnvAsync();
        env.Role = EngagementRole.Viewer;
        env.Caps = [Capabilities.View];

        await Assert.ThrowsAsync<ForbiddenException>(() => env.ServiceFor(Victor).GetAsync(Eng, default));
    }

    [Fact]
    public async Task One_failing_tab_degrades_that_tab_only()
    {
        var env = await NewEnvAsync();
        env.DownTabs.Add(3);

        var view = await env.ServiceFor(Rachel).GetAsync(Eng, default);

        Assert.False(view.Tabs.Single(t => t.TabNo == 3).Available);
        Assert.Null(view.Tabs.Single(t => t.TabNo == 3).Content);
        Assert.Equal(4, view.Tabs.Count(t => t.Available));
        Assert.NotNull(view.Validation);
    }

    [Fact]
    public async Task When_workflow_is_unavailable_the_screen_still_loads_with_comments_and_history()
    {
        var env = await NewEnvAsync();
        await env.ServiceFor(Rachel).AddCommentAsync(Eng, new AddCommentRequest(1, "note"), default);
        env.TabListDown = true;
        env.ValidationDown = true;

        var view = await env.ServiceFor(Rachel).GetAsync(Eng, default);

        Assert.Equal(5, view.Tabs.Count);
        Assert.All(view.Tabs, t => Assert.False(t.Available));
        Assert.Null(view.Validation);
        Assert.Single(view.Comments);
        Assert.True(view.CanComment);
    }

    // ---- Demo seed ---------------------------------------------------------------------------------------------

    private static EventEnvelope<EngagementCreatedPayload> Seeded(EngagementStatus status)
        => EventEnvelope<EngagementCreatedPayload>.Create(EventTypes.EngagementCreated, Eng,
            new EngagementCreatedPayload("Seed", "Quarterly", "EMEA", status, true, Alice.UserId, Rachel.UserId,
                [new ParticipantInfo(Alice.UserId, EngagementRole.Auditor, Alice.DisplayName), new ParticipantInfo(Rachel.UserId, EngagementRole.Reviewer, Rachel.DisplayName)],
                new ConfigurationInfo(["Equities"], new Dictionary<string, bool>())));

    [Theory]
    [InlineData(EngagementStatus.UnderReview, 2, 0)]
    [InlineData(EngagementStatus.Approved, 1, 1)]
    [InlineData(EngagementStatus.Rejected, 2, 1)]
    public async Task Seeded_late_stage_engagements_get_review_history(EngagementStatus status, int comments, int decisions)
    {
        var env = await NewEnvAsync(submitted: false);

        await env.RunAsync("review", Seeded(status), new SeedReviewHandler(env.Repository, NullLogger<SeedReviewHandler>.Instance));

        Assert.Equal(comments, await env.Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.TabComments"));
        Assert.Equal(decisions, await env.Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReviewDecisions"));
        Assert.Equal(1, await env.Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReviewCycles"));
        Assert.Equal(0, await env.EventsAsync(EventTypes.ReviewApproved) + await env.EventsAsync(EventTypes.ReviewRejected)); // seeds do not emit decisions
    }

    [Theory]
    [InlineData(EngagementStatus.Draft)]
    [InlineData(EngagementStatus.InProgress)]
    public async Task Seeding_early_stage_engagements_adds_nothing(EngagementStatus status)
    {
        var env = await NewEnvAsync(submitted: false);

        await env.RunAsync("review", Seeded(status), new SeedReviewHandler(env.Repository, NullLogger<SeedReviewHandler>.Instance));

        Assert.Equal(0, await env.Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReviewCycles"));
    }
}
