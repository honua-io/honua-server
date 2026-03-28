// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Defines an H3 hexagonal grid aggregation query that groups features into
/// H3 cells at a given resolution and computes aggregate statistics per cell.
/// </summary>
public readonly record struct H3AggregationQuery
{
    /// <summary>
    /// H3 resolution level (0–15). Higher resolutions produce smaller cells.
    /// </summary>
    public required int Resolution { get; init; }

    /// <summary>
    /// Optional polyfill geometry in WKB format. When provided, only H3 cells
    /// that cover this polygon are included in the result.
    /// </summary>
    public byte[]? PolyfillGeometry { get; init; }

    /// <summary>
    /// SRID of the polyfill geometry (null if unspecified, defaults to layer SRID).
    /// </summary>
    public int? PolyfillSrid { get; init; }

    /// <summary>
    /// Optional k-ring (grid-disk) expansion distance. When greater than zero,
    /// each result cell is expanded to include its neighbors within this distance.
    /// </summary>
    public int? KRingDistance { get; init; }

    /// <summary>
    /// Aggregate statistics to compute per H3 cell. When empty, only the feature
    /// count per cell is returned.
    /// </summary>
    public ImmutableArray<StatisticDefinition>? OutStatistics { get; init; }

    /// <summary>
    /// Maximum number of H3 cells returned by the aggregation query.
    /// When greater than zero, a LIMIT is applied after GROUP BY to prevent
    /// unbounded result sets at high resolutions. Null or zero disables the limit.
    /// </summary>
    public int? MaxCells { get; init; }

    /// <summary>
    /// Minimum H3 resolution (inclusive).
    /// </summary>
    public const int MinResolution = 0;

    /// <summary>
    /// Maximum H3 resolution (inclusive).
    /// </summary>
    public const int MaxResolution = 15;

    /// <summary>
    /// Maximum k-ring expansion distance. Limits resource usage since
    /// h3_grid_disk generates approximately 3k²+3k+1 cells per input cell.
    /// </summary>
    public const int MaxKRingDistance = 20;

    /// <summary>
    /// Operator-facing error title when H3 operations are requested but the
    /// h3-pg extension is not installed.
    /// </summary>
    public const string CapabilityErrorTitle = "H3 operations are not available";

    /// <summary>
    /// Operator-facing detail message when h3-pg is missing.
    /// </summary>
    public const string CapabilityErrorDetail =
        "The h3-pg PostgreSQL extension is not installed. Contact your database administrator to install it: https://github.com/zachasme/h3-pg";

    /// <summary>
    /// Operator-facing detail message when the H3 capability check fails transiently.
    /// </summary>
    public const string CapabilityCheckFailedDetail =
        "Unable to determine h3-pg extension availability. The database may be temporarily unreachable. Please retry the request.";
}
