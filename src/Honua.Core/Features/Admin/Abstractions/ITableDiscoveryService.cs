// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Admin.Domain;

namespace Honua.Core.Features.Admin.Abstractions;

/// <summary>
/// Service for discovering spatial tables in a database.
/// </summary>
public interface ITableDiscoveryService
{
    /// <summary>
    /// Discover all spatial tables in a PostGIS database.
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of discovered tables with metadata.</returns>
    Task<List<TableInfo>> DiscoverPostGisTablesAsync(
        string connectionString,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Discover all spatial tables in a PostGIS database using an existing connection.
    /// </summary>
    /// <param name="connection">Open database connection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of discovered tables with metadata.</returns>
    Task<List<TableInfo>> DiscoverPostGisTablesAsync(
        DbConnection connection,
        CancellationToken cancellationToken = default);
    /// <summary>
    /// Discover an attribute-only relational table using the provider's publication exclusions.
    /// Providers that do not support nonspatial discovery return null.
    /// </summary>
    /// <param name="connection">Open database connection.</param>
    /// <param name="schema">Requested table schema.</param>
    /// <param name="table">Requested table name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The discovered table, or null when unavailable or excluded.</returns>
    Task<TableInfo?> DiscoverNonSpatialTableAsync(
        DbConnection connection,
        string schema,
        string table,
        CancellationToken cancellationToken = default)
        => Task.FromResult<TableInfo?>(null);
}
