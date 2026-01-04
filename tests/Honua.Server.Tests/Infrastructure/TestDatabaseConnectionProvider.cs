// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using Honua.Core.Features.Infrastructure.Abstractions;
using Npgsql;

namespace Honua.Server.Tests.Infrastructure;

/// <summary>
/// Test implementation of database connection provider for unit tests
/// </summary>
internal sealed class TestDatabaseConnectionProvider : IDatabaseConnectionProvider
{
    private readonly NpgsqlDataSource _dataSource;

    public TestDatabaseConnectionProvider(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        return await _dataSource.OpenConnectionAsync(cancellationToken);
    }

    public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
        IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
        CancellationToken cancellationToken = default)
    {
        var connection = await OpenConnectionAsync(cancellationToken);

        try
        {
            var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken);
            return (connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<T> ExecuteWithDeadlockRetryAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await operation();
    }

    public async Task ExecuteWithDeadlockRetryAsync(
        Func<Task> operation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await operation();
    }
}
