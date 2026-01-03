// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using System.Diagnostics;
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
}
