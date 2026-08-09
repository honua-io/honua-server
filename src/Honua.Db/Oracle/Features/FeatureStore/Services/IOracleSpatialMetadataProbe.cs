// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Security.Domain;

namespace Honua.Oracle.Features.FeatureStore.Services;

/// <summary>
/// Reads Oracle catalog metadata used by <see cref="OracleSpatialGuard"/> to detect non-SDO and
/// ArcSDE-versioned surfaces. Abstracted so the guard's policy can be unit tested without
/// a live Oracle connection.
/// </summary>
internal interface IOracleSpatialMetadataProbe
{
    /// <summary>
    /// Reads the <c>DATA_TYPE</c> of the geometry column from <c>ALL_TAB_COLUMNS</c>.
    /// Returns <c>null</c> when the geometry column is not configured or no row is found.
    /// </summary>
    Task<string?> GetGeometryColumnTypeAsync(
        OracleLayerMapping mapping,
        DataConnection? dataConnection,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns any ArcSDE versioning columns present on the table
    /// (subset of <see cref="OracleSpatialGuard.ArcSdeVersioningColumns"/>).
    /// </summary>
    Task<IReadOnlyList<string>> GetArcSdeVersioningColumnsAsync(
        OracleLayerMapping mapping,
        DataConnection? dataConnection,
        CancellationToken cancellationToken);
}
