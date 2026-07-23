// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.HealthCheck.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;

namespace Honua.DuckDB.Features.HealthCheck;

/// <summary>
/// DuckDB implementation of <see cref="IDatabaseHealthChecker"/>.
/// </summary>
/// <remarks>
/// Routes through the shared <see cref="IAdoNetDatabaseConnectionProvider"/> so the
/// readiness probe exercises the same pooled DuckDB connection the feature store uses.
/// Mirrors the MySQL and Postgres implementations: cheap <c>SELECT 1</c> with a 5 s
/// command timeout. Required so <c>/healthz/ready</c> does not throw
/// <see cref="InvalidOperationException"/> when <c>DataSource:Provider=duckdb</c>
/// (PA-158).
/// </remarks>
internal sealed partial class DuckDBDatabaseHealthChecker(
    IAdoNetDatabaseConnectionProvider connectionProvider,
    ILogger<DuckDBDatabaseHealthChecker> logger)
    : IDatabaseHealthChecker
{
    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider =
        connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));

    private readonly ILogger<DuckDBDatabaseHealthChecker> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Checks DuckDB connectivity and responsiveness via a lightweight probe query.
    /// </summary>
    public async Task<bool> IsDatabaseHealthyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            command.CommandTimeout = 5;
            _ = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // Intentionally generic: a health check must never throw regardless of the
        // underlying failure (connection, timeout, driver bug); any failure here means
        // "unhealthy", logged for diagnosis.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.HealthCheckFailed(_logger, ex);
            return false;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 8806,
            Level = LogLevel.Warning,
            Message = "DuckDB database health check failed")]
        public static partial void HealthCheckFailed(ILogger logger, Exception exception);
    }
}
