// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Sdk.Grpc.Models;

/// <summary>
/// Request parameters for a feature query.
/// </summary>
public sealed class QueryFeaturesRequest
{
    /// <summary>Service identifier.</summary>
    public string ServiceId { get; set; } = string.Empty;

    /// <summary>Layer index within the service.</summary>
    public int LayerId { get; set; }

    /// <summary>SQL-like where clause.</summary>
    public string Where { get; set; } = "1=1";

    /// <summary>Specific object IDs to return.</summary>
    public IReadOnlyList<long>? ObjectIds { get; set; }

    /// <summary>Fields to include in results.</summary>
    public IReadOnlyList<string>? OutFields { get; set; }

    /// <summary>Whether to return geometry.</summary>
    public bool ReturnGeometry { get; set; } = true;

    /// <summary>Output spatial reference.</summary>
    public SpatialReference? OutSr { get; set; }

    /// <summary>Result offset for pagination.</summary>
    public int ResultOffset { get; set; }

    /// <summary>Maximum number of records to return.</summary>
    public int ResultRecordCount { get; set; }

    /// <summary>Order by clause.</summary>
    public string OrderBy { get; set; } = string.Empty;

    /// <summary>Return distinct values only.</summary>
    public bool ReturnDistinct { get; set; }

    /// <summary>Return only the count of matching features.</summary>
    public bool ReturnCountOnly { get; set; }

    /// <summary>Return only object IDs.</summary>
    public bool ReturnIdsOnly { get; set; }

    /// <summary>Return only the extent.</summary>
    public bool ReturnExtentOnly { get; set; }

    /// <summary>Statistics to compute.</summary>
    public IReadOnlyList<StatisticDefinition>? OutStatistics { get; set; }

    /// <summary>Fields to group by.</summary>
    public IReadOnlyList<string>? GroupBy { get; set; }

    /// <summary>Geometry coordinate precision.</summary>
    public int GeometryPrecision { get; set; }

    /// <summary>Maximum allowable offset for geometry generalization.</summary>
    public double MaxAllowableOffset { get; set; }
}
