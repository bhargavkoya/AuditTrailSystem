using AuditFlow.BuildingBlocks.Messaging;
using AuditFlow.Contracts.Api;
using AuditFlow.Engagement.Api.Data;
using Microsoft.Extensions.Logging.Abstractions;
using static AuditFlow.Contracts.Api.DemoData;

namespace AuditFlow.IntegrationTests;

[Collection(SqlCollection.Name)]
[Trait("Category", "Integration")]
public class EngagementIntegrationTests(SqlServerFixture sql)
{
    private static readonly System.Reflection.Assembly EngagementAssembly = typeof(EngagementRepository).Assembly;

    private async Task<(TestDatabase Db, EngagementRepository Repo)> SeededAsync()
    {
        var db = await sql.CreateDatabaseAsync(EngagementAssembly);
        var repo = new EngagementRepository(db.Connections);
        var seeder = new DemoEngagementSeeder(repo, db.UnitOfWork, new SqlOutbox(), TimeProvider.System, NullLogger<DemoEngagementSeeder>.Instance);
        await seeder.SeedAsync(CancellationToken.None);
        return (db, repo);
    }

    private static async Task<string[]> IdsAsync(EngagementRepository repo, DemoUser user, ListScope scope = ListScope.Participating,
        EngagementStatus? status = null, string? search = null, int page = 1, int pageSize = 50)
        => (await repo.ListAsync(new ListQuery(user.UserId, scope, status, search, page, pageSize), CancellationToken.None))
            .Items.Select(i => i.EngagementId).Order().ToArray();

    [Fact]
    public async Task Seeder_creates_one_engagement_per_status_and_one_seeded_event_each()
    {
        var (db, _) = await SeededAsync();

        Assert.Equal(5, await db.ScalarAsync<int>("SELECT COUNT(DISTINCT Status) FROM dbo.Engagements"));
        Assert.Equal(5, await db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.OutboxMessage WHERE EventType = 'EngagementCreated'"));
        Assert.Equal(5, await db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.OutboxMessage WHERE Envelope LIKE '%\"seeded\":true%'"));
        Assert.Equal("ENG-1001", await db.ScalarAsync<string>("SELECT MIN(EngagementId) FROM dbo.Engagements"));
    }

    [Fact]
    public async Task Seeding_twice_does_not_duplicate()
    {
        var (db, repo) = await SeededAsync();

        await new DemoEngagementSeeder(repo, db.UnitOfWork, new SqlOutbox(), TimeProvider.System, NullLogger<DemoEngagementSeeder>.Instance)
            .SeedAsync(CancellationToken.None);

        Assert.Equal(5, await db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Engagements"));
    }

    [Fact]
    public async Task Users_only_see_engagements_they_participate_in()
    {
        var (_, repo) = await SeededAsync();

        Assert.Equal(["ENG-1001", "ENG-1002", "ENG-1003", "ENG-1004", "ENG-1005"], await IdsAsync(repo, Alice));
        Assert.Equal(["ENG-1001", "ENG-1003", "ENG-1005"], await IdsAsync(repo, Victor));
        Assert.Equal(["ENG-1002", "ENG-1005"], await IdsAsync(repo, Rohan));
    }

    [Fact]
    public async Task Scope_filters_owned_assigned_and_reviewing()
    {
        var (_, repo) = await SeededAsync();

        Assert.Equal(["ENG-1001", "ENG-1002", "ENG-1004", "ENG-1005"], await IdsAsync(repo, Alice, ListScope.Owned));
        Assert.Equal(["ENG-1003"], await IdsAsync(repo, Alice, ListScope.Assigned));
        Assert.Equal(["ENG-1001", "ENG-1003", "ENG-1004"], await IdsAsync(repo, Rachel, ListScope.Reviewing));
        Assert.Empty(await IdsAsync(repo, Victor, ListScope.Owned));
    }

    [Fact]
    public async Task Status_filter_narrows_the_list_but_counts_keep_every_status()
    {
        var (_, repo) = await SeededAsync();

        var result = await repo.ListAsync(new ListQuery(Alice.UserId, ListScope.Participating, EngagementStatus.Approved, null, 1, 50), CancellationToken.None);

        Assert.Equal("ENG-1004", Assert.Single(result.Items).EngagementId);
        Assert.Equal(1, result.Total);
        Assert.Equal(5, result.Counts.Count);
        Assert.All(result.Counts.Values, c => Assert.Equal(1, c));
    }

    [Fact]
    public async Task Search_matches_name_or_id_and_treats_wildcards_literally()
    {
        var (_, repo) = await SeededAsync();

        Assert.Equal(["ENG-1002"], await IdsAsync(repo, Alice, search: "fx controls"));
        Assert.Equal(["ENG-1004"], await IdsAsync(repo, Alice, search: "1004"));
        Assert.Empty(await IdsAsync(repo, Alice, search: "%"));
        Assert.Empty(await IdsAsync(repo, Alice, search: "_"));
    }

    [Fact]
    public async Task Paging_returns_disjoint_pages_and_the_full_total()
    {
        var (_, repo) = await SeededAsync();

        var first = await repo.ListAsync(new ListQuery(Alice.UserId, ListScope.Participating, null, null, 1, 2), CancellationToken.None);
        var second = await repo.ListAsync(new ListQuery(Alice.UserId, ListScope.Participating, null, null, 2, 2), CancellationToken.None);

        Assert.Equal(5, first.Total);
        Assert.Empty(first.Items.Select(i => i.EngagementId).Intersect(second.Items.Select(i => i.EngagementId)));
        Assert.Equal(2, second.Items.Count);
    }

    [Fact]
    public async Task Get_returns_the_callers_role_or_null_for_a_non_participant()
    {
        var (_, repo) = await SeededAsync();

        Assert.Equal(EngagementRole.Reviewer, (await repo.GetAsync("ENG-1003", Rachel.UserId, default))!.MyRole);
        Assert.Equal(EngagementRole.CoAuditor, (await repo.GetAsync("ENG-1001", Bob.UserId, default))!.MyRole);
        Assert.Null((await repo.GetAsync("ENG-1003", Rohan.UserId, default))!.MyRole);
        Assert.Null(await repo.GetAsync("ENG-9999", Rohan.UserId, default));
    }

    [Fact]
    public async Task Status_check_constraint_rejects_values_outside_the_lifecycle()
    {
        var (db, _) = await SeededAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => db.ExecuteAsync("UPDATE dbo.Engagements SET Status = 'Archived' WHERE EngagementId = 'ENG-1001'"));
    }

    [Fact]
    public async Task Rowversion_changes_on_update_which_is_the_basis_for_409_detection()
    {
        var (db, repo) = await SeededAsync();
        var before = (await repo.GetAsync("ENG-1001", Alice.UserId, default))!.Engagement.RowVer;

        await db.ExecuteAsync("UPDATE dbo.Engagements SET Name = N'Renamed' WHERE EngagementId = 'ENG-1001'");
        var after = (await repo.GetAsync("ENG-1001", Alice.UserId, default))!.Engagement.RowVer;

        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task Creating_in_a_rolled_back_transaction_leaves_no_engagement_participant_or_event()
    {
        var db = await sql.CreateDatabaseAsync(EngagementAssembly);
        var repo = new EngagementRepository(db.Connections);

        await using (var uow = await db.UnitOfWork.BeginAsync(default))
        {
            var id = await repo.NextIdAsync(uow, default);
            var engagement = new NewEngagement(id, "x", "Annual", "EMEA", Alice.UserId, Rachel.UserId, EngagementStatus.Draft, 0,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [new ParticipantRow(Alice.UserId, "Auditor", "Alice")]);
            await repo.InsertAsync(uow, engagement, default);
            await new SqlOutbox().EnqueueAsync(uow, AuditFlow.Engagement.Api.Domain.EngagementService.CreatedEvent(
                engagement, new ConfigurationRequest(["FX"], null), seeded: false), default);
            // no commit
        }

        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Engagements"));
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.EngagementParticipants"));
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.OutboxMessage"));
    }
}
