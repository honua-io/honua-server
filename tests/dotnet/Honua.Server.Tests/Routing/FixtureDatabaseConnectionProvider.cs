// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Session;
using Npgsql;

namespace Honua.Server.Tests.Routing;

internal sealed class FixtureDatabaseConnectionProvider : IAdoNetDatabaseConnectionProvider
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _connectionString;

    public FixtureDatabaseConnectionProvider(NpgsqlDataSource dataSource, string connectionString)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public string GetConnectionString() => _connectionString;

    public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        return await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
        IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
        CancellationToken cancellationToken = default)
    {
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
        return (connection, transaction);
    }

    public async Task<T> ExecuteWithDeadlockRetryAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return await operation().ConfigureAwait(false);
    }

    public async Task ExecuteWithDeadlockRetryAsync(
        Func<Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await operation().ConfigureAwait(false);
    }
}

internal sealed class FixtureDatabaseSessionFactory(FixtureDatabaseConnectionProvider connectionProvider) : IDatabaseSessionFactory
{
    private readonly FixtureDatabaseConnectionProvider _connectionProvider =
        connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));

    public async Task<IDatabaseSession> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return new FixtureDatabaseSession(connection);
    }

    public async Task<IDatabaseSession> OpenTransactionAsync(
        IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
        CancellationToken cancellationToken = default)
    {
        var (connection, transaction) = await _connectionProvider
            .OpenTransactionAsync(isolationLevel, cancellationToken)
            .ConfigureAwait(false);
        return new FixtureDatabaseSession(connection, transaction);
    }

    private sealed class FixtureDatabaseSession(DbConnection connection, DbTransaction? transaction = null)
        : AdoNetDatabaseSession(connection, transaction, ownsConnection: true);
}
