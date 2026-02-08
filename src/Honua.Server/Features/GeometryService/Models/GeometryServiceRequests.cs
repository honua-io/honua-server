// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.GeometryService.Models;

/// <summary>
/// Request body for the buffer operation.
/// </summary>
public sealed class BufferRequest
{
    /// <summary>
    /// Array of GeoServices JSON geometries to buffer.
    /// </summary>
    [JsonPropertyName("geometries")]
    public JsonElement[]? Geometries { get; init; }

    /// <summary>
    /// Input spatial reference (WKID).
    /// </summary>
    [JsonPropertyName("inSR")]
    public int InSR { get; init; }

    /// <summary>
    /// Output spatial reference (WKID). Defaults to inSR if not specified.
    /// </summary>
    [JsonPropertyName("outSR")]
    public int? OutSR { get; init; }

    /// <summary>
    /// Buffer distances. If a single value is provided, it is applied to all geometries.
    /// If multiple values are provided, each is applied to the corresponding geometry.
    /// </summary>
    [JsonPropertyName("distances")]
    public double[]? Distances { get; init; }

    /// <summary>
    /// Unit of the buffer distance (e.g. esriMeters, esriFeet, esriKilometers).
    /// </summary>
    [JsonPropertyName("unit")]
    public string? Unit { get; init; }

    /// <summary>
    /// When true, all buffered geometries are unioned into a single result geometry.
    /// </summary>
    [JsonPropertyName("unionResults")]
    public bool UnionResults { get; init; }

    /// <summary>
    /// When true, geodesic (geography-based) buffering is used.
    /// </summary>
    [JsonPropertyName("geodesic")]
    public bool Geodesic { get; init; }
}

/// <summary>
/// Request body for the simplify operation.
/// </summary>
public sealed class SimplifyRequest
{
    /// <summary>
    /// Array of GeoServices JSON geometries to simplify.
    /// </summary>
    [JsonPropertyName("geometries")]
    public JsonElement[]? Geometries { get; init; }

    /// <summary>
    /// Input spatial reference (WKID).
    /// </summary>
    [JsonPropertyName("inSR")]
    public int InSR { get; init; }

    /// <summary>
    /// Output spatial reference (WKID). Defaults to inSR if not specified.
    /// </summary>
    [JsonPropertyName("outSR")]
    public int? OutSR { get; init; }

    /// <summary>
    /// Maximum allowable offset (tolerance) for simplification.
    /// </summary>
    [JsonPropertyName("maxDeviation")]
    public double MaxDeviation { get; init; }

    /// <summary>
    /// Unit of the deviation (e.g. esriMeters, esriFeet). Defaults to the unit of inSR.
    /// </summary>
    [JsonPropertyName("deviationUnit")]
    public string? DeviationUnit { get; init; }
}

/// <summary>
/// Request body for the project operation.
/// </summary>
public sealed class ProjectRequest
{
    /// <summary>
    /// Array of GeoServices JSON geometries to reproject.
    /// </summary>
    [JsonPropertyName("geometries")]
    public JsonElement[]? Geometries { get; init; }

    /// <summary>
    /// Input spatial reference (WKID).
    /// </summary>
    [JsonPropertyName("inSR")]
    public int InSR { get; init; }

    /// <summary>
    /// Output spatial reference (WKID).
    /// </summary>
    [JsonPropertyName("outSR")]
    public int OutSR { get; init; }
}
