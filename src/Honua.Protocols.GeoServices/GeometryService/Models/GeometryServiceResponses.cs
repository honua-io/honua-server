// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.GeometryService.Models;

/// <summary>
/// Standard response for geometry service operations.
/// </summary>
public sealed class GeometryServiceResponse
{
    /// <summary>
    /// The geometry type of the result geometries (e.g. esriGeometryPolygon).
    /// </summary>
    [JsonPropertyName("geometryType")]
    public string? GeometryType { get; init; }

    /// <summary>
    /// Spatial reference of the result geometries.
    /// </summary>
    [JsonPropertyName("spatialReference")]
    public GeometryServiceSpatialReference? SpatialReference { get; init; }

    /// <summary>
    /// Array of result geometries in GeoServices JSON format.
    /// </summary>
    [JsonPropertyName("geometries")]
    public JsonElement[]? Geometries { get; init; }
}

/// <summary>
/// Response payload for the <c>areasAndLengths</c> operation.
/// </summary>
public sealed class GeometryServiceAreasAndLengthsResponse
{
    /// <summary>
    /// Computed area values for the input geometries.
    /// </summary>
    [JsonPropertyName("areas")]
    public double[]? Areas { get; init; }

    /// <summary>
    /// Computed perimeter length values for the input geometries.
    /// </summary>
    [JsonPropertyName("lengths")]
    public double[]? Lengths { get; init; }
}

/// <summary>
/// Response payload for length operation.
/// </summary>
public sealed class GeometryServiceLengthResponse
{
    /// <summary>
    /// Computed length values for the input geometries.
    /// </summary>
    [JsonPropertyName("lengths")]
    public double[]? Lengths { get; init; }
}

/// <summary>
/// Response payload for the <c>distance</c> operation.
/// </summary>
public sealed class GeometryServiceDistanceResponse
{
    /// <summary>
    /// Distance between the two input geometries, expressed in the requested distance unit.
    /// </summary>
    [JsonPropertyName("distance")]
    public double Distance { get; init; }
}

/// <summary>
/// Response payload for the <c>relation</c> operation.
/// </summary>
public sealed class GeometryServiceRelationResponse
{
    /// <summary>
    /// Index pairs identifying which input geometries satisfy the requested spatial relation.
    /// </summary>
    [JsonPropertyName("relations")]
    public GeometryServiceRelationPair[]? Relations { get; init; }
}

/// <summary>
/// A single relation match identifying a geometry from each input set.
/// </summary>
public sealed class GeometryServiceRelationPair
{
    /// <summary>
    /// Zero-based index of the matching geometry in the first input set (<c>geometries1</c>).
    /// </summary>
    [JsonPropertyName("geometry1Index")]
    public int Geometry1Index { get; init; }

    /// <summary>
    /// Zero-based index of the matching geometry in the second input set (<c>geometries2</c>).
    /// </summary>
    [JsonPropertyName("geometry2Index")]
    public int Geometry2Index { get; init; }
}

/// <summary>
/// Response payload for operations that return a single geometry (for example <c>convexHull</c>).
/// </summary>
public sealed class GeometryServiceGeometryResponse
{
    /// <summary>
    /// Result geometry in GeoServices JSON format.
    /// </summary>
    [JsonPropertyName("geometry")]
    public JsonElement Geometry { get; init; }
}

/// <summary>
/// Error response for geometry service operations.
/// </summary>
public sealed class GeometryServiceErrorResponse
{
    /// <summary>
    /// Error details.
    /// </summary>
    [JsonPropertyName("error")]
    public required GeometryServiceError Error { get; init; }
}

/// <summary>
/// Spatial reference for geometry service responses.
/// </summary>
public sealed class GeometryServiceSpatialReference
{
    /// <summary>
    /// Well-Known ID (EPSG code).
    /// </summary>
    [JsonPropertyName("wkid")]
    public int Wkid { get; init; }

    /// <summary>
    /// Latest Well-Known ID.
    /// </summary>
    [JsonPropertyName("latestWkid")]
    public int LatestWkid { get; init; }
}

/// <summary>
/// Error details for geometry service operations.
/// </summary>
public sealed class GeometryServiceError
{
    /// <summary>
    /// Numeric error code.
    /// </summary>
    [JsonPropertyName("code")]
    public int Code { get; init; }

    /// <summary>
    /// Human-readable error message.
    /// </summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>
    /// Additional error details.
    /// </summary>
    [JsonPropertyName("details")]
    public string[]? Details { get; init; }
}
