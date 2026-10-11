using System.Data.Common;
using AuditFlow.BuildingBlocks.Data;
using AuditFlow.Workflow.Api.Domain;
using Dapper;

namespace AuditFlow.Workflow.Api.Data;

public sealed record SectionState(
    string SectionKey, int TabNo, bool IsVisible, DateTimeOffset UpdatedAt, Guid? UpdatedByUserId, string? UpdatedByName, byte[] RowVer);

public sealed record FieldRow(string SectionKey, string FieldKey, string FieldType, string? ValueText, bool? ValueBool, DateOnly? ValueDate)
{
    public object? Value => FieldValues.FromStored(FieldType, new StoredValue(ValueText, ValueBool, ValueDate));
}

/// <summary>Everything stored for one provisioned engagement, read in one pass.</summary>
public sealed record WorkflowSnapshot(
    string EngagementId,
    string PeriodType,
    string Region,
    int RuleSetVersion,
    byte[] ConfigVersion,
    IReadOnlyDictionary<string, SectionState> Sections,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> Values,
    IReadOnlySet<string> Attachments,
    string? PublishedSummaryHash)
{
    public EvaluationContext ToContext() => new(PeriodType, Region, Values, Attachments);
}

public sealed record SectionInit(string SectionKey, int TabNo, bool IsVisible);

public sealed record FieldInit(string SectionKey, string FieldKey, string FieldType, StoredValue Value);

public sealed record ProvisionedEngagement(
    string EngagementId, string PeriodType, string Region, int RuleSetVersion, DateTimeOffset At,
    IReadOnlyList<SectionInit> Sections, IReadOnlyList<FieldInit> Fields, ValidationSummaryResult Summary);

public sealed record FieldWrite(string SectionKey, string FieldKey, string FieldType, StoredValue Value);

public interface IWorkflowRepository
{
    /// <summary>Takes the per-engagement write lock (held to the end of the transaction). False when not provisioned.</summary>
    Task<bool> LockAsync(IUnitOfWork uow, string engagementId, CancellationToken ct);

    Task<WorkflowSnapshot?> LoadAsync(DbConnection connection, DbTransaction? transaction, string engagementId, CancellationToken ct);

    Task InsertProvisionedAsync(IUnitOfWork uow, ProvisionedEngagement engagement, CancellationToken ct);

    /// <summary>Attachment scopes already announced for an engagement (AttachmentAdded may beat EngagementCreated).</summary>
    Task<IReadOnlySet<string>> GetAttachmentPresenceAsync(IUnitOfWork uow, string engagementId, CancellationToken ct);

    /// <summary>
    /// Writes the fields and moves the section's version, but only if its version still equals <paramref name="expectedVersion"/>.
    /// Returns false when it does not (stale => 409).
    /// </summary>
    Task<bool> WriteSectionAsync(
        IUnitOfWork uow, string engagementId, string sectionKey, byte[] expectedVersion,
        IReadOnlyList<FieldWrite> writes, Guid actorId, string actorName, DateTimeOffset now, CancellationToken ct);

    Task SetVisibilityAsync(IUnitOfWork uow, string engagementId, string sectionKey, bool visible, DateTimeOffset now, CancellationToken ct);

    /// <summary>Moves the configuration version (Tab 1 changed, or period / region changed).</summary>
    Task TouchConfigurationAsync(IUnitOfWork uow, string engagementId, CancellationToken ct);

    Task UpdateRuleInputsAsync(IUnitOfWork uow, string engagementId, string periodType, string region, CancellationToken ct);

    Task AddAttachmentPresenceAsync(IUnitOfWork uow, string engagementId, string scopeKey, DateTimeOffset now, CancellationToken ct);

    Task UpsertSummaryAsync(IUnitOfWork uow, string engagementId, ValidationSummaryResult summary, DateTimeOffset now, CancellationToken ct);
}

public sealed class WorkflowRepository : IWorkflowRepository
{
    public async Task<bool> LockAsync(IUnitOfWork uow, string engagementId, CancellationToken ct)
        => await uow.QuerySingleOrDefaultAsync<byte[]>(
               "SELECT RowVer FROM dbo.EngagementConfigurations WITH (UPDLOCK, HOLDLOCK) WHERE EngagementId = @engagementId",
               new { engagementId }, ct) is not null;

    public async Task<WorkflowSnapshot?> LoadAsync(DbConnection connection, DbTransaction? transaction, string engagementId, CancellationToken ct)
    {
        CommandDefinition Command(string sql) => new(sql, new { engagementId }, transaction, cancellationToken: ct);

        var config = await connection.QuerySingleOrDefaultAsync<ConfigRow>(Command("""
            SELECT PeriodType, Region, RuleSetVersion, RowVer FROM dbo.EngagementConfigurations WHERE EngagementId = @engagementId
            """));
        if (config is null) return null;

        var sections = (await connection.QueryAsync<SectionDb>(Command("""
            SELECT SectionKey, TabNo, IsVisible, UpdatedAt, UpdatedByUserId, UpdatedByName, RowVer
            FROM dbo.WorkflowSections WHERE EngagementId = @engagementId
            """))).ToDictionary(s => s.SectionKey, s => new SectionState(
                s.SectionKey, s.TabNo, s.IsVisible, s.UpdatedAt, s.UpdatedByUserId, s.UpdatedByName, s.RowVer));

        var fields = (await connection.QueryAsync<FieldDb>(Command("""
            SELECT SectionKey, FieldKey, FieldType, ValueText, ValueBool, ValueDate
            FROM dbo.SectionFields WHERE EngagementId = @engagementId
            """))).Select(f => new FieldRow(f.SectionKey, f.FieldKey, f.FieldType, f.ValueText, f.ValueBool, f.ValueDate));
        var values = fields.GroupBy(f => f.SectionKey).ToDictionary(
            g => g.Key, g => (IReadOnlyDictionary<string, object?>)g.ToDictionary(f => f.FieldKey, f => f.Value));

        var attachments = (await connection.QueryAsync<string>(Command(
            "SELECT ScopeKey FROM dbo.AttachmentPresence WHERE EngagementId = @engagementId"))).ToHashSet();

        var hash = await connection.QuerySingleOrDefaultAsync<string>(Command(
            "SELECT Hash FROM dbo.ValidationSummaries WHERE EngagementId = @engagementId"));

        return new WorkflowSnapshot(engagementId, config.PeriodType, config.Region, config.RuleSetVersion, config.RowVer,
            sections, values, attachments, hash);
    }

    public async Task<IReadOnlySet<string>> GetAttachmentPresenceAsync(IUnitOfWork uow, string engagementId, CancellationToken ct)
        => (await uow.QueryAsync<string>("SELECT ScopeKey FROM dbo.AttachmentPresence WHERE EngagementId = @engagementId", new { engagementId }, ct)).ToHashSet();

    public async Task InsertProvisionedAsync(IUnitOfWork uow, ProvisionedEngagement e, CancellationToken ct)
    {
        await uow.ExecuteAsync("""
            INSERT INTO dbo.EngagementConfigurations (EngagementId, PeriodType, Region, RuleSetVersion, ProvisionedAt)
            VALUES (@EngagementId, @PeriodType, @Region, @RuleSetVersion, @At)
            """, new { e.EngagementId, e.PeriodType, e.Region, e.RuleSetVersion, e.At }, ct);

        foreach (var s in e.Sections)
            await uow.ExecuteAsync("""
                INSERT INTO dbo.WorkflowSections (EngagementId, SectionKey, TabNo, IsVisible, UpdatedAt)
                VALUES (@EngagementId, @SectionKey, @TabNo, @IsVisible, @At)
                """, new { e.EngagementId, s.SectionKey, s.TabNo, s.IsVisible, e.At }, ct);

        foreach (var f in e.Fields)
            await uow.ExecuteAsync("""
                INSERT INTO dbo.SectionFields (EngagementId, SectionKey, FieldKey, FieldType, ValueText, ValueBool, ValueDate)
                VALUES (@EngagementId, @SectionKey, @FieldKey, @FieldType, @Text, @Bool, @Date)
                """, new { e.EngagementId, f.SectionKey, f.FieldKey, f.FieldType, f.Value.Text, f.Value.Bool, f.Value.Date }, ct);

        await UpsertSummaryAsync(uow, e.EngagementId, e.Summary, e.At, ct);
    }

    public async Task<bool> WriteSectionAsync(
        IUnitOfWork uow, string engagementId, string sectionKey, byte[] expectedVersion,
        IReadOnlyList<FieldWrite> writes, Guid actorId, string actorName, DateTimeOffset now, CancellationToken ct)
    {
        var updated = await uow.ExecuteAsync("""
            UPDATE dbo.WorkflowSections
            SET UpdatedAt = @now, UpdatedByUserId = @actorId, UpdatedByName = @actorName
            WHERE EngagementId = @engagementId AND SectionKey = @sectionKey AND RowVer = @expectedVersion
            """, new { engagementId, sectionKey, expectedVersion, now, actorId, actorName }, ct);
        if (updated == 0) return false;

        foreach (var w in writes)
            await uow.ExecuteAsync("""
                UPDATE dbo.SectionFields SET ValueText = @Text, ValueBool = @Bool, ValueDate = @Date
                WHERE EngagementId = @engagementId AND SectionKey = @SectionKey AND FieldKey = @FieldKey
                """, new { engagementId, w.SectionKey, w.FieldKey, w.Value.Text, w.Value.Bool, w.Value.Date }, ct);
        return true;
    }

    public Task SetVisibilityAsync(IUnitOfWork uow, string engagementId, string sectionKey, bool visible, DateTimeOffset now, CancellationToken ct)
        => uow.ExecuteAsync("""
            UPDATE dbo.WorkflowSections SET IsVisible = @visible, UpdatedAt = @now
            WHERE EngagementId = @engagementId AND SectionKey = @sectionKey
            """, new { engagementId, sectionKey, visible, now }, ct);

    public Task TouchConfigurationAsync(IUnitOfWork uow, string engagementId, CancellationToken ct)
        => uow.ExecuteAsync("UPDATE dbo.EngagementConfigurations SET ProvisionedAt = ProvisionedAt WHERE EngagementId = @engagementId", new { engagementId }, ct);

    public Task UpdateRuleInputsAsync(IUnitOfWork uow, string engagementId, string periodType, string region, CancellationToken ct)
        => uow.ExecuteAsync("""
            UPDATE dbo.EngagementConfigurations SET PeriodType = @periodType, Region = @region WHERE EngagementId = @engagementId
            """, new { engagementId, periodType, region }, ct);

    public Task AddAttachmentPresenceAsync(IUnitOfWork uow, string engagementId, string scopeKey, DateTimeOffset now, CancellationToken ct)
        => uow.ExecuteAsync("""
            IF NOT EXISTS (SELECT 1 FROM dbo.AttachmentPresence WHERE EngagementId = @engagementId AND ScopeKey = @scopeKey)
                INSERT INTO dbo.AttachmentPresence (EngagementId, ScopeKey, AddedAt) VALUES (@engagementId, @scopeKey, @now)
            """, new { engagementId, scopeKey, now }, ct);

    public Task UpsertSummaryAsync(IUnitOfWork uow, string engagementId, ValidationSummaryResult s, DateTimeOffset now, CancellationToken ct)
        => uow.ExecuteAsync("""
            MERGE dbo.ValidationSummaries AS t
            USING (SELECT @engagementId AS EngagementId) AS src ON t.EngagementId = src.EngagementId
            WHEN MATCHED THEN UPDATE SET Errors = @Errors, Warnings = @Warnings, Infos = @Infos, ReadyToSubmit = @Ready, Hash = @Hash, UpdatedAt = @now
            WHEN NOT MATCHED THEN INSERT (EngagementId, Errors, Warnings, Infos, ReadyToSubmit, Hash, UpdatedAt)
                 VALUES (@engagementId, @Errors, @Warnings, @Infos, @Ready, @Hash, @now);
            """, new { engagementId, s.Counts.Errors, s.Counts.Warnings, s.Counts.Infos, Ready = s.ReadyToSubmit, s.Hash, now }, ct);

    // Dapper maps these by property, which tolerates TINYINT -> int and DATE -> DateOnly (positional records need exact types).
    private sealed class ConfigRow
    {
        public string PeriodType { get; init; } = "";
        public string Region { get; init; } = "";
        public int RuleSetVersion { get; init; }
        public byte[] RowVer { get; init; } = [];
    }

    private sealed class SectionDb
    {
        public string SectionKey { get; init; } = "";
        public byte TabNo { get; init; }
        public bool IsVisible { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
        public Guid? UpdatedByUserId { get; init; }
        public string? UpdatedByName { get; init; }
        public byte[] RowVer { get; init; } = [];
    }

    private sealed class FieldDb
    {
        public string SectionKey { get; init; } = "";
        public string FieldKey { get; init; } = "";
        public string FieldType { get; init; } = "";
        public string? ValueText { get; init; }
        public bool? ValueBool { get; init; }
        public DateOnly? ValueDate { get; init; }
    }
}
