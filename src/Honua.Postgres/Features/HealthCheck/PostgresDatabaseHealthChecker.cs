// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.HealthCheck.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.HealthCheck;

/// <summary>
/// PostgreSQL implementation of database health checking
/// </summary>
/// <remarks>
/// Marked as internal to prevent exposure of database-specific implementations
/// outside the Infrastructure layer (Clean Architecture principle).
/// </remarks>
internal sealed partial class PostgresDatabaseHealthChecker(
    IDatabaseConnectionProvider connectionProvider,
    ILogger<PostgresDatabaseHealthChecker> logger)
    : IDatabaseHealthChecker
{
    private readonly IDatabaseConnectionProvider _connectionProvider =
        connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));

    private readonly ILogger<PostgresDatabaseHealthChecker> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Checks PostgreSQL database connectivity and responsiveness
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
        catch (Exception ex)
        {
            Log.HealthCheckFailed(_logger, ex);
            return false;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 2100,
            Level = LogLevel.Warning,
            Message = "PostgreSQL database health check failed")]
        public static partial void HealthCheckFailed(ILogger logger, Exception exception);
    }
}
