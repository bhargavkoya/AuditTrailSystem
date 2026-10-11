using System.Collections.Concurrent;
using AuditFlow.BuildingBlocks.Data;
using AuditFlow.Workflow.Api.Domain;
using Dapper;

namespace AuditFlow.Workflow.Api.Data;

public interface IRuleSetProvider
{
    /// <summary>The rule set new engagements are provisioned with.</summary>
    Task<RuleSet> GetActiveAsync(CancellationToken ct);

    /// <summary>The rule set an engagement was provisioned with. Versions are immutable, so they are cached.</summary>
    Task<RuleSet> GetAsync(int version, CancellationToken ct);
}

public sealed class RuleSetProvider(IDbConnectionFactory connections) : IRuleSetProvider
{
    private readonly ConcurrentDictionary<int, RuleSet> cache = new();

    public async Task<RuleSet> GetActiveAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var version = await connection.QuerySingleOrDefaultAsync<int?>(
            "SELECT TOP 1 Version FROM dbo.RuleSets WHERE IsActive = 1 ORDER BY Version DESC", null, ct)
            ?? throw new InvalidOperationException("No active rule set.");
        return await GetAsync(version, ct);
    }

    public async Task<RuleSet> GetAsync(int version, CancellationToken ct)
    {
        if (cache.TryGetValue(version, out var cached)) return cached;

        await using var connection = await connections.OpenAsync(ct);
        var json = await connection.QuerySingleOrDefaultAsync<string>("SELECT Json FROM dbo.RuleSets WHERE Version = @version", new { version }, ct)
                   ?? throw new InvalidOperationException($"Rule set version {version} not found.");
        return cache[version] = RuleSetJson.Parse(json);
    }
}

/// <summary>
/// Publishes the embedded rule set (the source of truth for its version) into RuleSets at startup. There is no editing UI
/// (non-goal); a new version is a new embedded file. Fails fast when the catalogue is inconsistent.
/// </summary>
public sealed class RuleSetSeeder(IDbConnectionFactory connections, TimeProvider clock, ILogger<RuleSetSeeder> logger) : IDataSeeder
{
    public async Task SeedAsync(CancellationToken ct)
    {
        var json = RuleSetJson.LoadEmbeddedJson();
        var ruleSet = RuleSetJson.Parse(json);
        var errors = RuleSetValidator.Validate(ruleSet);
        if (errors.Count > 0)
            throw new InvalidOperationException("Rule set is inconsistent: " + string.Join("; ", errors));

        await using var connection = await connections.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            MERGE dbo.RuleSets AS target
            USING (SELECT @Version AS Version) AS source ON target.Version = source.Version
            WHEN MATCHED THEN UPDATE SET Json = @Json
            WHEN NOT MATCHED THEN INSERT (Version, Json, IsActive, CreatedAt) VALUES (@Version, @Json, 1, @Now);
            """, new { ruleSet.Version, Json = json, Now = clock.GetUtcNow() }, cancellationToken: ct));

        logger.LogInformation("Rule set v{Version} published ({Sections} sections)", ruleSet.Version, ruleSet.Sections.Count);
    }
}
