// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Admin.Models;

namespace Honua.Server.Features.Admin.Services;

/// <summary>
/// Service for discovering spatial tables in a database
/// </summary>
public interface ITableDiscoveryService
{
    /// <summary>
    /// Discover all spatial tables in a PostGIS database
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of discovered tables with metadata</returns>
    Task<List<TableInfo>> DiscoverPostGisTablesAsync(
        string connectionString,
        CancellationToken cancellationToken = default);
}
