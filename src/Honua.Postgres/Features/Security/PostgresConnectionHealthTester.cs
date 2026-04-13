// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Security;
using Honua.Core.Features.Security.Abstractions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Postgres.Features.Security;

internal sealed class PostgresConnectionHealthTester : IConnectionHealthTester
{
    private static readonly Action<ILogger, Exception?> _logHealthCheckSuccess =
        LoggerMessage.Define(LogLevel.Debug, new EventId(2101, nameof(_logHealthCheckSuccess)),
            "Connection test succeeded");

    private static readonly Action<ILogger, object?, Exception?> _logHealthCheckUnexpectedResult =
        LoggerMessage.Define<object?>(LogLevel.Warning, new EventId(2102, nameof(_logHealthCheckUnexpectedResult)),
            "Connection test returned unexpected result: {Result}");

    private static readonly Action<ILogger, string, Exception?> _logHealthCheckFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(2103, nameof(_logHealthCheckFailed)),
            "Connection test failed: {Error}");

    private readonly ILogger<PostgresConnectionHealthTester> _logger;

    public PostgresConnectionHealthTester(ILogger<PostgresConnectionHealthTester> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ConnectionHealthStatus> TestConnectionAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string is required.", nameof(connectionString));
        }

        try
        {
            using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            command.CommandTimeout = 5;

            var result = await command.ExecuteScalarAsync(cancellationToken);
            var isHealthy = result?.ToString() == "1";

            if (isHealthy)
            {
                _logHealthCheckSuccess(_logger, null);
                return ConnectionHealthStatus.Healthy;
            }
            else
            {
                _logHealthCheckUnexpectedResult(_logger, result, null);
                return ConnectionHealthStatus.Warning;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logHealthCheckFailed(_logger, ex.GetType().Name, ex);
            return ConnectionHealthStatus.Unhealthy;
        }
    }
}
