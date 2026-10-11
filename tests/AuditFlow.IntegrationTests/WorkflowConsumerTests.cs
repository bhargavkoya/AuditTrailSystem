using AuditFlow.BuildingBlocks.Access;
using AuditFlow.BuildingBlocks.Data;
using AuditFlow.BuildingBlocks.Eventing;
using AuditFlow.BuildingBlocks.Messaging;
using AuditFlow.Contracts.Api;
using AuditFlow.Contracts.Events;
using AuditFlow.Workflow.Api.Data;
using AuditFlow.Workflow.Api.Domain;
using AuditFlow.Workflow.Api.Handlers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using static AuditFlow.Contracts.Api.DemoData;

namespace AuditFlow.IntegrationTests;

[Collection(SqlCollection.Name)]
[Trait("Category", "Integration")]
public class WorkflowConsumerTests(SqlServerFixture sql)
{
    private const string Eng = "ENG-7101";

    private sealed class Handlers(WorkflowApi api)
    {
        private readonly RuleSetProvider ruleSets = new(api.Db.Connections);
        private readonly Recomputer recomputer = new(api.Harness.Repository, new ValidationEngine(new RuleEvaluator(), new ReconciliationCalculator()), new SqlOutbox(), TimeProvider.System);

        public AttachmentAddedHandler Attachment => new(api.Harness.Repository, ruleSets, recomputer, TimeProvider.System, NullLogger<AttachmentAddedHandler>.Instance);
        public EngagementUpdatedHandler Updated => new(api.Harness.Repository, ruleSets, recomputer);

        // Mirrors the idempotent consumer pipeline: processed-marker + handler in one transaction.
        public async Task<bool> RunAsync<T>(string consumer, EventEnvelope<T> envelope, IEventHandler<T> handler)
        {
            await using var uow = await api.Db.UnitOfWork.BeginAsync(CancellationToken.None);
            if (!await new SqlProcessedMessageStore().TryMarkAsync(consumer, envelope.EventId, uow, CancellationToken.None)) return false;
            await handler.HandleAsync(envelope, uow, CancellationToken.None);
            await uow.CommitAsync(CancellationToken.None);
            return true;
        }
    }

    private static EventEnvelope<AttachmentAddedPayload> Added(string engagementId, AttachmentScope scope, int? tab = null, string? section = null)
        => EventEnvelope<AttachmentAddedPayload>.Create(EventTypes.AttachmentAdded, engagementId,
            new AttachmentAddedPayload(scope, tab, section, Guid.NewGuid(), "file.pdf"), new EventActor(Alice.UserId.ToString(), Alice.DisplayName));

    private static EventEnvelope<EngagementUpdatedPayload> Updated(string engagementId, string? period = null, string? region = null)
        => EventEnvelope<EngagementUpdatedPayload>.Create(EventTypes.EngagementUpdated, engagementId,
            new EngagementUpdatedPayload(["periodType"], "AAAA", period, region), new EventActor(Alice.UserId.ToString(), Alice.DisplayName));

    [Fact]
    public async Task AttachmentAdded_satisfies_the_attachment_requirement_and_publishes_the_new_summary()
    {
        var api = await WorkflowApi.CreateAsync(sql, Eng);
        var handlers = new Handlers(api);
        var before = await api.Alice_().GetValidationSummaryAsync(Eng, default);
        Assert.Contains(before.Issues, i => i.Code == "ATTACHMENT_REQUIRED" && i.SectionKey == "section-2-1");
        Assert.False((await api.Alice_().GetTabAsync(Eng, 2, default)).Sections[0].Attachment!.HasCurrent);

        await handlers.RunAsync("workflow", Added(Eng, AttachmentScope.Section, section: "section-2-1"), handlers.Attachment);

        var after = await api.Alice_().GetValidationSummaryAsync(Eng, default);
        Assert.DoesNotContain(after.Issues, i => i.Code == "ATTACHMENT_REQUIRED");
        Assert.True((await api.Alice_().GetTabAsync(Eng, 2, default)).Sections[0].Attachment!.HasCurrent);
        Assert.Equal(1, await api.EventCountAsync("ValidationSummaryChanged"));
    }

    [Fact]
    public async Task AttachmentAdded_is_idempotent_for_a_redelivered_message()
    {
        var api = await WorkflowApi.CreateAsync(sql, Eng);
        var handlers = new Handlers(api);
        var message = Added(Eng, AttachmentScope.Section, section: "section-2-1");

        Assert.True(await handlers.RunAsync("workflow", message, handlers.Attachment));
        Assert.False(await handlers.RunAsync("workflow", message, handlers.Attachment));

        Assert.Equal(1, await api.EventCountAsync("ValidationSummaryChanged"));
        Assert.Equal(1, await api.Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.AttachmentPresence WHERE EngagementId = @id", new { id = Eng }));
    }

    [Fact]
    public async Task A_replacement_upload_for_the_same_scope_changes_nothing()
    {
        var api = await WorkflowApi.CreateAsync(sql, Eng);
        var handlers = new Handlers(api);
        await handlers.RunAsync("workflow", Added(Eng, AttachmentScope.Section, section: "section-2-1"), handlers.Attachment);
        var summaryEvents = await api.EventCountAsync("ValidationSummaryChanged");

        await handlers.RunAsync("workflow", Added(Eng, AttachmentScope.Section, section: "section-2-1"), handlers.Attachment); // new file, same scope

        Assert.Equal(summaryEvents, await api.EventCountAsync("ValidationSummaryChanged"));
        Assert.Equal(1, await api.Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.AttachmentPresence WHERE EngagementId = @id", new { id = Eng }));
    }

    [Fact]
    public async Task Tab_and_engagement_scope_attachments_are_recorded_under_their_own_keys()
    {
        var api = await WorkflowApi.CreateAsync(sql, Eng);
        var handlers = new Handlers(api);

        await handlers.RunAsync("workflow", Added(Eng, AttachmentScope.Tab, tab: 3), handlers.Attachment);
        await handlers.RunAsync("workflow", Added(Eng, AttachmentScope.Engagement), handlers.Attachment);

        var keys = await api.Db.ScalarAsync<string>("SELECT STRING_AGG(ScopeKey, ',') WITHIN GROUP (ORDER BY ScopeKey) FROM dbo.AttachmentPresence WHERE EngagementId = @id", new { id = Eng });
        Assert.Equal("engagement,tab:3", keys);
    }

    [Fact]
    public async Task AttachmentAdded_before_provisioning_is_remembered_and_used_at_provisioning()
    {
        var harness = await WorkflowHarness.CreateAsync(sql);
        var api = new WorkflowApi { Harness = harness };
        var handlers = new Handlers(api);

        await handlers.RunAsync("workflow", Added("ENG-7102", AttachmentScope.Section, section: "section-2-1"), handlers.Attachment);
        await harness.ProvisionAsync(WorkflowHarness.Created("ENG-7102"));

        var summary = await api.Alice_().GetValidationSummaryAsync("ENG-7102", default);
        Assert.DoesNotContain(summary.Issues, i => i.Code == "ATTACHMENT_REQUIRED");
    }

    [Fact]
    public async Task AttachmentAdded_with_a_malformed_scope_is_rejected()
    {
        var api = await WorkflowApi.CreateAsync(sql, Eng);
        var handlers = new Handlers(api);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handlers.RunAsync("workflow", Added(Eng, AttachmentScope.Section), handlers.Attachment)); // section scope without a key
        Assert.Equal(0, await api.Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ProcessedMessage"));  // rolled back, so a retry reprocesses
    }

    [Fact]
    public async Task EngagementUpdated_refreshes_rule_inputs_and_moves_the_configuration_version()
    {
        var api = await WorkflowApi.CreateAsync(sql, Eng);
        var handlers = new Handlers(api);
        var versionBefore = await api.Db.ScalarAsync<string>("SELECT CONVERT(VARCHAR(20), RowVer, 1) FROM dbo.EngagementConfigurations WHERE EngagementId = @id", new { id = Eng });

        await handlers.RunAsync("workflow", Updated(Eng, period: "Quarterly", region: "APAC"), handlers.Updated);

        Assert.Equal("Quarterly", await api.Db.ScalarAsync<string>("SELECT PeriodType FROM dbo.EngagementConfigurations WHERE EngagementId = @id", new { id = Eng }));
        Assert.Equal("APAC", await api.Db.ScalarAsync<string>("SELECT Region FROM dbo.EngagementConfigurations WHERE EngagementId = @id", new { id = Eng }));
        Assert.NotEqual(versionBefore, await api.Db.ScalarAsync<string>("SELECT CONVERT(VARCHAR(20), RowVer, 1) FROM dbo.EngagementConfigurations WHERE EngagementId = @id", new { id = Eng }));
    }

    [Fact]
    public async Task EngagementUpdated_with_unchanged_inputs_does_nothing()
    {
        var api = await WorkflowApi.CreateAsync(sql, Eng);
        var handlers = new Handlers(api);

        await handlers.RunAsync("workflow", Updated(Eng, period: "Annual", region: "EMEA"), handlers.Updated);

        Assert.Equal(0, await api.EventCountAsync("ConfigurationChanged"));
    }

    [Fact]
    public async Task EngagementUpdated_before_provisioning_fails_so_the_broker_redelivers()
    {
        var api = new WorkflowApi { Harness = await WorkflowHarness.CreateAsync(sql) };
        var handlers = new Handlers(api);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handlers.RunAsync("workflow", Updated("ENG-NOT-YET", region: "APAC"), handlers.Updated));
        Assert.Equal(0, await api.Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ProcessedMessage"));
    }

    [Fact]
    public async Task Status_changes_evict_the_cached_access_answer()
    {
        var cache = new EngagementAccessCache(TimeProvider.System);
        cache.Set(Eng, Alice.UserId, new EngagementAccessDto(Eng, EngagementRole.Auditor, EngagementStatus.InProgress, "v", [Capabilities.Edit]));
        var envelope = EventEnvelope<EngagementStatusChangedPayload>.Create(EventTypes.EngagementStatusChanged, Eng,
            new EngagementStatusChangedPayload(EngagementStatus.InProgress, EngagementStatus.UnderReview, "Submit", "v2"));

        await new AccessCacheEvictionHandler(cache).HandleAsync(envelope, new Mock<IUnitOfWork>().Object, default);

        Assert.False(cache.TryGet(Eng, Alice.UserId, out _));
    }
}
