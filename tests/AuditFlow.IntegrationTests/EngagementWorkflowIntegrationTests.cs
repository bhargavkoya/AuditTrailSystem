using System.Text.Json;
using AuditFlow.BuildingBlocks.Errors;
using AuditFlow.BuildingBlocks.Eventing;
using AuditFlow.BuildingBlocks.Messaging;
using AuditFlow.BuildingBlocks.Security;
using AuditFlow.BuildingBlocks.Versioning;
using AuditFlow.Contracts.Api;
using AuditFlow.Contracts.Events;
using AuditFlow.Engagement.Api.Clients;
using AuditFlow.Engagement.Api.Data;
using AuditFlow.Engagement.Api.Domain;
using AuditFlow.Engagement.Api.Handlers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using static AuditFlow.Contracts.Api.DemoData;

namespace AuditFlow.IntegrationTests;

[Collection(SqlCollection.Name)]
[Trait("Category", "Integration")]
public class EngagementWorkflowIntegrationTests(SqlServerFixture sql)
{
    private static readonly System.Reflection.Assembly EngagementAssembly = typeof(EngagementRepository).Assembly;

    private sealed class Env
    {
        public required TestDatabase Db { get; init; }
        public required EngagementRepository Repository { get; init; }

        public EngagementService ServiceFor(DemoUser user)
        {
            var directory = new Mock<IUserDirectoryClient>();
            directory.Setup(d => d.LookupAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) => (IReadOnlyDictionary<Guid, UserSummaryDto>)Users
                    .Where(u => ids.Contains(u.UserId))
                    .ToDictionary(u => u.UserId, u => new UserSummaryDto(u.UserId, u.DisplayName, u.HomeRole)));
            var current = new Mock<ICurrentUser>();
            current.SetupGet(u => u.UserId).Returns(user.UserId);
            current.SetupGet(u => u.Name).Returns(user.DisplayName);
            current.SetupGet(u => u.HomeRole).Returns(user.HomeRole);

            var catalogue = new CreationCatalogue
            {
                PeriodTypes = ["Quarterly", "Half-Yearly", "Annual"],
                Regions = ["EMEA", "APAC", "AMER"],
                ConfigTypes = ["Equities", "Fixed Income", "Derivatives", "FX"]
            };
            return new EngagementService(Repository, directory.Object, Db.UnitOfWork, new SqlOutbox(), Options.Create(catalogue), current.Object, TimeProvider.System);
        }

        public WorkActivityHandler Activity => new(Repository, new SqlOutbox(), TimeProvider.System, NullLogger<WorkActivityHandler>.Instance);

        public async Task HandleAsync(string engagementId, DemoUser actor, string eventType = EventTypes.SectionUpdated)
        {
            await using var uow = await Db.UnitOfWork.BeginAsync(CancellationToken.None);
            await Activity.HandleAsync(engagementId, new EventActor(actor.UserId.ToString(), actor.DisplayName), eventType, uow, CancellationToken.None);
            await uow.CommitAsync(CancellationToken.None);
        }

        public Task<string> StatusAsync(string id) => Db.ScalarAsync<string>("SELECT Status FROM dbo.Engagements WHERE EngagementId = @id", new { id });

        public Task<int> EventsAsync(string type, string id)
            => Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.OutboxMessage WHERE EventType = @type AND EngagementId = @id", new { type, id });

        public async Task<JsonElement> LastPayloadAsync(string type, string id)
        {
            var envelope = await Db.ScalarAsync<string>(
                "SELECT TOP 1 Envelope FROM dbo.OutboxMessage WHERE EventType = @type AND EngagementId = @id ORDER BY Seq DESC", new { type, id });
            return JsonDocument.Parse(envelope).RootElement.Clone();
        }
    }

    private async Task<Env> SeededAsync()
    {
        var db = await sql.CreateDatabaseAsync(EngagementAssembly);
        var repo = new EngagementRepository(db.Connections);
        await new DemoEngagementSeeder(repo, db.UnitOfWork, new SqlOutbox(), TimeProvider.System, NullLogger<DemoEngagementSeeder>.Instance)
            .SeedAsync(CancellationToken.None);
        return new Env { Db = db, Repository = repo };
    }

    // Seeded: 1001 Draft, 1002 InProgress, 1003 UnderReview, 1004 Approved, 1005 Rejected.

    // ---- Seed variety ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Seeded_engagements_have_different_configurations()
    {
        var env = await SeededAsync();

        var configs = new Dictionary<string, string[]>();
        foreach (var id in new[] { "ENG-1001", "ENG-1002", "ENG-1003", "ENG-1004", "ENG-1005" })
        {
            var payload = (await env.LastPayloadAsync(EventTypes.EngagementCreated, id)).GetProperty("payload").GetProperty("configuration");
            configs[id] = payload.GetProperty("configTypes").EnumerateArray().Select(e => e.GetString()!).ToArray();
        }

        Assert.Equal(["Equities"], configs["ENG-1001"]);
        Assert.Contains("Derivatives", configs["ENG-1002"]);
        Assert.Equal(["Fixed Income"], configs["ENG-1004"]);
        Assert.Equal(5, configs.Values.Select(c => string.Join(",", c)).Distinct().Count());
        var legal = (await env.LastPayloadAsync(EventTypes.EngagementCreated, "ENG-1004")).GetProperty("payload").GetProperty("configuration").GetProperty("options");
        Assert.True(legal.GetProperty("requiresLegalReview").GetBoolean());
    }

    // ---- PATCH /engagements/{id} -------------------------------------------------------------------------------

    [Fact]
    public async Task Auditor_can_edit_a_draft_and_the_version_moves()
    {
        var env = await SeededAsync();
        var service = env.ServiceFor(Alice);
        var before = await service.GetAsync("ENG-1001", default);

        var updated = await service.UpdateAsync("ENG-1001", new UpdateEngagementRequest("Renamed audit", null, null, null), before.Version, default);

        Assert.Equal("Renamed audit", updated.Name);
        Assert.NotEqual(before.Version, updated.Version);
        var payload = (await env.LastPayloadAsync(EventTypes.EngagementUpdated, "ENG-1001")).GetProperty("payload");
        Assert.Equal(["name"], payload.GetProperty("changedFields").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(updated.Version, payload.GetProperty("version").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("periodType").ValueKind);
    }

    [Fact]
    public async Task Changing_period_and_region_publishes_the_new_rule_inputs()
    {
        var env = await SeededAsync();
        var service = env.ServiceFor(Alice);
        var before = await service.GetAsync("ENG-1001", default);

        await service.UpdateAsync("ENG-1001", new UpdateEngagementRequest(null, "Annual", "APAC", null), before.Version, default);

        var payload = (await env.LastPayloadAsync(EventTypes.EngagementUpdated, "ENG-1001")).GetProperty("payload");
        Assert.Equal("Annual", payload.GetProperty("periodType").GetString());
        Assert.Equal("APAC", payload.GetProperty("region").GetString());
    }

    [Fact]
    public async Task A_stale_version_is_a_conflict_with_the_current_version()
    {
        var env = await SeededAsync();
        var service = env.ServiceFor(Alice);
        var stale = (await service.GetAsync("ENG-1001", default)).Version;
        var winner = await env.ServiceFor(Bob).UpdateAsync("ENG-1001", new UpdateEngagementRequest("Bob was first", null, null, null), stale, default);

        var conflict = await Assert.ThrowsAsync<ConcurrencyException>(() =>
            service.UpdateAsync("ENG-1001", new UpdateEngagementRequest("Alice too late", null, null, null), stale, default));

        Assert.Equal(winner.Version, conflict.CurrentVersion);
        Assert.Equal("Bob was first", (await service.GetAsync("ENG-1001", default)).Name);
    }

    [Fact]
    public async Task A_missing_version_is_a_validation_error()
    {
        var env = await SeededAsync();

        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            env.ServiceFor(Alice).UpdateAsync("ENG-1001", new UpdateEngagementRequest("x", null, null, null), "", default));
    }

    [Fact]
    public async Task A_request_that_changes_nothing_is_a_no_op()
    {
        var env = await SeededAsync();
        var service = env.ServiceFor(Alice);
        var before = await service.GetAsync("ENG-1001", default);
        var events = await env.EventsAsync(EventTypes.EngagementUpdated, "ENG-1001");

        var same = await service.UpdateAsync("ENG-1001", new UpdateEngagementRequest(before.Name, before.PeriodType, null, null), before.Version, default);

        Assert.Equal(before.Version, same.Version);
        Assert.Equal(events, await env.EventsAsync(EventTypes.EngagementUpdated, "ENG-1001"));
    }

    [Theory]
    [InlineData("ENG-1002")] // In Progress
    [InlineData("ENG-1003")] // Under Review
    [InlineData("ENG-1004")] // Approved
    [InlineData("ENG-1005")] // Rejected
    public async Task Details_are_locked_after_the_draft_stage(string id)
    {
        var env = await SeededAsync();
        var service = env.ServiceFor(Alice);
        var version = (await service.GetAsync(id, default)).Version;

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.UpdateAsync(id, new UpdateEngagementRequest("nope", null, null, null), version, default));
    }

    [Theory]
    [InlineData("Rachel")]
    [InlineData("Victor")]
    public async Task Only_editors_can_change_details(string who)
    {
        var env = await SeededAsync();
        var user = who == "Rachel" ? Rachel : Victor;
        var version = (await env.ServiceFor(Alice).GetAsync("ENG-1001", default)).Version;

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            env.ServiceFor(user).UpdateAsync("ENG-1001", new UpdateEngagementRequest("nope", null, null, null), version, default));
    }

    [Fact]
    public async Task Invalid_values_are_rejected()
    {
        var env = await SeededAsync();
        var service = env.ServiceFor(Alice);
        var version = (await service.GetAsync("ENG-1001", default)).Version;

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() =>
            service.UpdateAsync("ENG-1001", new UpdateEngagementRequest(" ", "Weekly", "Mars", Alice.UserId), version, default));

        Assert.Equal(["name", "periodType", "region", "reviewerUserId"], ex.Errors.Keys.Order());
    }

    [Fact]
    public async Task Changing_the_reviewer_replaces_the_participant_row()
    {
        var env = await SeededAsync();
        var service = env.ServiceFor(Alice);
        var version = (await service.GetAsync("ENG-1001", default)).Version;

        var updated = await service.UpdateAsync("ENG-1001", new UpdateEngagementRequest(null, null, null, Rohan.UserId), version, default);

        Assert.Equal(Rohan.UserId, updated.Reviewer.UserId);
        var participants = await service.GetParticipantsAsync("ENG-1001", default);
        Assert.Single(participants, p => p.Role == EngagementRole.Reviewer && p.UserId == Rohan.UserId);
        Assert.DoesNotContain(participants, p => p.UserId == Rachel.UserId);
        // The previous reviewer lost access.
        await Assert.ThrowsAsync<ForbiddenException>(() => env.ServiceFor(Rachel).GetAsync("ENG-1001", default));
    }

    [Fact]
    public async Task The_new_reviewer_must_be_a_reviewer_and_not_already_a_participant()
    {
        var env = await SeededAsync();
        var service = env.ServiceFor(Alice);
        var version = (await service.GetAsync("ENG-1001", default)).Version;

        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            service.UpdateAsync("ENG-1001", new UpdateEngagementRequest(null, null, null, Bob.UserId), version, default)); // a Co-Auditor
        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            service.UpdateAsync("ENG-1001", new UpdateEngagementRequest(null, null, null, Guid.NewGuid()), version, default)); // unknown
    }

    [Fact]
    public async Task Edit_details_is_only_offered_by_the_backend_for_drafts()
    {
        var env = await SeededAsync();
        var service = env.ServiceFor(Alice);

        Assert.Contains(EngagementActions.EditDetails, (await service.GetAsync("ENG-1001", default)).AllowedActions);
        Assert.DoesNotContain(EngagementActions.EditDetails, (await service.GetAsync("ENG-1002", default)).AllowedActions);
    }

    // ---- Work activity => status transitions -----------------------------------------------------------------------

    [Fact]
    public async Task First_edit_moves_a_draft_to_in_progress_and_announces_it()
    {
        var env = await SeededAsync();

        await env.HandleAsync("ENG-1001", Alice);

        Assert.Equal("InProgress", await env.StatusAsync("ENG-1001"));
        var payload = (await env.LastPayloadAsync(EventTypes.EngagementStatusChanged, "ENG-1001")).GetProperty("payload");
        Assert.Equal("Draft", payload.GetProperty("from").GetString());
        Assert.Equal("InProgress", payload.GetProperty("to").GetString());
        Assert.Equal("StartWork", payload.GetProperty("reason").GetString());
        Assert.False(string.IsNullOrEmpty(payload.GetProperty("version").GetString()));
    }

    [Fact]
    public async Task First_edit_after_a_rejection_moves_it_back_to_in_progress()
    {
        var env = await SeededAsync();

        await env.HandleAsync("ENG-1005", Bob, EventTypes.ConfigurationChanged); // Bob is a Co-Auditor on 1005

        Assert.Equal("InProgress", await env.StatusAsync("ENG-1005"));
        var payload = (await env.LastPayloadAsync(EventTypes.EngagementStatusChanged, "ENG-1005")).GetProperty("payload");
        Assert.Equal("Rejected", payload.GetProperty("from").GetString());
        Assert.Equal("StartRework", payload.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Further_edits_only_refresh_last_activity()
    {
        var env = await SeededAsync();
        var before = await env.Db.ScalarAsync<DateTimeOffset>("SELECT LastActivityAt FROM dbo.Engagements WHERE EngagementId = 'ENG-1002'");

        await env.HandleAsync("ENG-1002", Alice);

        Assert.Equal("InProgress", await env.StatusAsync("ENG-1002"));
        Assert.Equal(0, await env.EventsAsync(EventTypes.EngagementStatusChanged, "ENG-1002"));
        Assert.True(await env.Db.ScalarAsync<DateTimeOffset>("SELECT LastActivityAt FROM dbo.Engagements WHERE EngagementId = 'ENG-1002'") > before);
    }

    [Theory]
    [InlineData("ENG-1003", "UnderReview")]
    [InlineData("ENG-1004", "Approved")]
    public async Task Edits_for_locked_engagements_are_ignored(string id, string expectedStatus)
    {
        var env = await SeededAsync();

        await env.HandleAsync(id, Alice);

        Assert.Equal(expectedStatus, await env.StatusAsync(id));
        Assert.Equal(0, await env.EventsAsync(EventTypes.EngagementStatusChanged, id));
    }

    [Fact]
    public async Task A_non_editor_actor_never_starts_work()
    {
        var env = await SeededAsync();

        await env.HandleAsync("ENG-1001", Victor); // a Viewer cannot cause a transition (defence in depth)

        Assert.Equal("Draft", await env.StatusAsync("ENG-1001"));
        Assert.Equal(0, await env.EventsAsync(EventTypes.EngagementStatusChanged, "ENG-1001"));
    }

    [Fact]
    public async Task Concurrent_activity_events_cause_exactly_one_transition()
    {
        var env = await SeededAsync();

        await Task.WhenAll(Enumerable.Range(0, 6).Select(i => env.HandleAsync("ENG-1001", i % 2 == 0 ? Alice : Bob,
            i % 2 == 0 ? EventTypes.SectionUpdated : EventTypes.ConfigurationChanged)));

        Assert.Equal("InProgress", await env.StatusAsync("ENG-1001"));
        Assert.Equal(1, await env.EventsAsync(EventTypes.EngagementStatusChanged, "ENG-1001"));
    }

    [Fact]
    public async Task Activity_for_an_unknown_engagement_fails_so_the_broker_redelivers()
    {
        var env = await SeededAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => env.HandleAsync("ENG-0000", Alice));
    }

    [Fact]
    public async Task A_reopened_engagement_exposes_the_edit_capability_again()
    {
        var env = await SeededAsync();
        Assert.Equal(EngagementStatus.Rejected, (await env.ServiceFor(Alice).GetAccessAsync("ENG-1005", default)).Status);

        await env.HandleAsync("ENG-1005", Alice);

        var access = await env.ServiceFor(Alice).GetAccessAsync("ENG-1005", default);
        Assert.Equal(EngagementStatus.InProgress, access.Status);
        Assert.Contains(Capabilities.Submit, access.Capabilities);
        Assert.Equal(RowVersion.ToToken(await env.Db.ScalarAsync<byte[]>("SELECT RowVer FROM dbo.Engagements WHERE EngagementId = 'ENG-1005'")), access.Version);
    }
}
