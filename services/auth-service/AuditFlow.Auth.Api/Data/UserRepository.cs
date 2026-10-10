using AuditFlow.BuildingBlocks.Data;
using AuditFlow.Contracts.Api;

namespace AuditFlow.Auth.Api.Data;

public sealed record UserRow(Guid UserId, string Email, string DisplayName, string PasswordHash, string HomeRole)
{
    public EngagementRole Role => Enum.Parse<EngagementRole>(HomeRole);
}

public interface IUserRepository
{
    Task<UserRow?> FindByEmailAsync(string email, CancellationToken ct);
    Task<IReadOnlyList<UserRow>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);
    Task<IReadOnlyList<UserRow>> ListAsync(EngagementRole? homeRole, CancellationToken ct);
    Task<int> CountAsync(CancellationToken ct);
    Task InsertAsync(UserRow user, CancellationToken ct);
}

public sealed class UserRepository(IDbConnectionFactory connections, IUnitOfWorkFactory unitOfWork) : IUserRepository
{
    private const string Columns = "UserId, Email, DisplayName, PasswordHash, HomeRole";

    public async Task<UserRow?> FindByEmailAsync(string email, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<UserRow>(
            $"SELECT {Columns} FROM dbo.Users WHERE Email = @email", new { email }, ct);
    }

    public async Task<IReadOnlyList<UserRow>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        await using var connection = await connections.OpenAsync(ct);
        return (await connection.QueryAsync<UserRow>(
            $"SELECT {Columns} FROM dbo.Users WHERE UserId IN @ids", new { ids }, ct)).ToList();
    }

    public async Task<IReadOnlyList<UserRow>> ListAsync(EngagementRole? homeRole, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return (await connection.QueryAsync<UserRow>(
            $"SELECT {Columns} FROM dbo.Users WHERE (@role IS NULL OR HomeRole = @role) ORDER BY DisplayName",
            new { role = homeRole?.ToString() }, ct)).ToList();
    }

    public async Task<int> CountAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<int>("SELECT COUNT(*) FROM dbo.Users", null, ct);
    }

    public async Task InsertAsync(UserRow user, CancellationToken ct)
    {
        await using var uow = await unitOfWork.BeginAsync(ct);
        await uow.ExecuteAsync(
            "INSERT INTO dbo.Users (UserId, Email, DisplayName, PasswordHash, HomeRole) VALUES (@UserId, @Email, @DisplayName, @PasswordHash, @HomeRole)",
            user, ct);
        await uow.CommitAsync(ct);
    }
}
