// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.HealthCheck.Abstractions;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Honua.Postgres.Features.HealthCheck;

/// <summary>
/// PostgreSQL implementation of database health checking
/// </summary>
/// <remarks>
/// Marked as internal to prevent exposure of database-specific implementations
/// outside the Infrastructure layer (Clean Architecture principle).
/// </remarks>
internal sealed class PostgresDatabaseHealthChecker(IConfiguration configuration) : IDatabaseHealthChecker
{
    private readonly string? _connectionString = configuration.GetConnectionString("DefaultConnection");

    /// <summary>
    /// Checks PostgreSQL database connectivity and responsiveness
    /// </summary>
    public async Task<bool> IsDatabaseHealthyAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_connectionString))
        {
            return true; // No database configured - considered healthy for development
        }

        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // Execute a simple query to verify database is responsive with timeout
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            command.CommandTimeout = 5; // 5-second timeout for health checks
            _ = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch
        {
            return false;
        }
    }
}
