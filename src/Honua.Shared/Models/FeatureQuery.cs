// Copyright (c) 2026 Honua Project Contributors
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Collections.Immutable;
using NetTopologySuite.Geometries;

namespace Honua.Shared.Models;

/// <summary>
/// Domain model for feature queries.
/// Shared between server query processing and mobile client building.
/// </summary>
public class FeatureQuery
{
    /// <summary>
    /// SQL-like where clause for attribute filtering.
    /// </summary>
    public string? Where { get; set; }

    /// <summary>
    /// Specific object IDs to query for.
    /// </summary>
    public ImmutableArray<long>? ObjectIds { get; set; }

    /// <summary>
    /// Fields to include in the response. If null, all fields are returned.
    /// </summary>
    public ImmutableArray<string>? OutFields { get; set; }

    /// <summary>
    /// Whether to include geometry in the response.
    /// </summary>
    public bool ReturnGeometry { get; set; } = true;

    /// <summary>
    /// Spatial filter for geographic queries.
    /// </summary>
    public SpatialFilter? SpatialFilter { get; set; }

    /// <summary>
    /// Result offset for pagination.
    /// </summary>
    public int? Offset { get; set; }

    /// <summary>
    /// Maximum number of features to return.
    /// </summary>
    public int? Count { get; set; }

    /// <summary>
    /// Field name to order results by.
    /// </summary>
    public string? OrderBy { get; set; }

    /// <summary>
    /// Whether to return only distinct features.
    /// </summary>
    public bool ReturnDistinct { get; set; }

    /// <summary>
    /// Statistics to calculate on the query results.
    /// </summary>
    public ImmutableArray<StatisticDefinition>? Statistics { get; set; }

    /// <summary>
    /// Fields to group statistics by.
    /// </summary>
    public ImmutableArray<string>? GroupBy { get; set; }
}

/// <summary>
/// Spatial filtering criteria for feature queries.
/// </summary>
public class SpatialFilter
{
    /// <summary>
    /// Geometry to use for spatial filtering.
    /// </summary>
    public required Geometry FilterGeometry { get; set; }

    /// <summary>
    /// Type of spatial relationship to test.
    /// </summary>
    public SpatialRelationship Relationship { get; set; } = SpatialRelationship.Intersects;

    /// <summary>
    /// Buffer distance to apply to the filter geometry.
    /// </summary>
    public double? BufferDistance { get; set; }

    /// <summary>
    /// Units for the buffer distance.
    /// </summary>
    public DistanceUnit? BufferUnit { get; set; }

    /// <summary>
    /// Spatial reference system for the filter geometry.
    /// </summary>
    public SpatialReference? SpatialReference { get; set; }
}

/// <summary>
/// Statistical calculation definition.
/// </summary>
public class StatisticDefinition
{
    /// <summary>
    /// Field to calculate statistics on.
    /// </summary>
    public required string FieldName { get; set; }

    /// <summary>
    /// Type of statistic to calculate.
    /// </summary>
    public StatisticType StatisticType { get; set; }

    /// <summary>
    /// Output field name for the statistic result.
    /// </summary>
    public string? OutputFieldName { get; set; }
}

/// <summary>
/// Supported spatial relationships for filtering.
/// </summary>
public enum SpatialRelationship
{
    Intersects,
    Contains,
    Within,
    Crosses,
    Touches,
    Overlaps,
    Disjoint,
    Equals,
    EnvelopeIntersects,
    WithinDistance,
    BeyondDistance,
    NearestNeighbor
}

/// <summary>
/// Distance units for spatial operations.
/// </summary>
public enum DistanceUnit
{
    Meters,
    Feet,
    Kilometers,
    Miles
}

/// <summary>
/// Types of statistical calculations.
/// </summary>
public enum StatisticType
{
    Count,
    Sum,
    Min,
    Max,
    Average,
    StandardDeviation,
    Variance
}

/// <summary>
/// Spatial reference system definition.
/// </summary>
public class SpatialReference
{
    /// <summary>
    /// Well-Known ID (EPSG code).
    /// </summary>
    public int? WKID { get; set; }

    /// <summary>
    /// Latest Well-Known ID.
    /// </summary>
    public int? LatestWKID { get; set; }

    /// <summary>
    /// Well-Known Text definition.
    /// </summary>
    public string? WKT { get; set; }
}