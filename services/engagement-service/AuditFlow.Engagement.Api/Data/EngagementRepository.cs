using AuditFlow.BuildingBlocks.Data;
using AuditFlow.Contracts.Api;

namespace AuditFlow.Engagement.Api.Data;

public sealed record EngagementRow(
    string EngagementId, string Name, string PeriodType, string Region,
    Guid OwnerUserId, Guid ReviewerUserId, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt, byte[] RowVer)
{
    public EngagementStatus StatusValue => Enum.Parse<EngagementStatus>(Status);
}

public sealed record ParticipantRow(Guid UserId, string Role, string DisplayName)
{
    public EngagementRole RoleValue => Enum.Parse<EngagementRole>(Role);
}

public sealed record NewEngagement(
    string EngagementId, string Name, string PeriodType, string Region,
    Guid OwnerUserId, Guid ReviewerUserId, EngagementStatus Status, int SubmissionCount,
    DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt,
    IReadOnlyList<ParticipantRow> Participants);

public enum ListScope { Participating, Owned, Assigned, Reviewing }

public sealed record ListQuery(Guid UserId, ListScope Scope, EngagementStatus? Status, string? Search, int Page, int PageSize);

public sealed record ListRow(
    string EngagementId, string Name, string Region, string PeriodType,
    Guid ReviewerUserId, string ReviewerName, string Status, DateTimeOffset LastActivityAt, string MyRole);

public sealed record ListResult(IReadOnlyList<ListRow> Items, IReadOnlyDictionary<string, int> Counts, int Total);

/// <summary>An engagement together with the caller's role on it (null when the caller is not a participant).</summary>
public sealed record EngagementWithRole(EngagementRow Engagement, EngagementRole? MyRole);

public interface IEngagementRepository
{
    Task<string> NextIdAsync(IUnitOfWork uow, CancellationToken ct);
    Task InsertAsync(IUnitOfWork uow, NewEngagement engagement, CancellationToken ct);
    Task<bool> AnyAsync(CancellationToken ct);
    Task<EngagementWithRole?> GetAsync(string engagementId, Guid userId, CancellationToken ct);
    Task<IReadOnlyList<ParticipantRow>> GetParticipantsAsync(string engagementId, CancellationToken ct);
    Task<ListResult> ListAsync(ListQuery query, CancellationToken ct);
}

public sealed class EngagementRepository(IDbConnectionFactory connections) : IEngagementRepository
{
    public async Task<string> NextIdAsync(IUnitOfWork uow, CancellationToken ct)
        => $"ENG-{await uow.QuerySingleOrDefaultAsync<int>("SELECT NEXT VALUE FOR dbo.EngagementSeq", null, ct)}";

    public async Task InsertAsync(IUnitOfWork uow, NewEngagement e, CancellationToken ct)
    {
        await uow.ExecuteAsync("""
            INSERT INTO dbo.Engagements
                (EngagementId, Name, PeriodType, Region, OwnerUserId, ReviewerUserId, Status, SubmissionCount, CreatedAt, LastActivityAt)
            VALUES
                (@EngagementId, @Name, @PeriodType, @Region, @OwnerUserId, @ReviewerUserId, @Status, @SubmissionCount, @CreatedAt, @LastActivityAt)
            """,
            new { e.EngagementId, e.Name, e.PeriodType, e.Region, e.OwnerUserId, e.ReviewerUserId, Status = e.Status.ToString(), e.SubmissionCount, e.CreatedAt, e.LastActivityAt }, ct);

        foreach (var p in e.Participants)
            await uow.ExecuteAsync("""
                INSERT INTO dbo.EngagementParticipants (EngagementId, UserId, Role, DisplayName, AddedAt)
                VALUES (@EngagementId, @UserId, @Role, @DisplayName, @AddedAt)
                """,
                new { e.EngagementId, p.UserId, p.Role, p.DisplayName, AddedAt = e.CreatedAt }, ct);
    }

    public async Task<bool> AnyAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<int>("SELECT COUNT(*) FROM dbo.Engagements", null, ct) > 0;
    }

    public async Task<EngagementWithRole?> GetAsync(string engagementId, Guid userId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<EngagementRow>("""
            SELECT EngagementId, Name, PeriodType, Region, OwnerUserId, ReviewerUserId, Status, CreatedAt, LastActivityAt, RowVer
            FROM dbo.Engagements WHERE EngagementId = @engagementId
            """, new { engagementId }, ct);
        if (row is null) return null;

        var role = await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT Role FROM dbo.EngagementParticipants WHERE EngagementId = @engagementId AND UserId = @userId",
            new { engagementId, userId }, ct);

        return new EngagementWithRole(row, role is null ? null : Enum.Parse<EngagementRole>(role));
    }

    public async Task<IReadOnlyList<ParticipantRow>> GetParticipantsAsync(string engagementId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return (await connection.QueryAsync<ParticipantRow>(
            "SELECT UserId, Role, DisplayName FROM dbo.EngagementParticipants WHERE EngagementId = @engagementId ORDER BY Role, DisplayName",
            new { engagementId }, ct)).ToList();
    }

    public async Task<ListResult> ListAsync(ListQuery query, CancellationToken ct)
    {
        // Fixed SQL fragments only; every user-supplied value is a parameter.
        var scope = query.Scope switch
        {
            ListScope.Owned => "AND e.OwnerUserId = @me",
            ListScope.Assigned => "AND me.Role IN (N'Auditor', N'CoAuditor') AND e.OwnerUserId <> @me",
            ListScope.Reviewing => "AND me.Role = N'Reviewer'",
            _ => string.Empty
        };

        var from = $"""
            FROM dbo.Engagements e
            JOIN dbo.EngagementParticipants me ON me.EngagementId = e.EngagementId AND me.UserId = @me
            LEFT JOIN dbo.EngagementParticipants rv ON rv.EngagementId = e.EngagementId AND rv.Role = N'Reviewer'
            WHERE 1 = 1 {scope}
              AND (@q IS NULL OR e.Name LIKE @q ESCAPE N'\' OR e.EngagementId LIKE @q ESCAPE N'\')
            """;

        var parameters = new
        {
            me = query.UserId,
            q = string.IsNullOrWhiteSpace(query.Search) ? null : $"%{EscapeLike(query.Search.Trim())}%",
            status = query.Status?.ToString(),
            skip = (query.Page - 1) * query.PageSize,
            take = query.PageSize
        };

        await using var connection = await connections.OpenAsync(ct);

        // Counts ignore the status filter so the cards can double as filters.
        var counts = (await connection.QueryAsync<(string Status, int Count)>(
            $"SELECT e.Status, COUNT(*) {from} GROUP BY e.Status", parameters, ct)).ToDictionary(c => c.Status, c => c.Count);

        var filtered = $"{from} AND (@status IS NULL OR e.Status = @status)";
        var total = await connection.QuerySingleOrDefaultAsync<int>($"SELECT COUNT(*) {filtered}", parameters, ct);

        var items = (await connection.QueryAsync<ListRow>($"""
            SELECT e.EngagementId, e.Name, e.Region, e.PeriodType, e.ReviewerUserId,
                   ISNULL(rv.DisplayName, N'') AS ReviewerName, e.Status, e.LastActivityAt, me.Role AS MyRole
            {filtered}
            ORDER BY e.LastActivityAt DESC, e.EngagementId DESC
            OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY
            """, parameters, ct)).ToList();

        return new ListResult(items, counts, total);
    }

    private static string EscapeLike(string value)
        => value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_").Replace("[", @"\[");
}
