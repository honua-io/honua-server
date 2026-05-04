// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.HealthCheck.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;

namespace Honua.MySql.Features.HealthCheck;

/// <summary>
/// MySQL/MariaDB implementation of <see cref="IDatabaseHealthChecker"/>.
/// </summary>
/// <remarks>
/// The provider routes connections through the shared <see cref="IDatabaseConnectionProvider"/>
/// so the readiness probe exercises the same pooled <c>MySqlDataSource</c> the feature store uses.
/// Mirrors the Postgres implementation: cheap <c>SELECT 1</c> with a 5s command timeout.
/// </remarks>
internal sealed partial class MySqlDatabaseHealthChecker(
    IDatabaseConnectionProvider connectionProvider,
    ILogger<MySqlDatabaseHealthChecker> logger)
    : IDatabaseHealthChecker
{
    private readonly IDatabaseConnectionProvider _connectionProvider =
        connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));

    private readonly ILogger<MySqlDatabaseHealthChecker> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

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
            EventId = 8903,
            Level = LogLevel.Warning,
            Message = "MySQL/MariaDB database health check failed")]
        public static partial void HealthCheckFailed(ILogger logger, Exception exception);
    }
}
