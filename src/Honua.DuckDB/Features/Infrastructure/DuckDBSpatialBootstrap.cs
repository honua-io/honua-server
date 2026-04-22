// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using Honua.DuckDB;
using Microsoft.Extensions.Logging;

namespace Honua.DuckDB.Features.Infrastructure;

/// <summary>
/// Installs the DuckDB spatial extension once per process and loads it onto every
/// connection handed out by <see cref="DuckDBConnectionProvider"/>.
/// Supports an optional offline extension directory for air-gapped deployments.
/// </summary>
internal sealed class DuckDBSpatialBootstrap
{
    private readonly string? _extensionPath;
    private readonly ILogger<DuckDBSpatialBootstrap> _logger;
    private int _installed;

    public DuckDBSpatialBootstrap(string? extensionPath, ILogger<DuckDBSpatialBootstrap> logger)
    {
        _extensionPath = extensionPath;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Ensures the spatial extension is installed (once per process) and loaded on the
    /// given connection. <c>LOAD spatial</c> and <c>SET extension_directory</c> are
    /// connection-scoped in DuckDB, so they must run on every freshly opened connection
    /// or query execution will fail when spatial <c>ST_*</c> functions are referenced.
    /// </summary>
    public async Task EnsureSpatialExtensionAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        // SET extension_directory is connection-scoped — apply on every fresh connection
        // when an offline extension path is configured so LOAD can locate the artifact.
        if (!string.IsNullOrWhiteSpace(_extensionPath))
        {
            await ExecuteNonQueryAsync(connection, $"SET extension_directory='{_extensionPath}'", cancellationToken).ConfigureAwait(false);
        }

        // INSTALL persists the extension to the configured directory and only needs to
        // run once per process. Subsequent connections find it on disk via LOAD.
        if (Interlocked.CompareExchange(ref _installed, 1, 0) == 0)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_extensionPath))
                {
                    DuckDbLog.InstallingSpatialExtensionFromOfflinePath(_logger, _extensionPath);
                }

                await ExecuteNonQueryAsync(connection, "INSTALL spatial", cancellationToken).ConfigureAwait(false);
                DuckDbLog.SpatialExtensionInstalled(_logger);
            }
            catch
            {
                Volatile.Write(ref _installed, 0);
                throw;
            }
        }

        // LOAD is connection-scoped — must run on every new connection so spatial
        // ST_* functions are available for query execution on that connection.
        await ExecuteNonQueryAsync(connection, "LOAD spatial", cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteNonQueryAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
