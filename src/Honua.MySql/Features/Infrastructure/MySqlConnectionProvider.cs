// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using Honua.Core.Features.Infrastructure.Abstractions;
using MySqlConnector;

namespace Honua.MySql.Features.Infrastructure;

/// <summary>
/// MySQL/MariaDB implementation of <see cref="IDatabaseConnectionProvider"/>.
/// Wraps a pooled <see cref="MySqlDataSource"/> for read-only feature workloads.
/// </summary>
internal sealed class MySqlConnectionProvider : IDatabaseConnectionProvider
{
    private readonly MySqlDataSource _dataSource;

    public MySqlConnectionProvider(MySqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    /// <inheritdoc />
    public string GetConnectionString() => _dataSource.ConnectionString;

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
        var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
            return (connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<T> ExecuteWithDeadlockRetryAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        // Read-only provider; deadlock retry is not relevant for SELECT-only workloads.
        return await operation().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ExecuteWithDeadlockRetryAsync(
        Func<Task> operation,
        CancellationToken cancellationToken = default)
    {
        await operation().ConfigureAwait(false);
    }
}
