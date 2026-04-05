// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using DuckDB.NET.Data;
using Honua.Core.Features.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;

namespace Honua.DuckDB.Features.Infrastructure;

/// <summary>
/// DuckDB implementation of database connection provider.
/// Opens read-only connections to a DuckDB database file.
/// </summary>
internal sealed class DuckDBConnectionProvider : IDatabaseConnectionProvider
{
    private readonly string _connectionString;
    private readonly ILogger<DuckDBConnectionProvider> _logger;
    private readonly DuckDBSpatialBootstrap _spatialBootstrap;

    public DuckDBConnectionProvider(
        string connectionString,
        DuckDBSpatialBootstrap spatialBootstrap,
        ILogger<DuckDBConnectionProvider> logger)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _spatialBootstrap = spatialBootstrap ?? throw new ArgumentNullException(nameof(spatialBootstrap));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new DuckDBConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await _spatialBootstrap.EnsureSpatialExtensionAsync(connection, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <inheritdoc />
    public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
        IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
        CancellationToken cancellationToken = default)
    {
        var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        return (connection, transaction);
    }

    /// <inheritdoc />
    public async Task<T> ExecuteWithDeadlockRetryAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        // DuckDB is single-writer; deadlocks are not expected for read-only workloads.
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
