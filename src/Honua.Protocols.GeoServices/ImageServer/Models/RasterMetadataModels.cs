// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.ImageServer.Models;

/// <summary>
/// Esri-conformant response for the Image Server <c>statistics</c> child resource.
/// Returns one entry per raster band describing the band's pixel-value distribution.
/// </summary>
public sealed class StatisticsResourceResponse
{
    /// <summary>
    /// Per-band statistics for the layer's primary raster (or resolved mosaic).
    /// </summary>
    [JsonPropertyName("statistics")]
    public StatisticsEntry[] Statistics { get; init; } = [];
}

/// <summary>
/// Single band entry in the Esri <c>statistics</c> resource document.
/// </summary>
public sealed class StatisticsEntry
{
    /// <summary>Minimum pixel value found in the band.</summary>
    [JsonPropertyName("min")]
    public double Min { get; init; }

    /// <summary>Maximum pixel value found in the band.</summary>
    [JsonPropertyName("max")]
    public double Max { get; init; }

    /// <summary>Mean pixel value of the band.</summary>
    [JsonPropertyName("mean")]
    public double Mean { get; init; }

    /// <summary>Standard deviation of the band's pixel values.</summary>
    [JsonPropertyName("standardDeviation")]
    public double StandardDeviation { get; init; }

    /// <summary>Count of valid (non-NoData) pixels.</summary>
    [JsonPropertyName("count")]
    public long Count { get; init; }
}

/// <summary>
/// Esri-conformant response for the Image Server <c>histograms</c> child resource.
/// </summary>
public sealed class HistogramsResourceResponse
{
    /// <summary>
    /// Per-band histograms for the layer's primary raster (or resolved mosaic).
    /// </summary>
    [JsonPropertyName("histograms")]
    public BandHistogram[] Histograms { get; init; } = [];
}

/// <summary>
/// Esri-conformant response for the Image Server <c>rasterFunctionInfos</c> child resource.
/// Lists the raster functions the service can apply through <c>renderingRule</c>.
/// </summary>
public sealed class RasterFunctionInfosResponse
{
    /// <summary>
    /// Raster function descriptors advertised by the service.
    /// </summary>
    [JsonPropertyName("rasterFunctionInfos")]
    public RasterFunctionInfoEntry[] RasterFunctionInfos { get; init; } = [];
}

/// <summary>
/// Single descriptor in the Esri <c>rasterFunctionInfos</c> document.
/// </summary>
public sealed class RasterFunctionInfoEntry
{
    /// <summary>Raster function name (e.g. <c>None</c>, <c>Stretch</c>).</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Human-readable description of the function.</summary>
    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    /// <summary>Help text for the function.</summary>
    [JsonPropertyName("help")]
    public string Help { get; init; } = string.Empty;
}

/// <summary>
/// Esri-conformant response for the Image Server <c>rasterAttributeTable</c> child
/// resource. The table is shaped as an Esri feature set (<c>fields</c> + <c>features</c>).
/// Continuous (non-thematic) rasters have no value/attribute table, so Honua returns the
/// canonical column schema with an empty <c>features</c> array rather than a 404.
/// </summary>
public sealed class RasterAttributeTableResponse
{
    /// <summary>Object id field name for the attribute table.</summary>
    [JsonPropertyName("objectIdFieldName")]
    public string ObjectIdFieldName { get; init; } = "OBJECTID";

    /// <summary>Column schema for the attribute table.</summary>
    [JsonPropertyName("fields")]
    public RasterAttributeTableField[] Fields { get; init; } = [];

    /// <summary>Attribute rows; empty when the raster carries no value table.</summary>
    [JsonPropertyName("features")]
    public RasterAttributeTableFeature[] Features { get; init; } = [];
}

/// <summary>
/// Field descriptor in the Esri <c>rasterAttributeTable</c> document.
/// </summary>
public sealed class RasterAttributeTableField
{
    /// <summary>Field name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Esri field type (e.g. <c>esriFieldTypeOID</c>, <c>esriFieldTypeInteger</c>).</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    /// <summary>Display alias for the field.</summary>
    [JsonPropertyName("alias")]
    public string Alias { get; init; } = string.Empty;
}

/// <summary>
/// Attribute row in the Esri <c>rasterAttributeTable</c> document.
/// </summary>
public sealed class RasterAttributeTableFeature
{
    /// <summary>Attribute name/value pairs for the row.</summary>
    [JsonPropertyName("attributes")]
    public Dictionary<string, object?> Attributes { get; init; } = new();
}
