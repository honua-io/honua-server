// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.ImageServer.Models;

/// <summary>
/// Esri-conformant response for the Image Server <c>computeHistograms</c> endpoint.
/// </summary>
public sealed class ComputeHistogramsResponse
{
    /// <summary>
    /// Per-band histograms describing the analysed area (whole layer or AOI).
    /// </summary>
    [JsonPropertyName("histograms")]
    public BandHistogram[] Histograms { get; init; } = [];
}

/// <summary>
/// Esri-conformant response for the Image Server <c>getSamples</c> endpoint.
/// </summary>
public sealed class GetSamplesResponse
{
    /// <summary>
    /// Sampled pixel values at the requested points / along the requested geometry.
    /// </summary>
    [JsonPropertyName("samples")]
    public SampleEntry[] Samples { get; init; } = [];
}

/// <summary>
/// A single sampled pixel value at a geographic location, shaped per the Esri spec.
/// </summary>
public sealed class SampleEntry
{
    /// <summary>
    /// Catalog object id of the raster that produced the sample.
    /// </summary>
    [JsonPropertyName("rasterId")]
    public long? RasterId { get; init; }

    /// <summary>
    /// Sample location in the response spatial reference.
    /// </summary>
    [JsonPropertyName("location")]
    public required SampleLocation Location { get; init; }

    /// <summary>
    /// Space-separated band values at the sample location, or <c>NoData</c>.
    /// </summary>
    [JsonPropertyName("value")]
    public string? Value { get; init; }

    /// <summary>
    /// Pixel resolution at the sample location, when known.
    /// </summary>
    [JsonPropertyName("resolution")]
    public double? Resolution { get; init; }

    /// <summary>
    /// Per-band sample attributes (band index → value).
    /// </summary>
    [JsonPropertyName("attributes")]
    public Dictionary<string, object?>? Attributes { get; init; }
}

/// <summary>
/// Sample location with its spatial reference.
/// </summary>
public sealed class SampleLocation
{
    /// <summary>
    /// X coordinate of the sample location.
    /// </summary>
    [JsonPropertyName("x")]
    public required double X { get; init; }

    /// <summary>
    /// Y coordinate of the sample location.
    /// </summary>
    [JsonPropertyName("y")]
    public required double Y { get; init; }

    /// <summary>
    /// Spatial reference of the location.
    /// </summary>
    [JsonPropertyName("spatialReference")]
    public SpatialReference? SpatialReference { get; init; }
}

/// <summary>
/// Esri-conformant response for the Image Server <c>keyProperties</c> endpoint.
/// </summary>
public sealed class KeyPropertiesResponse
{
    /// <summary>
    /// Per-band properties describing the primary raster.
    /// </summary>
    [JsonPropertyName("BandProperties")]
    public BandProperty[] BandProperties { get; init; } = [];

    /// <summary>
    /// Esri pixel data type of the raster (e.g. <c>U8</c>, <c>F32</c>).
    /// </summary>
    [JsonPropertyName("DataType")]
    public string? DataType { get; init; }

    /// <summary>
    /// Number of bands in the raster.
    /// </summary>
    [JsonPropertyName("BandCount")]
    public int BandCount { get; init; }

    /// <summary>
    /// NoData value of the raster, when declared by the source.
    /// </summary>
    [JsonPropertyName("NoDataValue")]
    public double? NoDataValue { get; init; }
}

/// <summary>
/// Per-band key properties of a raster.
/// </summary>
public sealed class BandProperty
{
    /// <summary>
    /// 1-based band index.
    /// </summary>
    [JsonPropertyName("BandName")]
    public required string BandName { get; init; }

    /// <summary>
    /// Source pixel type for the band (PostGIS representation, e.g. <c>8BUI</c>).
    /// </summary>
    [JsonPropertyName("PixelType")]
    public string? PixelType { get; init; }
}
