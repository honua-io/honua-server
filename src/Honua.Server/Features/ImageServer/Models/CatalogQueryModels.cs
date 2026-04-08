// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.ImageServer.Models;

/// <summary>
/// Esri-conformant response for the Image Server <c>query</c> endpoint.
/// Mirrors the FeatureServer query envelope so the Esri client SDK code paths converge.
/// </summary>
public sealed class CatalogQueryResponse
{
    /// <summary>
    /// Name of the OBJECTID field in the raster catalog.
    /// </summary>
    [JsonPropertyName("objectIdFieldName")]
    public string ObjectIdFieldName { get; init; } = "OBJECTID";

    /// <summary>
    /// Name of the global identifier field. Empty until raster catalog supports GUIDs.
    /// </summary>
    [JsonPropertyName("globalIdFieldName")]
    public string GlobalIdFieldName { get; init; } = string.Empty;

    /// <summary>
    /// Geometry type for raster catalog footprints.
    /// </summary>
    [JsonPropertyName("geometryType")]
    public string GeometryType { get; init; } = "esriGeometryPolygon";

    /// <summary>
    /// Spatial reference for the returned geometries.
    /// </summary>
    [JsonPropertyName("spatialReference")]
    public required SpatialReference SpatialReference { get; init; }

    /// <summary>
    /// Field schema describing the catalog attributes.
    /// </summary>
    [JsonPropertyName("fields")]
    public Field[] Fields { get; init; } = [];

    /// <summary>
    /// Returned features. Empty when the query had no matches.
    /// </summary>
    [JsonPropertyName("features")]
    public CatalogQueryFeature[] Features { get; init; } = [];

    /// <summary>
    /// Indicates the result set was truncated by <c>resultRecordCount</c>.
    /// </summary>
    [JsonPropertyName("exceededTransferLimit")]
    public bool ExceededTransferLimit { get; init; }
}

/// <summary>
/// Single raster catalog feature in a query response.
/// </summary>
public sealed class CatalogQueryFeature
{
    /// <summary>
    /// Per-item attributes (OBJECTID, Name, MinPS, ...).
    /// </summary>
    [JsonPropertyName("attributes")]
    public Dictionary<string, object?> Attributes { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Polygon footprint of the catalog item, when <c>returnGeometry</c> is true.
    /// </summary>
    [JsonPropertyName("geometry")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CatalogQueryGeometry? Geometry { get; init; }
}

/// <summary>
/// Esri ring-based polygon geometry for raster footprints.
/// </summary>
public sealed class CatalogQueryGeometry
{
    /// <summary>
    /// Polygon rings expressed as [[x,y],[x,y],...].
    /// </summary>
    [JsonPropertyName("rings")]
    public required double[][][] Rings { get; init; }

    /// <summary>
    /// Spatial reference of the rings.
    /// </summary>
    [JsonPropertyName("spatialReference")]
    public required SpatialReference SpatialReference { get; init; }
}

/// <summary>
/// Response for a query invoked with <c>returnIdsOnly=true</c>.
/// </summary>
public sealed class CatalogObjectIdsResponse
{
    /// <summary>
    /// Name of the OBJECTID field in the raster catalog.
    /// </summary>
    [JsonPropertyName("objectIdFieldName")]
    public string ObjectIdFieldName { get; init; } = "OBJECTID";

    /// <summary>
    /// OBJECTIDs that match the query, ordered ascending.
    /// </summary>
    [JsonPropertyName("objectIds")]
    public long[] ObjectIds { get; init; } = [];
}

/// <summary>
/// Response for a query invoked with <c>returnCountOnly=true</c>.
/// </summary>
public sealed class CatalogCountResponse
{
    /// <summary>
    /// Number of catalog items that match the query.
    /// </summary>
    [JsonPropertyName("count")]
    public long Count { get; init; }
}

/// <summary>
/// Response for a query invoked with <c>returnExtentOnly=true</c>.
/// </summary>
public sealed class CatalogExtentResponse
{
    /// <summary>
    /// Aggregate extent of the matching catalog items.
    /// </summary>
    [JsonPropertyName("extent")]
    public required ImageServerExtent Extent { get; init; }
}
