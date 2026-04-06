// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using Honua.Core.Exceptions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Postgres.Features.Infrastructure.Monitoring;
using Honua.Postgres.Features.Infrastructure.Resilience;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Postgres.Features.Infrastructure.Caching;

/// <summary>
/// Enhanced PostgreSQL connection provider with prepared statement caching support
/// </summary>
/// <remarks>
/// <para>
/// Extends the base connection provider with intelligent prepared statement caching
/// to optimize frequently-executed queries. Maintains compatibility with existing
/// connection patterns while adding transparent performance improvements.
/// </para>
/// <para>
/// PERFORMANCE FEATURES:
/// - Automatic prepared statement management
/// - Connection-aware caching for optimal resource usage
/// - Graceful degradation when caching is disabled
/// - Comprehensive monitoring and logging capabilities
/// </para>
/// <para>
/// SECURITY: All existing parameterized query patterns remain secure.
/// The caching layer only optimizes execution, not query construction.
/// </para>
/// </remarks>
internal sealed partial class CachingDatabaseConnectionProvider : IPrimaryDatabaseConnectionProvider
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<CachingDatabaseConnectionProvider> _logger;
    private readonly ISchemaContext? _schemaContext;
    private readonly IActiveDbConnectionTracker? _activeDbConnectionTracker;
    private readonly QueryConcurrencyGate? _concurrencyGate;

    // ActivitySource for tracing (same name as HonuaTelemetry for correlation)
    private static readonly ActivitySource _activitySource = new("Honua", "1.0.0");

    public CachingDatabaseConnectionProvider(
        NpgsqlDataSource dataSource,
        ILogger<CachingDatabaseConnectionProvider> logger,
        ISchemaContext? schemaContext = null,
        IActiveDbConnectionTracker? activeDbConnectionTracker = null,
        QueryConcurrencyGate? concurrencyGate = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _schemaContext = schemaContext;
        _activeDbConnectionTracker = activeDbConnectionTracker;
        _concurrencyGate = concurrencyGate;
    }

    /// <summary>
    /// Opens a PostgreSQL connection with automatic retry for transient failures
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A caching-enabled PostgreSQL connection</returns>
    public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (_concurrencyGate is not null)
        {
            if (!await _concurrencyGate.WaitAsync(cancellationToken).ConfigureAwait(false))
            {
                ConnectionAcquisitionTimedOut(_logger, null);
                throw new ServiceUnavailableException("Connection acquisition timed out — server is under heavy load.");
            }
        }

        try
        {
            // Use the resilience extension method with logging callback
            var connection = await _dataSource.OpenConnectionWithRetryAsync(
                onRetry: (ex, delay, attempt) => DatabaseConnectionRetry(_logger, attempt, ex.Message, ex),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            await SchemaSearchPath.ApplyAsync(connection, _schemaContext?.CurrentSchema, cancellationToken: cancellationToken).ConfigureAwait(false);
            DbConnectionTracking.Track(connection, _activeDbConnectionTracker);

            return _concurrencyGate is not null
                ? new SemaphoreReleasingConnection(connection, () => _concurrencyGate.Release())
                : connection;
        }
        catch
        {
            _concurrencyGate?.Release();
            throw;
        }
    }

    /// <summary>
    /// Opens a PostgreSQL connection and begins a transaction with the specified isolation level
    /// </summary>
    /// <param name="isolationLevel">The isolation level for the transaction</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A tuple containing the open connection and active transaction</returns>
    public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
        IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
        CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity("honua.db.transaction", ActivityKind.Client);
        activity?.SetTag("db.system", "postgresql");
        activity?.SetTag("db.transaction.isolation_level", isolationLevel.ToString());

        // Get the connection using existing resilience logic
        var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Begin transaction with specified isolation level
            var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);

            activity?.SetTag("db.transaction.id", transaction.GetHashCode().ToString(CultureInfo.InvariantCulture));
            activity?.SetStatus(ActivityStatusCode.Ok);

            DatabaseTransactionStarted(_logger, isolationLevel);

            return (connection, transaction);
        }
        catch
        {
            // If transaction creation fails, dispose the connection
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Executes a database operation with deadlock retry policy
    /// </summary>
    /// <typeparam name="T">Return type of the operation</typeparam>
    /// <param name="operation">The database operation to execute</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the database operation</returns>
    public async Task<T> ExecuteWithDeadlockRetryAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity("honua.db.deadlock_retry", ActivityKind.Client);
        activity?.SetTag("db.system", "postgresql");

        return await _dataSource.ExecuteWithDeadlockRetryAsync(
            operation,
            onRetry: (ex, delay, attempt) =>
            {
                activity?.AddEvent(new ActivityEvent("deadlock_retry", tags: new ActivityTagsCollection
                {
                    { "attempt", attempt },
                    { "delay_ms", delay.TotalMilliseconds },
                    { "error.message", ex.Message }
                }));

                DatabaseDeadlockRetry(_logger, attempt, ex.Message, ex);
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes a database operation with deadlock retry policy (no return value)
    /// </summary>
    /// <param name="operation">The database operation to execute</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the completion of the operation</returns>
    public async Task ExecuteWithDeadlockRetryAsync(
        Func<Task> operation,
        CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity("honua.db.deadlock_retry", ActivityKind.Client);
        activity?.SetTag("db.system", "postgresql");

        await _dataSource.ExecuteWithDeadlockRetryAsync(
            operation,
            onRetry: (ex, delay, attempt) =>
            {
                activity?.AddEvent(new ActivityEvent("deadlock_retry", tags: new ActivityTagsCollection
                {
                    { "attempt", attempt },
                    { "delay_ms", delay.TotalMilliseconds },
                    { "error.message", ex.Message }
                }));

                DatabaseDeadlockRetry(_logger, attempt, ex.Message, ex);
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(1, LogLevel.Warning, "Database connection retry attempt {Attempt}: {ErrorMessage}")]
    private static partial void DatabaseConnectionRetry(ILogger logger, int attempt, string errorMessage, Exception exception);

    [LoggerMessage(2, LogLevel.Warning, "Database deadlock detected, retry attempt {Attempt}: {ErrorMessage}")]
    private static partial void DatabaseDeadlockRetry(ILogger logger, int attempt, string errorMessage, Exception exception);

    [LoggerMessage(3, LogLevel.Debug, "Started transaction with isolation level {IsolationLevel}")]
    private static partial void DatabaseTransactionStarted(ILogger logger, IsolationLevel isolationLevel);

    [LoggerMessage(4, LogLevel.Warning, "Connection acquisition timed out — server is under heavy load")]
    private static partial void ConnectionAcquisitionTimedOut(ILogger logger, Exception? exception);
}
