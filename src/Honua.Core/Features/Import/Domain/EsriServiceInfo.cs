// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// Represents metadata about an ArcGIS Server service discovered from a remote URL.
/// </summary>
public sealed record EsriServiceInfo
{
    /// <summary>
    /// The URL of the ArcGIS service.
    /// </summary>
    public required string ServiceUrl { get; init; }

    /// <summary>
    /// The name of the service.
    /// </summary>
    public required string ServiceName { get; init; }

    /// <summary>
    /// Description of the service (if available).
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// The spatial reference WKID of the service.
    /// </summary>
    public int? SpatialReferenceWkid { get; init; }

    /// <summary>
    /// Maximum number of records the service returns per query.
    /// </summary>
    public int? MaxRecordCount { get; init; }

    /// <summary>
    /// Capabilities supported by the service (e.g., "Query", "Create", "Update", "Delete").
    /// </summary>
    public string[] Capabilities { get; init; } = [];

    /// <summary>
    /// Layers available in this service.
    /// </summary>
    public EsriLayerInfo[] Layers { get; init; } = [];

    /// <summary>
    /// The service version (e.g., "10.91", "11.0").
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Supported query formats.
    /// </summary>
    public string[] SupportedQueryFormats { get; init; } = [];
}

/// <summary>
/// Represents metadata about a single layer within an ArcGIS service.
/// </summary>
public sealed record EsriLayerInfo
{
    /// <summary>
    /// The layer ID within the service.
    /// </summary>
    public required int Id { get; init; }

    /// <summary>
    /// The name of the layer.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Description of the layer (if available).
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// The geometry type (esriGeometryPoint, esriGeometryPolyline, esriGeometryPolygon, etc.).
    /// </summary>
    public string? GeometryType { get; init; }

    /// <summary>
    /// The spatial reference WKID of the layer.
    /// </summary>
    public int? SpatialReferenceWkid { get; init; }

    /// <summary>
    /// Maximum number of records the layer returns per query.
    /// </summary>
    public int? MaxRecordCount { get; init; }

    /// <summary>
    /// Fields available in the layer.
    /// </summary>
    public EsriFieldInfo[] Fields { get; init; } = [];

    /// <summary>
    /// The type of layer (e.g., "Feature Layer", "Table").
    /// </summary>
    public string? Type { get; init; }

    /// <summary>
    /// Whether the layer supports attachments.
    /// </summary>
    public bool HasAttachments { get; init; }

    /// <summary>
    /// Minimum scale at which the layer is visible.
    /// </summary>
    public double? MinScale { get; init; }

    /// <summary>
    /// Maximum scale at which the layer is visible.
    /// </summary>
    public double? MaxScale { get; init; }

    /// <summary>
    /// The extent of the layer's data.
    /// </summary>
    public EsriExtent? Extent { get; init; }

    /// <summary>
    /// Estimated feature count (if available from service).
    /// </summary>
    public int? FeatureCount { get; init; }
}

/// <summary>
/// Represents a field definition from an ArcGIS layer.
/// </summary>
public sealed record EsriFieldInfo
{
    /// <summary>
    /// The field name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The Esri field type (e.g., "esriFieldTypeOID", "esriFieldTypeString", "esriFieldTypeDouble").
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Human-readable alias for the field.
    /// </summary>
    public string? Alias { get; init; }

    /// <summary>
    /// Maximum length for string fields.
    /// </summary>
    public int? Length { get; init; }

    /// <summary>
    /// Whether the field can contain null values.
    /// </summary>
    public bool Nullable { get; init; } = true;

    /// <summary>
    /// Whether this is the ObjectID field.
    /// </summary>
    public bool IsObjectId => Type.Equals("esriFieldTypeOID", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Represents the spatial extent from an ArcGIS service.
/// </summary>
public sealed record EsriExtent
{
    /// <summary>
    /// Minimum X coordinate.
    /// </summary>
    public double Xmin { get; init; }

    /// <summary>
    /// Minimum Y coordinate.
    /// </summary>
    public double Ymin { get; init; }

    /// <summary>
    /// Maximum X coordinate.
    /// </summary>
    public double Xmax { get; init; }

    /// <summary>
    /// Maximum Y coordinate.
    /// </summary>
    public double Ymax { get; init; }

    /// <summary>
    /// Spatial reference WKID.
    /// </summary>
    public int? SpatialReferenceWkid { get; init; }
}
