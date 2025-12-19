// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Infrastructure.Resilience;
using Microsoft.Extensions.Logging;
using Npgsql;

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
/// </remarks>
internal sealed class PostgresDatabaseConnectionProvider : IDatabaseConnectionProvider
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PostgresDatabaseConnectionProvider> _logger;

    public PostgresDatabaseConnectionProvider(
        NpgsqlDataSource dataSource,
        ILogger<PostgresDatabaseConnectionProvider> logger)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Opens a PostgreSQL connection with automatic retry for transient failures
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An open PostgreSQL connection</returns>
    public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        // Use the resilience extension method with logging callback
        var connection = await _dataSource.OpenConnectionWithRetryAsync(
            onRetry: (ex, delay, attempt) =>
            {
#pragma warning disable CA1848 // Use LoggerMessage delegates for performance
                _logger.LogWarning("Database connection retry attempt {Attempt}: {ErrorMessage}", attempt, ex.Message);
#pragma warning restore CA1848
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return connection;
    }
}
