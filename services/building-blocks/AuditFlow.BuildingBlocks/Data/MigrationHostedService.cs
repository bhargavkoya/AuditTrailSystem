using System.Reflection;
using DbUp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AuditFlow.BuildingBlocks.Data;

public sealed record MigrationAssembly(Assembly Assembly);

/// <summary>Seeds demo data after migrations. Implementations must be idempotent.</summary>
public interface IDataSeeder
{
    Task SeedAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Creates the service database if missing, applies embedded SQL scripts (DbUp), then runs seeders.
/// Registered first so it completes before the outbox dispatcher and subscribers start.
/// </summary>
public sealed class MigrationHostedService(
    string connectionString,
    IEnumerable<MigrationAssembly> assemblies,
    IServiceScopeFactory scopes,
    ILogger<MigrationHostedService> logger) : IHostedService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        while (true)
        {
            try
            {
                Migrate();
                break;
            }
            catch (Exception ex) when (DateTime.UtcNow - started < Timeout && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Database not ready yet ({Message}); retrying in 3s", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            }
        }

        await using var scope = scopes.CreateAsyncScope();
        foreach (var seeder in scope.ServiceProvider.GetServices<IDataSeeder>())
            await seeder.SeedAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void Migrate()
    {
        EnsureDatabase.For.SqlDatabase(connectionString);

        var builder = DeployChanges.To.SqlDatabase(connectionString).WithTransactionPerScript().LogToConsole();
        foreach (var a in assemblies)
            builder = builder.WithScriptsEmbeddedInAssembly(a.Assembly, name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase));

        var result = builder.Build().PerformUpgrade();
        if (!result.Successful)
            throw result.Error;

        logger.LogInformation("Database migrations applied");
    }
}
