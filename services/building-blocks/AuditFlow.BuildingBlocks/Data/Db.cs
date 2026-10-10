using System.Data.Common;
using Dapper;
using Microsoft.Data.SqlClient;

namespace AuditFlow.BuildingBlocks.Data;

public interface IDbConnectionFactory
{
    Task<DbConnection> OpenAsync(CancellationToken cancellationToken);
}

public sealed class SqlConnectionFactory(string connectionString) : IDbConnectionFactory
{
    public async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}

/// <summary>An open connection with an explicit transaction. Disposing without committing rolls back.</summary>
public interface IUnitOfWork : IAsyncDisposable
{
    DbConnection Connection { get; }
    DbTransaction Transaction { get; }
    Task CommitAsync(CancellationToken cancellationToken);
}

public interface IUnitOfWorkFactory
{
    Task<IUnitOfWork> BeginAsync(CancellationToken cancellationToken);
}

public sealed class SqlUnitOfWorkFactory(IDbConnectionFactory connections) : IUnitOfWorkFactory
{
    public async Task<IUnitOfWork> BeginAsync(CancellationToken cancellationToken)
    {
        var connection = await connections.OpenAsync(cancellationToken);
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        return new SqlUnitOfWork(connection, transaction);
    }

    private sealed class SqlUnitOfWork(DbConnection connection, DbTransaction transaction) : IUnitOfWork
    {
        public DbConnection Connection => connection;
        public DbTransaction Transaction => transaction;

        public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync(); // rolls back if not committed
            await connection.DisposeAsync();
        }
    }
}

/// <summary>Thin Dapper helpers so repositories stay explicit SQL without ceremony.</summary>
public static class DapperExtensions
{
    public static Task<int> ExecuteAsync(this IUnitOfWork uow, string sql, object? param, CancellationToken ct)
        => uow.Connection.ExecuteAsync(new CommandDefinition(sql, param, uow.Transaction, cancellationToken: ct));

    public static Task<T?> QuerySingleOrDefaultAsync<T>(this IUnitOfWork uow, string sql, object? param, CancellationToken ct)
        => uow.Connection.QuerySingleOrDefaultAsync<T>(new CommandDefinition(sql, param, uow.Transaction, cancellationToken: ct));

    public static Task<IEnumerable<T>> QueryAsync<T>(this IUnitOfWork uow, string sql, object? param, CancellationToken ct)
        => uow.Connection.QueryAsync<T>(new CommandDefinition(sql, param, uow.Transaction, cancellationToken: ct));

    public static Task<IEnumerable<T>> QueryAsync<T>(this DbConnection connection, string sql, object? param, CancellationToken ct)
        => connection.QueryAsync<T>(new CommandDefinition(sql, param, cancellationToken: ct));

    public static Task<T?> QuerySingleOrDefaultAsync<T>(this DbConnection connection, string sql, object? param, CancellationToken ct)
        => connection.QuerySingleOrDefaultAsync<T>(new CommandDefinition(sql, param, cancellationToken: ct));
}
