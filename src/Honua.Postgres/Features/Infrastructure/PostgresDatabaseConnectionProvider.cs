// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using Honua.Core.Exceptions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Postgres.Features.Infrastructure.Resilience;
using Microsoft.Extensions.Logging;
using Npgsql;
using Polly.CircuitBreaker;

namespace Honua.Postgres.Features.Infrastructure;

/// <summary>
/// PostgreSQL implementation of database connection provider with Polly resilience policies
/// </summary>
/// <remarks>
/// Provides reliable database connections with:
/// - Automatic retry for transient connection errors
/// - Exponential backoff strategy
/// - Structured logging for retry attempts
/// - Proper error handling and resource management
/// - OpenTelemetry tracing for connection acquisition
/// </remarks>
internal sealed class PostgresDatabaseConnectionProvider(
    NpgsqlDataSource dataSource,
    ILogger<PostgresDatabaseConnectionProvider> logger,
    ISchemaContext? schemaContext = null) : IDatabaseConnectionProvider
{
    // ActivitySource for tracing connection operations (same name as HonuaTelemetry for correlation)
    private static readonly ActivitySource _activitySource = new("Honua", "1.0.0");

    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    private readonly ILogger<PostgresDatabaseConnectionProvider> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ISchemaContext? _schemaContext = schemaContext;

    /// <summary>
    /// Opens a PostgreSQL connection with automatic retry for transient failures
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An open PostgreSQL connection</returns>
    public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity("honua.db.connection", ActivityKind.Client);
        activity?.SetTag("db.system", "postgresql");

        int retryCount = 0;

        // Use the resilience extension method with logging callback
        NpgsqlConnection connection;
        try
        {
            connection = await _dataSource.OpenConnectionWithRetryAsync(
                onRetry: (ex, delay, attempt) =>
                {
                    retryCount = attempt;
                    activity?.AddEvent(new ActivityEvent("retry", tags: new ActivityTagsCollection
                    {
                        { "attempt", attempt },
                        { "delay_ms", delay.TotalMilliseconds },
                        { "error.message", ex.Message }
                    }));

                    PostgresLog.ConnectionRetry(_logger, attempt, ex.Message);
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (BrokenCircuitException)
        {
            throw new ServiceUnavailableException(
                "Database connection failed.",
                ResiliencePolicyOptions.Default.RetryAfterSeconds);
        }
        catch (Exception ex)
        {
            throw new ServiceUnavailableException("Database connection failed.", ex);
        }

        // Apply schema context if specified
        if (_schemaContext?.CurrentSchema != null)
        {
            activity?.SetTag("db.schema", _schemaContext.CurrentSchema);
            await SchemaSearchPath.ApplyAsync(connection, _schemaContext.CurrentSchema, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await SchemaSearchPath.ApplyAsync(connection, null, cancellationToken).ConfigureAwait(false);
        }

        // Record connection success with retry count
        activity?.SetTag("db.connection.retry_count", retryCount);
        activity?.SetStatus(ActivityStatusCode.Ok);

        return connection;
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

            PostgresLog.TransactionStarted(_logger, isolationLevel.ToString());

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

                PostgresLog.DeadlockRetry(_logger, attempt, ex.Message);
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

                PostgresLog.DeadlockRetry(_logger, attempt, ex.Message);
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Source-generated logging methods for PostgreSQL operations.
/// </summary>
internal static partial class PostgresLog
{
    [LoggerMessage(
        EventId = 6001,
        Level = LogLevel.Warning,
        Message = "Database connection retry attempt {Attempt}: {ErrorMessage}")]
    public static partial void ConnectionRetry(ILogger logger, int attempt, string errorMessage);

    [LoggerMessage(
        EventId = 6002,
        Level = LogLevel.Warning,
        Message = "Database deadlock detected, retry attempt {Attempt}: {ErrorMessage}")]
    public static partial void DeadlockRetry(ILogger logger, int attempt, string errorMessage);

    [LoggerMessage(
        EventId = 6003,
        Level = LogLevel.Debug,
        Message = "Started transaction with isolation level {IsolationLevel}")]
    public static partial void TransactionStarted(ILogger logger, string isolationLevel);
}
