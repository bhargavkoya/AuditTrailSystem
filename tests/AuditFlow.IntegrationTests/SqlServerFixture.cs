using AuditFlow.BuildingBlocks.Data;
using AuditFlow.BuildingBlocks.Hosting;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.MsSql;

namespace AuditFlow.IntegrationTests;

/// <summary>One SQL Server container for the whole run; each test gets its own migrated database.</summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer container = new MsSqlBuilder().WithImage("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    public async Task<TestDatabase> CreateDatabaseAsync(params System.Reflection.Assembly[] serviceMigrations)
    {
        var builder = new SqlConnectionStringBuilder(container.GetConnectionString())
        {
            InitialCatalog = $"AuditFlow_Test_{Guid.NewGuid():N}",
            TrustServerCertificate = true
        };
        var connectionString = builder.ConnectionString;

        var migrations = new MigrationHostedService(
            connectionString,
            [new MigrationAssembly(typeof(ServiceDefaultsExtensions).Assembly), .. serviceMigrations.Select(a => new MigrationAssembly(a))],
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MigrationHostedService>.Instance);
        await migrations.StartAsync(CancellationToken.None);

        var connections = new SqlConnectionFactory(connectionString);
        return new TestDatabase(connectionString, connections, new SqlUnitOfWorkFactory(connections));
    }
}

public sealed record TestDatabase(string ConnectionString, SqlConnectionFactory Connections, SqlUnitOfWorkFactory UnitOfWork)
{
    public async Task<T> ScalarAsync<T>(string sql, object? param = null)
    {
        await using var connection = await Connections.OpenAsync(CancellationToken.None);
        return await connection.ExecuteScalarAsync<T>(sql, param) ?? throw new InvalidOperationException("No value");
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = await Connections.OpenAsync(CancellationToken.None);
        await connection.ExecuteAsync(sql);
    }
}

[CollectionDefinition(Name)]
public sealed class SqlCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "sql";
}
