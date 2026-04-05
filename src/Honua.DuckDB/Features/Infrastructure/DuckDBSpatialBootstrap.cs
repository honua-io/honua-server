// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using Microsoft.Extensions.Logging;

namespace Honua.DuckDB.Features.Infrastructure;

/// <summary>
/// Loads the DuckDB spatial extension on first use.
/// Supports optional offline extension path for air-gapped deployments.
/// </summary>
internal sealed class DuckDBSpatialBootstrap
{
    private readonly string? _extensionPath;
    private readonly ILogger<DuckDBSpatialBootstrap> _logger;

    public DuckDBSpatialBootstrap(string? extensionPath, ILogger<DuckDBSpatialBootstrap> logger)
    {
        _extensionPath = extensionPath;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Ensures the spatial extension is installed and loaded on the given connection.
    /// </summary>
    public async Task EnsureSpatialExtensionAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_extensionPath))
        {
            _logger.LogInformation("Loading DuckDB spatial extension from offline path: {Path}", _extensionPath);
            await ExecuteNonQueryAsync(connection, $"SET extension_directory='{_extensionPath}'", cancellationToken).ConfigureAwait(false);
        }

        await ExecuteNonQueryAsync(connection, "INSTALL spatial", cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "LOAD spatial", cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteNonQueryAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
