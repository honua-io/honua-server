// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.Migration.Domain;

/// <summary>
/// Represents metadata about an ArcGIS Server service discovered from a remote URL.
/// </summary>
public sealed record GeoservicesServiceInfo
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
    public GeoservicesLayerInfo[] Layers { get; init; } = [];

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
public sealed record GeoservicesLayerInfo
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
    public GeoservicesFieldInfo[] Fields { get; init; } = [];

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
/// Represents a field definition from an ArcGIS layer, extending shared field metadata.
/// </summary>
public sealed record GeoservicesFieldInfo : FieldDefinitionBase
{
    /// <summary>
    /// Whether this is the ObjectID field.
    /// </summary>
    public bool IsObjectId => Type.Equals("esriFieldTypeOID", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Determines if this field represents a string type based on Esri type
    /// </summary>
    protected override bool IsStringType()
    {
        return Type.Equals("esriFieldTypeString", StringComparison.OrdinalIgnoreCase);
    }
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
