using AuditFlow.BuildingBlocks.Data;
using AuditFlow.Contracts.Api;
using Microsoft.AspNetCore.Identity;

namespace AuditFlow.Auth.Api.Data;

/// <summary>Seeds the demo users (one per role, two reviewers) on first run. Dev/demo only.</summary>
public sealed class DemoUserSeeder(IUserRepository users, IPasswordHasher<UserRow> hasher, ILogger<DemoUserSeeder> logger) : IDataSeeder
{
    public async Task SeedAsync(CancellationToken ct)
    {
        if (await users.CountAsync(ct) > 0)
            return;

        foreach (var demo in DemoData.Users)
        {
            var row = new UserRow(demo.UserId, demo.Email, demo.DisplayName, string.Empty, demo.HomeRole.ToString());
            await users.InsertAsync(row with { PasswordHash = hasher.HashPassword(row, DemoData.Password) }, ct);
        }

        logger.LogInformation("Seeded {Count} demo users", DemoData.Users.Count);
    }
}
