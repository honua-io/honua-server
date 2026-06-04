// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using Honua.Core.Features.Infrastructure.Abstractions;
using Npgsql;

namespace Honua.Server.Tests.Routing;

/// <summary>
/// Minimal <see cref="IDatabaseConnectionProvider"/> test double that wraps a
/// <see cref="NpgsqlDataSource"/> from the pgRouting fixture. Lets the
/// integration test construct <c>PgRoutingProvider</c> directly without the full
/// Postgres provider stack. Deadlock-retry is a passthrough (no retry needed for
/// read-only routing queries in the test).
/// </summary>
internal sealed class FixtureDatabaseConnectionProvider : IDatabaseConnectionProvider
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _connectionString;

    /// <summary>
    /// Initializes a new instance of the <see cref="FixtureDatabaseConnectionProvider"/> class.
    /// </summary>
    /// <param name="dataSource">Data source backing the fixture database.</param>
    /// <param name="connectionString">Connection string for the fixture database.</param>
    public FixtureDatabaseConnectionProvider(NpgsqlDataSource dataSource, string connectionString)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    /// <inheritdoc />
    public string GetConnectionString() => _connectionString;

    /// <inheritdoc />
    public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        return await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
        IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
        CancellationToken cancellationToken = default)
    {
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
        return (connection, transaction);
    }

    /// <inheritdoc />
    public async Task<T> ExecuteWithDeadlockRetryAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return await operation().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ExecuteWithDeadlockRetryAsync(
        Func<Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await operation().ConfigureAwait(false);
    }
}
