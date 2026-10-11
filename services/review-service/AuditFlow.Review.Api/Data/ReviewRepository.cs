using AuditFlow.BuildingBlocks.Data;
using AuditFlow.Contracts.Api;
using Microsoft.Data.SqlClient;

namespace AuditFlow.Review.Api.Data;

public sealed record NewComment(Guid CommentId, string EngagementId, int Cycle, int TabNo, Guid AuthorUserId, string AuthorName, string Body, DateTimeOffset CreatedAt);

public sealed record NewDecision(
    Guid DecisionId, string EngagementId, int Cycle, string Decision, Guid ReviewerUserId, string ReviewerName, string? Comment, DateTimeOffset DecidedAt);

public interface IReviewRepository
{
    /// <summary>Idempotent: a cycle that already exists is left alone.</summary>
    Task EnsureCycleAsync(IUnitOfWork uow, string engagementId, int cycle, Guid? submittedBy, string? submittedByName, DateTimeOffset at, CancellationToken ct);

    Task<int?> LatestCycleAsync(string engagementId, CancellationToken ct);
    Task<int?> LatestCycleAsync(IUnitOfWork uow, string engagementId, CancellationToken ct);

    Task InsertCommentAsync(IUnitOfWork uow, NewComment comment, CancellationToken ct);

    /// <summary>Returns false when a decision already exists for this cycle (unique constraint).</summary>
    Task<bool> TryInsertDecisionAsync(IUnitOfWork uow, NewDecision decision, CancellationToken ct);

    Task<IReadOnlyList<TabCommentDto>> GetCommentsAsync(string engagementId, CancellationToken ct);
    Task<IReadOnlyList<ReviewDecisionDto>> GetDecisionsAsync(string engagementId, CancellationToken ct);
}

public sealed class ReviewRepository(IDbConnectionFactory connections) : IReviewRepository
{
    private const int UniqueConstraintViolation = 2627;
    private const int UniqueIndexViolation = 2601;

    public Task EnsureCycleAsync(IUnitOfWork uow, string engagementId, int cycle, Guid? submittedBy, string? submittedByName, DateTimeOffset at, CancellationToken ct)
        => uow.ExecuteAsync("""
            IF NOT EXISTS (SELECT 1 FROM dbo.ReviewCycles WITH (UPDLOCK, HOLDLOCK) WHERE EngagementId = @engagementId AND Cycle = @cycle)
                INSERT INTO dbo.ReviewCycles (EngagementId, Cycle, SubmittedBy, SubmittedByName, SubmittedAt)
                VALUES (@engagementId, @cycle, @submittedBy, @submittedByName, @at)
            """, new { engagementId, cycle, submittedBy, submittedByName, at }, ct);

    public async Task<int?> LatestCycleAsync(string engagementId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<int?>("SELECT MAX(Cycle) FROM dbo.ReviewCycles WHERE EngagementId = @engagementId", new { engagementId }, ct);
    }

    public Task<int?> LatestCycleAsync(IUnitOfWork uow, string engagementId, CancellationToken ct)
        => uow.QuerySingleOrDefaultAsync<int?>("SELECT MAX(Cycle) FROM dbo.ReviewCycles WHERE EngagementId = @engagementId", new { engagementId }, ct);

    public Task InsertCommentAsync(IUnitOfWork uow, NewComment c, CancellationToken ct)
        => uow.ExecuteAsync("""
            INSERT INTO dbo.TabComments (CommentId, EngagementId, Cycle, TabNo, AuthorUserId, AuthorName, Body, CreatedAt)
            VALUES (@CommentId, @EngagementId, @Cycle, @TabNo, @AuthorUserId, @AuthorName, @Body, @CreatedAt)
            """, c, ct);

    public async Task<bool> TryInsertDecisionAsync(IUnitOfWork uow, NewDecision d, CancellationToken ct)
    {
        try
        {
            await uow.ExecuteAsync("""
                INSERT INTO dbo.ReviewDecisions (DecisionId, EngagementId, Cycle, Decision, ReviewerUserId, ReviewerName, Comment, DecidedAt)
                VALUES (@DecisionId, @EngagementId, @Cycle, @Decision, @ReviewerUserId, @ReviewerName, @Comment, @DecidedAt)
                """, d, ct);
            return true;
        }
        catch (SqlException ex) when (ex.Number is UniqueConstraintViolation or UniqueIndexViolation)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<TabCommentDto>> GetCommentsAsync(string engagementId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return (await connection.QueryAsync<TabCommentDto>("""
            SELECT CommentId, TabNo, Cycle, AuthorUserId, AuthorName, Body, CreatedAt
            FROM dbo.TabComments WHERE EngagementId = @engagementId
            ORDER BY Cycle, TabNo, CreatedAt
            """, new { engagementId }, ct)).ToList();
    }

    public async Task<IReadOnlyList<ReviewDecisionDto>> GetDecisionsAsync(string engagementId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return (await connection.QueryAsync<ReviewDecisionDto>("""
            SELECT DecisionId, Cycle, Decision, ReviewerUserId, ReviewerName, Comment, DecidedAt
            FROM dbo.ReviewDecisions WHERE EngagementId = @engagementId
            ORDER BY Cycle
            """, new { engagementId }, ct)).ToList();
    }
}
