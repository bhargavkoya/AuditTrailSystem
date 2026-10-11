using AuditFlow.BuildingBlocks.Eventing;
using AuditFlow.Contracts.Api;
using AuditFlow.Contracts.Events;
using AuditFlow.Workflow.Api.Data;
using AuditFlow.Workflow.Api.Domain;
using AuditFlow.Workflow.Api.Handlers;
using Microsoft.Extensions.Logging.Abstractions;
using static AuditFlow.Contracts.Api.DemoData;

namespace AuditFlow.IntegrationTests;

internal sealed class WorkflowHarness
{
    public required TestDatabase Db { get; init; }
    public required WorkflowRepository Repository { get; init; }
    public required EngagementCreatedHandler Handler { get; init; }

    public static async Task<WorkflowHarness> CreateAsync(SqlServerFixture sql)
    {
        DateOnlyTypeHandler.Register();
        var db = await sql.CreateDatabaseAsync(typeof(WorkflowRepository).Assembly);

        await new RuleSetSeeder(db.Connections, TimeProvider.System, NullLogger<RuleSetSeeder>.Instance).SeedAsync(CancellationToken.None);

        var evaluator = new RuleEvaluator();
        var repository = new WorkflowRepository();
        var provisioning = new ProvisioningService(
            repository, new RuleSetProvider(db.Connections), new ValidationEngine(evaluator, new ReconciliationCalculator()),
            evaluator, TimeProvider.System, NullLogger<ProvisioningService>.Instance);
        return new WorkflowHarness { Db = db, Repository = repository, Handler = new EngagementCreatedHandler(provisioning) };
    }

    public static EventEnvelope<EngagementCreatedPayload> Created(
        string engagementId, EngagementStatus status = EngagementStatus.Draft, bool seeded = false,
        string[]? configTypes = null, bool reconciliation = false, bool legalReview = false)
        => EventEnvelope<EngagementCreatedPayload>.Create(EventTypes.EngagementCreated, engagementId,
            new EngagementCreatedPayload(
                "Test engagement", "Annual", "EMEA", status, seeded, Alice.UserId, Rachel.UserId,
                [new ParticipantInfo(Alice.UserId, EngagementRole.Auditor, Alice.DisplayName), new ParticipantInfo(Rachel.UserId, EngagementRole.Reviewer, Rachel.DisplayName)],
                new ConfigurationInfo(configTypes ?? ["Equities"], new Dictionary<string, bool>
                {
                    ["includeReconciliation"] = reconciliation,
                    ["requiresLegalReview"] = legalReview
                })),
            new EventActor(Alice.UserId.ToString(), Alice.DisplayName));

    public async Task ProvisionAsync(EventEnvelope<EngagementCreatedPayload> envelope)
    {
        await using var uow = await Db.UnitOfWork.BeginAsync(CancellationToken.None);
        await Handler.HandleAsync(envelope, uow, CancellationToken.None);
        await uow.CommitAsync(CancellationToken.None);
    }

    public async Task<WorkflowSnapshot> LoadAsync(string engagementId)
    {
        await using var connection = await Db.Connections.OpenAsync(CancellationToken.None);
        return (await Repository.LoadAsync(connection, null, engagementId, CancellationToken.None))!;
    }
}

[Collection(SqlCollection.Name)]
[Trait("Category", "Integration")]
public class WorkflowProvisioningTests(SqlServerFixture sql)
{
    [Fact]
    public async Task Rule_set_is_published_and_republishing_does_not_duplicate()
    {
        var h = await WorkflowHarness.CreateAsync(sql);

        await new RuleSetSeeder(h.Db.Connections, TimeProvider.System, NullLogger<RuleSetSeeder>.Instance).SeedAsync(CancellationToken.None);

        Assert.Equal(1, await h.Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.RuleSets WHERE IsActive = 1"));
    }

    [Fact]
    public async Task Provisioning_creates_sections_and_fields_with_visibility_decided_by_the_rules()
    {
        var h = await WorkflowHarness.CreateAsync(sql);

        await h.ProvisionAsync(WorkflowHarness.Created("ENG-9001", configTypes: ["Equities", "FX"], reconciliation: true));

        var snapshot = await h.LoadAsync("ENG-9001");
        Assert.Equal(13, snapshot.Sections.Count);
        Assert.Equal(1, snapshot.RuleSetVersion);
        Assert.True(snapshot.Sections["section-2-2"].IsVisible);
        Assert.False(snapshot.Sections["section-2-3"].IsVisible);
        Assert.False(snapshot.Sections["section-2-4"].IsVisible);
        Assert.True(snapshot.Sections["section-2-5"].IsVisible);
        Assert.True(snapshot.Sections["section-5-1"].IsVisible);
        Assert.False(snapshot.Sections["section-3-3"].IsVisible);
        Assert.Equal(true, snapshot.Values["section-1-1"]["cfgEquities"]);
        Assert.Equal(false, snapshot.Values["section-1-1"]["cfgDerivatives"]);
        Assert.Equal(true, snapshot.Values["section-1-1"]["optReconciliation"]);
        Assert.Null(snapshot.Values["section-1-1"]["periodStart"]);
        Assert.All(snapshot.Sections.Values, s => Assert.True(s.RowVer.Length == 8));
        Assert.NotNull(snapshot.PublishedSummaryHash);
    }

    [Fact]
    public async Task Provisioning_is_idempotent_for_a_repeated_event()
    {
        var h = await WorkflowHarness.CreateAsync(sql);

        await h.ProvisionAsync(WorkflowHarness.Created("ENG-9002"));
        await h.ProvisionAsync(WorkflowHarness.Created("ENG-9002", configTypes: ["Derivatives"])); // different message, same engagement

        Assert.Equal(1, await h.Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.EngagementConfigurations WHERE EngagementId = 'ENG-9002'"));
        Assert.Equal(13, await h.Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.WorkflowSections WHERE EngagementId = 'ENG-9002'"));
        var snapshot = await h.LoadAsync("ENG-9002");
        Assert.Equal(true, snapshot.Values["section-1-1"]["cfgEquities"]); // the first event won
    }

    [Fact]
    public async Task A_new_engagement_starts_with_errors_for_the_unfilled_requirements()
    {
        var h = await WorkflowHarness.CreateAsync(sql);

        await h.ProvisionAsync(WorkflowHarness.Created("ENG-9003"));

        Assert.False(await h.Db.ScalarAsync<bool>("SELECT ReadyToSubmit FROM dbo.ValidationSummaries WHERE EngagementId = 'ENG-9003'"));
        Assert.True(await h.Db.ScalarAsync<int>("SELECT Errors FROM dbo.ValidationSummaries WHERE EngagementId = 'ENG-9003'") > 0);
    }

    [Theory]
    [InlineData(EngagementStatus.Draft)]
    [InlineData(EngagementStatus.InProgress)]
    public async Task Early_stage_seeded_engagements_are_partly_filled_and_not_ready(EngagementStatus status)
    {
        var h = await WorkflowHarness.CreateAsync(sql);

        await h.ProvisionAsync(WorkflowHarness.Created("ENG-9004", status, seeded: true, reconciliation: true));

        var snapshot = await h.LoadAsync("ENG-9004");
        Assert.NotNull(snapshot.Values["section-1-1"]["periodStart"]);
        Assert.Equal(status == EngagementStatus.InProgress, snapshot.Values["section-2-2"]["bookValue"] is not null);
        Assert.Null(snapshot.Values["section-4-1"]["summary"]);
        Assert.False(await h.Db.ScalarAsync<bool>("SELECT ReadyToSubmit FROM dbo.ValidationSummaries WHERE EngagementId = 'ENG-9004'"));
    }

    [Theory]
    [InlineData(EngagementStatus.UnderReview)]
    [InlineData(EngagementStatus.Approved)]
    public async Task Late_stage_seeded_engagements_are_complete_once_their_attachment_is_known(EngagementStatus status)
    {
        var h = await WorkflowHarness.CreateAsync(sql);
        // AttachmentAdded for the seeded file arrives before (or after) provisioning; presence is independent of provisioning.
        await using (var uow = await h.Db.UnitOfWork.BeginAsync(CancellationToken.None))
        {
            await h.Repository.AddAttachmentPresenceAsync(uow, "ENG-9005", AttachmentScopeKeys.Section("section-2-1"), DateTimeOffset.UtcNow, CancellationToken.None);
            await uow.CommitAsync(CancellationToken.None);
        }

        await h.ProvisionAsync(WorkflowHarness.Created("ENG-9005", status, seeded: true, configTypes: ["Equities", "Derivatives"], reconciliation: true));

        Assert.True(await h.Db.ScalarAsync<bool>("SELECT ReadyToSubmit FROM dbo.ValidationSummaries WHERE EngagementId = 'ENG-9005'"));
        Assert.Equal(0, await h.Db.ScalarAsync<int>("SELECT Errors FROM dbo.ValidationSummaries WHERE EngagementId = 'ENG-9005'"));
        var snapshot = await h.LoadAsync("ENG-9005");
        Assert.NotNull(snapshot.Values["section-3-2"]["notes"]);
        Assert.NotNull(snapshot.Values["section-4-1"]["summary"]);
    }

    [Fact]
    public async Task A_rejected_seeded_engagement_carries_a_warning_level_variance()
    {
        var h = await WorkflowHarness.CreateAsync(sql);
        await using (var uow = await h.Db.UnitOfWork.BeginAsync(CancellationToken.None))
        {
            await h.Repository.AddAttachmentPresenceAsync(uow, "ENG-9006", AttachmentScopeKeys.Section("section-2-1"), DateTimeOffset.UtcNow, CancellationToken.None);
            await uow.CommitAsync(CancellationToken.None);
        }

        await h.ProvisionAsync(WorkflowHarness.Created("ENG-9006", EngagementStatus.Rejected, seeded: true, reconciliation: true));

        Assert.Equal(0, await h.Db.ScalarAsync<int>("SELECT Errors FROM dbo.ValidationSummaries WHERE EngagementId = 'ENG-9006'"));
        Assert.True(await h.Db.ScalarAsync<int>("SELECT Warnings FROM dbo.ValidationSummaries WHERE EngagementId = 'ENG-9006'") >= 1);
    }

    [Fact]
    public async Task Attachment_presence_is_recorded_once_per_scope()
    {
        var h = await WorkflowHarness.CreateAsync(sql);

        for (var i = 0; i < 2; i++)
        {
            await using var uow = await h.Db.UnitOfWork.BeginAsync(CancellationToken.None);
            await h.Repository.AddAttachmentPresenceAsync(uow, "ENG-9007", AttachmentScopeKeys.Engagement, DateTimeOffset.UtcNow, CancellationToken.None);
            await uow.CommitAsync(CancellationToken.None);
        }

        Assert.Equal(1, await h.Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.AttachmentPresence WHERE EngagementId = 'ENG-9007'"));
    }
}
