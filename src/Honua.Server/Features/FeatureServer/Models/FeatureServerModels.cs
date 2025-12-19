// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.FeatureServer.Models;

/// <summary>
/// Response model for FeatureServer service metadata endpoint
/// </summary>
public sealed class FeatureServerResponse
{
    /// <summary>
    /// Current version of the service
    /// </summary>
    public string CurrentVersion { get; init; } = "10.81";

    /// <summary>
    /// Name of the service
    /// </summary>
    public required string ServiceName { get; init; }

    /// <summary>
    /// Human-readable description of the service
    /// </summary>
    public required string ServiceDescription { get; init; }

    /// <summary>
    /// Layers available in this service
    /// </summary>
    public required LayerInfo[] Layers { get; init; }

    /// <summary>
    /// Tables available in this service (typically empty for basic implementation)
    /// </summary>
    public object[] Tables { get; init; } = Array.Empty<object>();

    /// <summary>
    /// Default spatial reference system for the service
    /// </summary>
    public required SpatialReferenceInfo SpatialReference { get; init; }

    /// <summary>
    /// Initial extent of the service
    /// </summary>
    public ExtentInfo? InitialExtent { get; init; }

    /// <summary>
    /// Full extent of all data in the service
    /// </summary>
    public ExtentInfo? FullExtent { get; init; }

    /// <summary>
    /// Units used for measurements
    /// </summary>
    public string Units { get; init; } = "esriMeters";

    /// <summary>
    /// Supported query formats
    /// </summary>
    public string[] SupportedQueryFormats { get; init; } = new[] { "JSON", "GeoJSON" };

    /// <summary>
    /// Service capabilities
    /// </summary>
    public string Capabilities { get; init; } = "Query,Extract";

    /// <summary>
    /// Maximum number of records returned in a single query
    /// </summary>
    public int MaxRecordCount { get; init; } = 1000;

    /// <summary>
    /// Whether the service supports advanced queries
    /// </summary>
    public bool SupportsAdvancedQueries { get; init; } = true;

    /// <summary>
    /// Whether the service supports statistics queries
    /// </summary>
    public bool SupportsStatistics { get; init; } = true;

    /// <summary>
    /// Whether the service supports spatial queries
    /// </summary>
    public bool HasGeometryProperties { get; init; } = true;

    /// <summary>
    /// Object ID field name used across the service
    /// </summary>
    public string ObjectIdField { get; init; } = "objectid";

    /// <summary>
    /// Global ID field name (if used)
    /// </summary>
    public string? GlobalIdField { get; init; }

    /// <summary>
    /// Type ID field name (if used for symbology)
    /// </summary>
    public string? TypeIdField { get; init; }

    /// <summary>
    /// Fields common across all layers
    /// </summary>
    public EsriFieldInfo[] Fields { get; init; } = Array.Empty<EsriFieldInfo>();

    /// <summary>
    /// Relationships between layers (typically empty for basic implementation)
    /// </summary>
    public object[] Relationships { get; init; } = Array.Empty<object>();
}

/// <summary>
/// Response model for individual layer metadata endpoint
/// </summary>
public sealed class LayerResponse
{
    /// <summary>
    /// Current version of the service
    /// </summary>
    public string CurrentVersion { get; init; } = "10.81";

    /// <summary>
    /// Layer identifier
    /// </summary>
    public required int Id { get; init; }

    /// <summary>
    /// Layer name
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Layer type (always "Feature Layer" for feature layers)
    /// </summary>
    public string Type { get; init; } = "Feature Layer";

    /// <summary>
    /// Human-readable description
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Geometry type of features in this layer
    /// </summary>
    public required string GeometryType { get; init; }

    /// <summary>
    /// Layer's spatial reference system
    /// </summary>
    public required SpatialReferenceInfo SpatialReference { get; init; }

    /// <summary>
    /// Field definitions for the layer
    /// </summary>
    public required EsriFieldInfo[] Fields { get; init; }

    /// <summary>
    /// Layer extent
    /// </summary>
    public ExtentInfo? Extent { get; init; }

    /// <summary>
    /// Minimum scale for layer visibility
    /// </summary>
    public double? MinScale { get; init; }

    /// <summary>
    /// Maximum scale for layer visibility
    /// </summary>
    public double? MaxScale { get; init; }

    /// <summary>
    /// Default visibility state
    /// </summary>
    public bool DefaultVisibility { get; init; } = true;

    /// <summary>
    /// Layer capabilities
    /// </summary>
    public string Capabilities { get; init; } = "Query,Extract";

    /// <summary>
    /// Maximum number of records in a single query
    /// </summary>
    public int MaxRecordCount { get; init; } = 1000;

    /// <summary>
    /// Whether the layer supports advanced queries
    /// </summary>
    public bool SupportsAdvancedQueries { get; init; } = true;

    /// <summary>
    /// Whether the layer supports statistics
    /// </summary>
    public bool SupportsStatistics { get; init; } = true;

    /// <summary>
    /// Whether the layer can return count only
    /// </summary>
    public bool SupportsCountDistinct { get; init; } = true;

    /// <summary>
    /// Whether the layer supports ordering by fields
    /// </summary>
    public bool SupportsOrderBy { get; init; } = true;

    /// <summary>
    /// Whether the layer supports distinct values
    /// </summary>
    public bool SupportsDistinct { get; init; } = true;

    /// <summary>
    /// Whether the layer supports pagination
    /// </summary>
    public bool SupportsPagination { get; init; } = true;

    /// <summary>
    /// Whether the layer supports TrueCurve geometries
    /// </summary>
    public bool SupportsTrueCurve { get; init; }

    /// <summary>
    /// Object ID field name
    /// </summary>
    public required string ObjectIdField { get; init; }

    /// <summary>
    /// Global ID field name (if available)
    /// </summary>
    public string? GlobalIdField { get; init; }

    /// <summary>
    /// Display field name (primary field for display)
    /// </summary>
    public string? DisplayField { get; init; }

    /// <summary>
    /// Type ID field name (if used for symbology)
    /// </summary>
    public string? TypeIdField { get; init; }

    /// <summary>
    /// Field used for type definitions
    /// </summary>
    public object[]? Types { get; init; }

    /// <summary>
    /// Relationships to other layers
    /// </summary>
    public object[] Relationships { get; init; } = Array.Empty<object>();

    /// <summary>
    /// Whether the layer has static data
    /// </summary>
    public bool IsDataVersioned { get; init; }

    /// <summary>
    /// Whether time is enabled for the layer
    /// </summary>
    public bool? SupportsRollbackOnFailureParameter { get; init; }

    /// <summary>
    /// Archive information (for versioned data)
    /// </summary>
    public object? ArchivingInfo { get; init; }

    /// <summary>
    /// Whether the layer supports applying edits
    /// </summary>
    public bool SupportsApplyEditsWithGlobalIds { get; init; }

    /// <summary>
    /// Drawing information for the layer
    /// </summary>
    public object? DrawingInfo { get; init; }

    /// <summary>
    /// Whether layer has attachments
    /// </summary>
    public bool HasAttachments { get; init; }

    /// <summary>
    /// HTML popup information
    /// </summary>
    public object? PopupInfo { get; init; }

    /// <summary>
    /// Whether layer supports querying for related records
    /// </summary>
    public bool SupportsQueryRelated { get; init; }

    /// <summary>
    /// Supported query formats
    /// </summary>
    public string[] SupportedQueryFormats { get; init; } = new[] { "JSON", "GeoJSON" };

    /// <summary>
    /// Layer ownership information
    /// </summary>
    public object? OwnershipBasedAccessControlForFeatures { get; init; }

    /// <summary>
    /// Whether the layer uses standardized queries
    /// </summary>
    public bool UseStandardizedQueries { get; init; } = true;

    /// <summary>
    /// Whether the layer supports spatial queries
    /// </summary>
    public bool SupportsCoordinatesQuantization { get; init; } = true;
}

/// <summary>
/// Basic layer info for service listing
/// </summary>
public sealed class LayerInfo
{
    /// <summary>
    /// Layer identifier
    /// </summary>
    public required int Id { get; init; }

    /// <summary>
    /// Layer name
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Parent layer ID (for group layers)
    /// </summary>
    public int? ParentLayerId { get; init; }

    /// <summary>
    /// Default visibility state
    /// </summary>
    public bool DefaultVisibility { get; init; } = true;

    /// <summary>
    /// Sub-layer IDs (for group layers)
    /// </summary>
    public int[]? SubLayerIds { get; init; }

    /// <summary>
    /// Minimum scale for visibility
    /// </summary>
    public double? MinScale { get; init; }

    /// <summary>
    /// Maximum scale for visibility
    /// </summary>
    public double? MaxScale { get; init; }

    /// <summary>
    /// Layer type
    /// </summary>
    public string Type { get; init; } = "Feature Layer";

    /// <summary>
    /// Geometry type
    /// </summary>
    public required string GeometryType { get; init; }
}

/// <summary>
/// Spatial reference information in Esri format
/// </summary>
public sealed class SpatialReferenceInfo
{
    /// <summary>
    /// Well-Known ID (EPSG code)
    /// </summary>
    public required int Wkid { get; init; }

    /// <summary>
    /// Latest Well-Known ID (for newer EPSG codes)
    /// </summary>
    public int? LatestWkid { get; init; }

    /// <summary>
    /// Vertical coordinate system WKID
    /// </summary>
    public int? VcsWkid { get; init; }

    /// <summary>
    /// Latest vertical coordinate system WKID
    /// </summary>
    public int? LatestVcsWkid { get; init; }

    /// <summary>
    /// Well-Known Text representation
    /// </summary>
    public string? Wkt { get; init; }
}

/// <summary>
/// Spatial extent information
/// </summary>
public sealed class ExtentInfo
{
    /// <summary>
    /// Minimum X coordinate
    /// </summary>
    public required double Xmin { get; init; }

    /// <summary>
    /// Minimum Y coordinate
    /// </summary>
    public required double Ymin { get; init; }

    /// <summary>
    /// Maximum X coordinate
    /// </summary>
    public required double Xmax { get; init; }

    /// <summary>
    /// Maximum Y coordinate
    /// </summary>
    public required double Ymax { get; init; }

    /// <summary>
    /// Spatial reference for the extent
    /// </summary>
    public required SpatialReferenceInfo SpatialReference { get; init; }
}

/// <summary>
/// Field definition in Esri format
/// </summary>
public sealed class EsriFieldInfo
{
    /// <summary>
    /// Field name
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Field type in Esri format
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Field alias (display name)
    /// </summary>
    public required string Alias { get; init; }

    /// <summary>
    /// SQL type name
    /// </summary>
    public string? SqlType { get; init; }

    /// <summary>
    /// Field domain (for coded values)
    /// </summary>
    public object? Domain { get; init; }

    /// <summary>
    /// Default value
    /// </summary>
    public object? DefaultValue { get; init; }

    /// <summary>
    /// Field length (for string types)
    /// </summary>
    public int? Length { get; init; }

    /// <summary>
    /// Whether the field is nullable
    /// </summary>
    public bool Nullable { get; init; } = true;

    /// <summary>
    /// Whether the field is editable
    /// </summary>
    public bool Editable { get; init; } = true;

    /// <summary>
    /// Whether the field is visible
    /// </summary>
    public bool Visible { get; init; } = true;

    /// <summary>
    /// Whether the field can be used for display
    /// </summary>
    public bool CanSort { get; init; } = true;
}

/// <summary>
/// Query response for FeatureServer query endpoint
/// </summary>
public sealed class QueryResponse
{
    /// <summary>
    /// Object ID field name for the layer
    /// </summary>
    public string ObjectIdFieldName { get; init; } = "objectid";

    /// <summary>
    /// Unique value field name (optional)
    /// </summary>
    public string? UniqueIdField { get; init; }

    /// <summary>
    /// Global ID field name (optional)
    /// </summary>
    public string? GlobalIdFieldName { get; init; }

    /// <summary>
    /// Features returned by the query
    /// </summary>
    public EsriFeature[] Features { get; init; } = Array.Empty<EsriFeature>();

    /// <summary>
    /// Whether the transfer limit was exceeded
    /// </summary>
    public bool ExceededTransferLimit { get; init; }
}

/// <summary>
/// Esri feature representation
/// </summary>
public sealed class EsriFeature
{
    /// <summary>
    /// Feature attributes as key-value pairs
    /// </summary>
    public required Dictionary<string, object?> Attributes { get; init; }

    /// <summary>
    /// Feature geometry (optional if returnGeometry=false)
    /// </summary>
    public object? Geometry { get; init; }
}

/// <summary>
/// Query parameters for feature queries
/// </summary>
public sealed class QueryParameters
{
    /// <summary>
    /// WHERE clause for attribute queries
    /// </summary>
    public string? Where { get; init; }

    /// <summary>
    /// Fields to return (comma-separated list)
    /// </summary>
    public string? OutFields { get; init; }

    /// <summary>
    /// Whether to return geometry
    /// </summary>
    public bool ReturnGeometry { get; init; } = true;

    /// <summary>
    /// Output format (json, geojson)
    /// </summary>
    public string F { get; init; } = "json";

    /// <summary>
    /// Number of records to offset for pagination
    /// </summary>
    public int? ResultOffset { get; init; }

    /// <summary>
    /// Maximum number of records to return
    /// </summary>
    public int? ResultRecordCount { get; init; }
}

/// <summary>
/// JSON source generation context for FeatureServer models (AOT compatibility)
/// </summary>
[JsonSerializable(typeof(FeatureServerResponse))]
[JsonSerializable(typeof(LayerResponse))]
[JsonSerializable(typeof(LayerInfo))]
[JsonSerializable(typeof(SpatialReferenceInfo))]
[JsonSerializable(typeof(ExtentInfo))]
[JsonSerializable(typeof(EsriFieldInfo))]
[JsonSerializable(typeof(QueryResponse))]
[JsonSerializable(typeof(EsriFeature))]
[JsonSerializable(typeof(QueryParameters))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class FeatureServerJsonContext : JsonSerializerContext
{
}
