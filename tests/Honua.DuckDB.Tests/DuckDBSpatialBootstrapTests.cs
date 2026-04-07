// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using DuckDB.NET.Data;
using Honua.DuckDB.Features.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.DuckDB.Tests;

/// <summary>
/// Verifies the spatial extension is loaded onto every fresh DuckDB connection.
/// LOAD is connection-scoped in DuckDB; if the bootstrap only loaded on the first
/// connection, subsequent connections would fail when query execution references
/// spatial ST_* functions.
/// </summary>
public sealed class DuckDBSpatialBootstrapTests
{
    [Fact]
    public async Task EnsureSpatialExtensionAsync_LoadsSpatialOnEveryConnection()
    {
        var bootstrap = new DuckDBSpatialBootstrap(
            extensionPath: null,
            logger: NullLogger<DuckDBSpatialBootstrap>.Instance);

        // First connection — INSTALL + LOAD.
        await using (var first = new DuckDBConnection("Data Source=:memory:"))
        {
            await first.OpenAsync();
            await bootstrap.EnsureSpatialExtensionAsync(first, CancellationToken.None);
            await AssertSpatialFunctionAvailableAsync(first);
        }

        // Second connection — different in-memory database, no shared session state.
        // The previous connection's LOAD does not transfer; the bootstrap must LOAD
        // again or ST_Point will fail.
        await using (var second = new DuckDBConnection("Data Source=:memory:"))
        {
            await second.OpenAsync();
            await bootstrap.EnsureSpatialExtensionAsync(second, CancellationToken.None);
            await AssertSpatialFunctionAvailableAsync(second);
        }
    }

    private static async Task AssertSpatialFunctionAvailableAsync(DuckDBConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ST_AsText(ST_Point(1.0, 2.0))";
        var result = await cmd.ExecuteScalarAsync();
        Assert.NotNull(result);
        Assert.Contains("POINT", result!.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
