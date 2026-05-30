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
