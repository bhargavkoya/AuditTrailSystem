using System.Text.Json;
using AuditFlow.BuildingBlocks.Access;
using AuditFlow.BuildingBlocks.Errors;
using AuditFlow.BuildingBlocks.Messaging;
using AuditFlow.BuildingBlocks.Security;
using AuditFlow.Contracts.Api;
using AuditFlow.Workflow.Api.Data;
using AuditFlow.Workflow.Api.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using static AuditFlow.Contracts.Api.DemoData;

namespace AuditFlow.IntegrationTests;

internal sealed class WorkflowApi
{
    public static readonly string[] AuditorCaps =
        [Capabilities.View, Capabilities.Edit, Capabilities.Attach, Capabilities.AttachView, Capabilities.CommentView, Capabilities.Submit];
    public static readonly string[] ViewerCaps = [Capabilities.View];
    public static readonly string[] ReviewerCaps = [Capabilities.View, Capabilities.AttachView, Capabilities.CommentView, Capabilities.Comment];

    public required WorkflowHarness Harness { get; init; }

    public TestDatabase Db => Harness.Db;

    public WorkflowService ServiceFor(DemoUser user, string[] capabilities, EngagementStatus status = EngagementStatus.InProgress)
    {
        var access = new Mock<IEngagementAccessClient>();
        access.Setup(a => a.GetAccessAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => new EngagementAccessDto(id, user.HomeRole, status, "AAAA", capabilities));
        var currentUser = new Mock<ICurrentUser>();
        currentUser.SetupGet(u => u.UserId).Returns(user.UserId);
        currentUser.SetupGet(u => u.Name).Returns(user.DisplayName);

        var evaluator = new RuleEvaluator();
        var engine = new ValidationEngine(evaluator, new ReconciliationCalculator());
        var repository = Harness.Repository;
        var recomputer = new Recomputer(repository, engine, new SqlOutbox(), TimeProvider.System);
        return new WorkflowService(
            repository, new RuleSetProvider(Db.Connections), engine, access.Object, Db.Connections, Db.UnitOfWork,
            new SqlOutbox(), recomputer, currentUser.Object, TimeProvider.System);
    }

    public WorkflowService Alice_() => ServiceFor(Alice, AuditorCaps);

    public static async Task<WorkflowApi> CreateAsync(SqlServerFixture sql, string engagementId = "ENG-7001",
        string[]? configTypes = null, bool reconciliation = false, bool legalReview = false)
    {
        var harness = await WorkflowHarness.CreateAsync(sql);
        await harness.ProvisionAsync(WorkflowHarness.Created(engagementId, configTypes: configTypes ?? ["Equities"],
            reconciliation: reconciliation, legalReview: legalReview));
        return new WorkflowApi { Harness = harness };
    }

    public static IReadOnlyDictionary<string, JsonElement> Fields(params (string Key, object? Value)[] values)
        => values.ToDictionary(v => v.Key, v => JsonSerializer.SerializeToElement(v.Value));

    public async Task<string> VersionOfAsync(string engagementId, string sectionKey)
    {
        var tab = await Alice_().GetTabAsync(engagementId, int.Parse(sectionKey.Split('-')[1]), default);
        return tab.Sections.Single(s => s.SectionKey == sectionKey).Version;
    }

    public Task<int> EventCountAsync(string eventType)
        => Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.OutboxMessage WHERE EventType = @eventType", new { eventType });
}

[Collection(SqlCollection.Name)]
[Trait("Category", "Integration")]
public class WorkflowServiceTests(SqlServerFixture sql)
{
    private const string Eng = "ENG-7001";

    // ---- Reads and role-shaped views -------------------------------------------------------------------------

    [Fact]
    public async Task Tabs_report_not_ready_until_the_engagement_is_provisioned()
    {
        var api = new WorkflowApi { Harness = await WorkflowHarness.CreateAsync(sql) };

        var tabs = await api.Alice_().GetTabsAsync("ENG-NOPE", default);

        Assert.False(tabs.Ready);
        Assert.Empty(tabs.Tabs);
    }

    [Fact]
    public async Task Tabs_list_five_tabs_with_issue_counts()
    {
        var api = await WorkflowApi.CreateAsync(sql);

        var tabs = await api.Alice_().GetTabsAsync(Eng, default);

        Assert.True(tabs.Ready);
        Assert.Equal([1, 2, 3, 4, 5], tabs.Tabs.Select(t => t.TabNo));
        Assert.True(tabs.Tabs[0].IssueCounts.Errors > 0); // period dates are required
    }

    [Fact]
    public async Task Tab_view_contains_only_visible_sections_and_backend_decided_flags()
    {
        var api = await WorkflowApi.CreateAsync(sql);

        var tab2 = await api.Alice_().GetTabAsync(Eng, 2, default);

        Assert.Equal(["section-2-1", "section-2-2"], tab2.Sections.Select(s => s.SectionKey)); // Equities only
        Assert.True(tab2.Editable);
        var positions = tab2.Sections[1];
        Assert.True(positions.Fields.Single(f => f.Key == "bookValue").Required);
        Assert.False(positions.Fields.Single(f => f.Key == "ledgerValue").Required); // reconciliation off
        Assert.All(positions.Fields, f => Assert.True(f.Editable));
    }

    [Fact]
    public async Task Viewer_gets_read_only_content_and_no_attachment_information()
    {
        var api = await WorkflowApi.CreateAsync(sql);

        var tab = await api.ServiceFor(Victor, WorkflowApi.ViewerCaps).GetTabAsync(Eng, 2, default);

        Assert.False(tab.Editable);
        Assert.All(tab.Sections, s =>
        {
            Assert.False(s.Editable);
            Assert.Null(s.Attachment);
            Assert.All(s.Fields, f => Assert.False(f.Editable));
        });
    }

    [Fact]
    public async Task Auditor_and_reviewer_see_attachment_info_but_only_the_auditor_can_upload()
    {
        var api = await WorkflowApi.CreateAsync(sql);

        var auditor = (await api.Alice_().GetTabAsync(Eng, 2, default)).Sections[0].Attachment!;
        var reviewer = (await api.ServiceFor(Rachel, WorkflowApi.ReviewerCaps).GetTabAsync(Eng, 2, default)).Sections[0].Attachment!;

        Assert.True(auditor.Required);
        Assert.True(auditor.CanUpload);
        Assert.False(auditor.HasCurrent);
        Assert.True(reviewer.Required);
        Assert.False(reviewer.CanUpload);
    }

    [Fact]
    public async Task Tab_five_shows_computed_reconciliation_only_there()
    {
        var api = await WorkflowApi.CreateAsync(sql, reconciliation: true);

        Assert.NotNull((await api.Alice_().GetTabAsync(Eng, 5, default)).Reconciliation);
        Assert.Null((await api.Alice_().GetTabAsync(Eng, 2, default)).Reconciliation);
    }

    [Fact]
    public async Task Unknown_tab_is_not_found()
    {
        var api = await WorkflowApi.CreateAsync(sql);

        await Assert.ThrowsAsync<NotFoundException>(() => api.Alice_().GetTabAsync(Eng, 9, default));
    }

    // ---- Saving: versions, no-ops, events --------------------------------------------------------------------

    [Fact]
    public async Task Saving_a_section_bumps_its_version_and_returns_the_new_state_and_summary()
    {
        var api = await WorkflowApi.CreateAsync(sql);
        var before = await api.VersionOfAsync(Eng, "section-2-2");

        var result = await api.Alice_().SaveSectionAsync(Eng, "section-2-2", before, WorkflowApi.Fields(("bookValue", 1_000_000)), default);

        Assert.NotEqual(before, result.Section.Version);
        Assert.Equal(1_000_000m, result.Section.Fields.Single(f => f.Key == "bookValue").Value);
        Assert.DoesNotContain(result.Section.Fields.Single(f => f.Key == "bookValue").Issues, i => i.Code == "FIELD_REQUIRED");
        Assert.Equal(result.Section.Version, await api.VersionOfAsync(Eng, "section-2-2"));
        Assert.Empty(result.VisibilityChanges);
    }

    [Fact]
    public async Task A_save_emits_SectionUpdated_with_field_names_and_version_but_never_values()
    {
        var api = await WorkflowApi.CreateAsync(sql);
        var version = await api.VersionOfAsync(Eng, "section-2-1");

        var result = await api.Alice_().SaveSectionAsync(Eng, "section-2-1", version,
            WorkflowApi.Fields(("sourceSystem", "Murex-secret-value"), ("recordCount", 42)), default);

        var envelope = await api.Db.ScalarAsync<string>("SELECT Envelope FROM dbo.OutboxMessage WHERE EventType = 'SectionUpdated'");
        using var json = JsonDocument.Parse(envelope);
        var payload = json.RootElement.GetProperty("payload");
        Assert.Equal("section-2-1", payload.GetProperty("sectionKey").GetString());
        Assert.Equal(2, payload.GetProperty("tabNo").GetInt32());
        Assert.Equal(["sourceSystem", "recordCount"], payload.GetProperty("changedFields").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(result.Section.Version, payload.GetProperty("version").GetString());
        Assert.DoesNotContain("Murex-secret-value", envelope);
        Assert.Equal(Alice.UserId.ToString(), json.RootElement.GetProperty("actor").GetProperty("userId").GetString());
    }

    [Fact]
    public async Task Saving_the_same_values_is_a_no_op_without_a_version_bump_or_events()
    {
        var api = await WorkflowApi.CreateAsync(sql);
        var v1 = await api.VersionOfAsync(Eng, "section-2-1");
        var saved = await api.Alice_().SaveSectionAsync(Eng, "section-2-1", v1, WorkflowApi.Fields(("sourceSystem", "GL")), default);
        var events = await api.EventCountAsync("SectionUpdated");

        var again = await api.Alice_().SaveSectionAsync(Eng, "section-2-1", saved.Section.Version, WorkflowApi.Fields(("sourceSystem", "GL")), default);

        Assert.Equal(saved.Section.Version, again.Section.Version);
        Assert.Equal(events, await api.EventCountAsync("SectionUpdated"));
    }

    [Fact]
    public async Task Validation_summary_changed_is_written_only_when_the_summary_changed()
    {
        var api = await WorkflowApi.CreateAsync(sql);
        var version = await api.VersionOfAsync(Eng, "section-2-1");

        // Changing a value that does not alter any message leaves the summary (and its hash) untouched ...
        await api.Alice_().SaveSectionAsync(Eng, "section-2-1", version, WorkflowApi.Fields(("sourceSystem", "GL")), default);
        // (sourceSystem was required; filling it removes an error, so the summary did change once)
        var afterFirst = await api.EventCountAsync("ValidationSummaryChanged");
        var next = await api.VersionOfAsync(Eng, "section-2-1");
        await api.Alice_().SaveSectionAsync(Eng, "section-2-1", next, WorkflowApi.Fields(("sourceSystem", "Another GL")), default);

        Assert.Equal(1, afterFirst);
        Assert.Equal(afterFirst, await api.EventCountAsync("ValidationSummaryChanged"));
    }

    // ---- Concurrency -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_stale_version_returns_a_conflict_carrying_the_current_version()
    {
        var api = await WorkflowApi.CreateAsync(sql);
        var stale = await api.VersionOfAsync(Eng, "section-2-1");
        var winner = await api.Alice_().SaveSectionAsync(Eng, "section-2-1", stale, WorkflowApi.Fields(("sourceSystem", "A")), default);

        var conflict = await Assert.ThrowsAsync<ConcurrencyException>(() =>
            api.ServiceFor(Bob, WorkflowApi.AuditorCaps).SaveSectionAsync(Eng, "section-2-1", stale, WorkflowApi.Fields(("sourceSystem", "B")), default));

        Assert.Equal(winner.Section.Version, conflict.CurrentVersion);
        Assert.Equal("section-2-1", conflict.Resource);
        var tab = await api.Alice_().GetTabAsync(Eng, 2, default);
        Assert.Equal("A", tab.Sections[0].Fields.Single(f => f.Key == "sourceSystem").Value); // the loser changed nothing
    }

    [Fact]
    public async Task Two_parallel_saves_with_the_same_version_let_exactly_one_win()
    {
        var api = await WorkflowApi.CreateAsync(sql);
        var version = await api.VersionOfAsync(Eng, "section-2-1");

        async Task<Exception?> Save(DemoUser user, string value)
        {
            try
            {
                await api.ServiceFor(user, WorkflowApi.AuditorCaps).SaveSectionAsync(Eng, "section-2-1", version, WorkflowApi.Fields(("sourceSystem", value)), default);
                return null;
            }
            catch (Exception ex) { return ex; }
        }

        var results = await Task.WhenAll(Save(Alice, "from Alice"), Save(Bob, "from Bob"));

        Assert.Single(results, r => r is null);
        Assert.Single(results, r => r is ConcurrencyException);
        Assert.Equal(1, await api.EventCountAsync("SectionUpdated"));
    }

    [Fact]
    public async Task Many_parallel_saves_never_lose_an_update_silently()
    {
        var api = await WorkflowApi.CreateAsync(sql);
        var version = await api.VersionOfAsync(Eng, "section-2-1");

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            try
            {
                await api.Alice_().SaveSectionAsync(Eng, "section-2-1", version, WorkflowApi.Fields(("recordCount", 100 + i)), default);
                return true;
            }
            catch (ConcurrencyException) { return false; }
        }));

        Assert.Equal(1, outcomes.Count(o => o));
    }

    [Fact]
    public async Task Tab_save_is_atomic_when_one_section_is_stale()
    {
        var api = await WorkflowApi.CreateAsync(sql, configTypes: ["Equities"]);
        var v21 = await api.VersionOfAsync(Eng, "section-2-1");
        var v22 = await api.VersionOfAsync(Eng, "section-2-2");
        await api.Alice_().SaveSectionAsync(Eng, "section-2-2", v22, WorkflowApi.Fields(("bookValue", 5)), default); // makes v22 stale

        var conflict = await Assert.ThrowsAsync<ConcurrencyException>(() => api.Alice_().SaveTabAsync(Eng, 2,
        [
            new SectionPatch("section-2-1", v21, WorkflowApi.Fields(("sourceSystem", "should roll back"))),
            new SectionPatch("section-2-2", v22, WorkflowApi.Fields(("bookValue", 99)))
        ], default));

        Assert.Equal("section-2-2", conflict.Resource);
        var tab = await api.Alice_().GetTabAsync(Eng, 2, default);
        Assert.Null(tab.Sections[0].Fields.Single(f => f.Key == "sourceSystem").Value);
        Assert.Equal(5m, tab.Sections[1].Fields.Single(f => f.Key == "bookValue").Value);
        Assert.Equal(v21, tab.Sections[0].Version);
    }

    [Fact]
    public async Task Tab_save_applies_every_section_and_emits_one_event_each()
    {
        var api = await WorkflowApi.CreateAsync(sql);
        var v21 = await api.VersionOfAsync(Eng, "section-2-1");
        var v22 = await api.VersionOfAsync(Eng, "section-2-2");

        var result = await api.Alice_().SaveTabAsync(Eng, 2,
        [
            new SectionPatch("section-2-1", v21, WorkflowApi.Fields(("sourceSystem", "GL"))),
            new SectionPatch("section-2-2", v22, WorkflowApi.Fields(("bookValue", 250)))
        ], default);

        Assert.Equal("GL", result.Tab.Sections[0].Fields.Single(f => f.Key == "sourceSystem").Value);
        Assert.Equal(250m, result.Tab.Sections[1].Fields.Single(f => f.Key == "bookValue").Value);
        Assert.Equal(2, await api.EventCountAsync("SectionUpdated"));
    }

    // ---- Tab 1 drives everything else -------------------------------------------------------------------------

    [Fact]
    public async Task Changing_a_Tab_1_option_flips_downstream_sections_in_the_same_response()
    {
        var api = await WorkflowApi.CreateAsync(sql);
        var version = await api.VersionOfAsync(Eng, "section-1-1");
        Assert.DoesNotContain((await api.Alice_().GetTabAsync(Eng, 3, default)).Sections, s => s.SectionKey == "section-3-2");

        var result = await api.Alice_().SaveSectionAsync(Eng, "section-1-1", version, WorkflowApi.Fields(("cfgDerivatives", true)), default);

        Assert.Equivalent(new[] { new VisibilityChangeDto("section-2-4", true), new VisibilityChangeDto("section-3-2", true) },
            result.VisibilityChanges, strict: false);
        Assert.Contains((await api.Alice_().GetTabAsync(Eng, 3, default)).Sections, s => s.SectionKey == "section-3-2");
        Assert.Contains(result.Summary.Issues, i => i.SectionKey == "section-3-2" && i.Code == "FIELD_REQUIRED");
    }

    [Fact]
    public async Task A_Tab_1_change_emits_ConfigurationChanged_listing_names_and_affected_sections()
    {
        var api = await WorkflowApi.CreateAsync(sql);
        var version = await api.VersionOfAsync(Eng, "section-1-1");

        await api.Alice_().SaveSectionAsync(Eng, "section-1-1", version, WorkflowApi.Fields(("cfgDerivatives", true)), default);

        Assert.Equal(1, await api.EventCountAsync("ConfigurationChanged"));
        using var json = JsonDocument.Parse(await api.Db.ScalarAsync<string>("SELECT Envelope FROM dbo.OutboxMessage WHERE EventType = 'ConfigurationChanged'"));
        var payload = json.RootElement.GetProperty("payload");
        Assert.Equal(["cfgDerivatives"], payload.GetProperty("changedKeys").EnumerateArray().Select(e => e.GetString()));
        var affected = payload.GetProperty("affectedSections").EnumerateArray().ToDictionary(
            e => e.GetProperty("sectionKey").GetString()!, e => e.GetProperty("visible").GetBoolean());
        Assert.Equal(new Dictionary<string, bool> { ["section-2-4"] = true, ["section-3-2"] = true }, affected);
    }

    [Fact]
    public async Task Hidden_section_values_come_back_when_the_section_is_enabled_again()
    {
        var api = await WorkflowApi.CreateAsync(sql, configTypes: ["Equities", "Derivatives"]);
        var v24 = await api.VersionOfAsync(Eng, "section-2-4");
        await api.Alice_().SaveSectionAsync(Eng, "section-2-4", v24, WorkflowApi.Fields(("bookValue", 777)), default);

        var v11 = await api.VersionOfAsync(Eng, "section-1-1");
        var hidden = await api.Alice_().SaveSectionAsync(Eng, "section-1-1", v11, WorkflowApi.Fields(("cfgDerivatives", false)), default);
        Assert.DoesNotContain((await api.Alice_().GetTabAsync(Eng, 2, default)).Sections, s => s.SectionKey == "section-2-4");
        Assert.DoesNotContain(hidden.Summary.Issues, i => i.SectionKey == "section-2-4");

        var shown = await api.Alice_().SaveSectionAsync(Eng, "section-1-1", hidden.Section.Version, WorkflowApi.Fields(("cfgDerivatives", true)), default);

        var restored = (await api.Alice_().GetTabAsync(Eng, 2, default)).Sections.Single(s => s.SectionKey == "section-2-4");
        Assert.Equal(777m, restored.Fields.Single(f => f.Key == "bookValue").Value);
        Assert.Contains(shown.VisibilityChanges, v => v is { SectionKey: "section-2-4", Visible: true });
    }

    [Fact]
    public async Task Saving_to_a_hidden_section_is_rejected()
    {
        var api = await WorkflowApi.CreateAsync(sql);
        var version = await api.VersionOfAsync(Eng, "section-1-1");

        // section-2-4 (Derivatives) is hidden; its version is readable from the database only.
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => api.Alice_().SaveSectionAsync(
            Eng, "section-2-4", version, WorkflowApi.Fields(("bookValue", 1)), default));

        Assert.Contains("section-2-4", ex.Errors.Keys);
    }

    // ---- Reconciliation through saves --------------------------------------------------------------------------

    [Fact]
    public async Task A_significant_variance_blocks_readiness_until_commented()
    {
        var api = await WorkflowApi.CreateAsync(sql, reconciliation: true);
        var v22 = await api.VersionOfAsync(Eng, "section-2-2");

        var saved = await api.Alice_().SaveSectionAsync(Eng, "section-2-2", v22,
            WorkflowApi.Fields(("bookValue", 1_000_000), ("ledgerValue", 1_080_000)), default);

        var row = Assert.Single(saved.Summary.Reconciliation);
        Assert.True(row.IsSignificant);
        Assert.Equal(8m, row.PercentVariance);
        Assert.Contains(saved.Summary.Issues, i => i.Code == "RECON_VARIANCE_SIGNIFICANT");
        Assert.Contains(saved.Summary.Issues, i => i.Code == "SIGNIFICANT_VARIANCE_COMMENT");
        Assert.False(saved.Summary.ReadyToSubmit);
    }

    // ---- Validation of the request and permissions --------------------------------------------------------------

    [Fact]
    public async Task Unknown_fields_and_wrong_types_are_rejected_with_field_level_errors()
    {
        var api = await WorkflowApi.CreateAsync(sql);
        var version = await api.VersionOfAsync(Eng, "section-2-2");

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => api.Alice_().SaveSectionAsync(
            Eng, "section-2-2", version, WorkflowApi.Fields(("bookValue", "not a number"), ("nope", 1)), default));

        Assert.Contains("section-2-2.bookValue", ex.Errors.Keys);
        Assert.Contains("section-2-2.nope", ex.Errors.Keys);
    }

    [Fact]
    public async Task A_missing_or_malformed_version_is_a_validation_error()
    {
        var api = await WorkflowApi.CreateAsync(sql);

        await Assert.ThrowsAsync<ValidationFailedException>(() => api.Alice_().SaveSectionAsync(Eng, "section-2-1", "", WorkflowApi.Fields(("sourceSystem", "x")), default));
        await Assert.ThrowsAsync<ValidationFailedException>(() => api.Alice_().SaveSectionAsync(Eng, "section-2-1", "%%%", WorkflowApi.Fields(("sourceSystem", "x")), default));
    }

    [Fact]
    public async Task Tab_save_rejects_sections_from_another_tab()
    {
        var api = await WorkflowApi.CreateAsync(sql);
        var version = await api.VersionOfAsync(Eng, "section-2-1");

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => api.Alice_().SaveTabAsync(Eng, 3,
            [new SectionPatch("section-2-1", version, WorkflowApi.Fields(("sourceSystem", "x")))], default));

        Assert.Contains("section-2-1", ex.Errors.Keys);
    }

    [Theory]
    [InlineData("Viewer")]
    [InlineData("Reviewer")]
    public async Task Roles_without_the_edit_capability_cannot_save(string role)
    {
        var api = await WorkflowApi.CreateAsync(sql);
        var version = await api.VersionOfAsync(Eng, "section-2-1");
        var service = role == "Viewer"
            ? api.ServiceFor(Victor, WorkflowApi.ViewerCaps)
            : api.ServiceFor(Rachel, WorkflowApi.ReviewerCaps);

        await Assert.ThrowsAsync<ForbiddenException>(() => service.SaveSectionAsync(Eng, "section-2-1", version, WorkflowApi.Fields(("sourceSystem", "x")), default));
    }

    [Fact]
    public async Task A_locked_engagement_has_no_edit_capability_so_saves_are_forbidden()
    {
        var api = await WorkflowApi.CreateAsync(sql);
        var version = await api.VersionOfAsync(Eng, "section-2-1");
        // Under Review: engagement-service withholds content.edit from everyone.
        var locked = api.ServiceFor(Alice, [Capabilities.View, Capabilities.AttachView, Capabilities.CommentView], EngagementStatus.UnderReview);

        await Assert.ThrowsAsync<ForbiddenException>(() => locked.SaveSectionAsync(Eng, "section-2-1", version, WorkflowApi.Fields(("sourceSystem", "x")), default));
        Assert.False((await locked.GetTabAsync(Eng, 2, default)).Editable);
    }

    // ---- Internal endpoints' logic ---------------------------------------------------------------------------

    [Fact]
    public async Task Submission_readiness_lists_blocking_errors()
    {
        var api = await WorkflowApi.CreateAsync(sql);

        var readiness = await api.Alice_().GetSubmissionReadinessAsync(Eng, default);

        Assert.False(readiness.ReadyToSubmit);
        Assert.NotEmpty(readiness.Errors);
        Assert.All(readiness.Errors, e => Assert.Equal(IssueSeverity.Error, e.Severity));
        Assert.False((await api.Alice_().GetSubmissionReadinessAsync("ENG-NOPE", default)).ReadyToSubmit);
    }

    [Fact]
    public async Task Section_policy_tells_attachment_service_what_may_be_attached()
    {
        var api = await WorkflowApi.CreateAsync(sql);
        var service = api.Alice_();

        var allowed = await service.GetSectionPolicyAsync(Eng, "section-2-1", default);
        var noAttachments = await service.GetSectionPolicyAsync(Eng, "section-1-1", default);
        var hidden = await service.GetSectionPolicyAsync(Eng, "section-2-4", default);
        var unknown = await service.GetSectionPolicyAsync(Eng, "section-9-9", default);

        Assert.Equal(new SectionPolicyDto(true, true, 2, true), allowed);
        Assert.Equal(new SectionPolicyDto(true, true, 1, false), noAttachments);
        Assert.False(hidden.Visible);
        Assert.False(unknown.Exists);
    }
}
