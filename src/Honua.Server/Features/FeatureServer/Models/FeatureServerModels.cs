// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Server.Features.Infrastructure.Models;
// using Honua.Server.Features.OData.Models; // Temporarily disabled for Issue 46 performance testing

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
    public object[] Tables { get; init; } = [];

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
    public string[] SupportedQueryFormats { get; init; } = ["JSON", "GeoJSON"];

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
    public GeoServicesFieldInfo[] Fields { get; init; } = [];

    /// <summary>
    /// Relationships between layers (typically empty for basic implementation)
    /// </summary>
    public object[] Relationships { get; init; } = [];
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
    public required GeoServicesFieldInfo[] Fields { get; init; }

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
    public object[] Relationships { get; init; } = [];

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
    public string[] SupportedQueryFormats { get; init; } = ["JSON", "GeoJSON"];

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
/// Spatial reference information in GeoServices format
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
/// Field definition in GeoServices format
/// </summary>
public sealed class GeoServicesFieldInfo
{
    /// <summary>
    /// Field name
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Field type in GeoServices format
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
    /// Geometry type for the features returned by the query
    /// </summary>
    public string? GeometryType { get; init; }

    /// <summary>
    /// Spatial reference for returned geometries
    /// </summary>
    public GeoServicesSpatialReference? SpatialReference { get; init; }

    /// <summary>
    /// Object ID field name for the layer
    /// </summary>
    public string ObjectIdFieldName { get; init; } = "objectid";

    /// <summary>
    /// Object IDs returned by the query (when returnIdsOnly=true)
    /// </summary>
    public long[]? ObjectIds { get; init; }

    /// <summary>
    /// Total count returned by the query (when returnCountOnly=true)
    /// </summary>
    public long? Count { get; init; }

    /// <summary>
    /// Extent returned by the query (when returnExtentOnly=true)
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public ExtentInfo? Extent { get; init; }

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
    public GeoServicesFeature[] Features { get; init; } = [];

    /// <summary>
    /// Whether the transfer limit was exceeded
    /// </summary>
    public bool ExceededTransferLimit { get; init; }
}

/// <summary>
/// GeoServices feature representation
/// </summary>
public sealed class GeoServicesFeature
{
    /// <summary>
    /// Feature attributes as key-value pairs
    /// </summary>
    public required Dictionary<string, object?> Attributes { get; init; }

    /// <summary>
    /// Feature geometry (optional if returnGeometry=false)
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public GeoServicesGeometry? Geometry { get; init; }
}

/// <summary>
/// GeoServices geometry representation (point)
/// </summary>
public sealed class GeoServicesGeometry
{
    /// <summary>
    /// Indicates whether the geometry includes Z values
    /// </summary>
    [JsonPropertyName("hasZ")]
    public bool? HasZ { get; init; }

    /// <summary>
    /// Indicates whether the geometry includes M values
    /// </summary>
    [JsonPropertyName("hasM")]
    public bool? HasM { get; init; }

    /// <summary>
    /// X coordinate (longitude)
    /// </summary>
    public double? X { get; init; }

    /// <summary>
    /// Y coordinate (latitude)
    /// </summary>
    public double? Y { get; init; }

    /// <summary>
    /// Z coordinate (elevation)
    /// </summary>
    public double? Z { get; init; }

    /// <summary>
    /// Measure value
    /// </summary>
    public double? M { get; init; }

    /// <summary>
    /// Envelope minimum X
    /// </summary>
    [JsonPropertyName("xmin")]
    public double? Xmin { get; init; }

    /// <summary>
    /// Envelope minimum Y
    /// </summary>
    [JsonPropertyName("ymin")]
    public double? Ymin { get; init; }

    /// <summary>
    /// Envelope maximum X
    /// </summary>
    [JsonPropertyName("xmax")]
    public double? Xmax { get; init; }

    /// <summary>
    /// Envelope maximum Y
    /// </summary>
    [JsonPropertyName("ymax")]
    public double? Ymax { get; init; }

    /// <summary>
    /// MultiPoint coordinates
    /// </summary>
    public double[][]? Points { get; init; }

    /// <summary>
    /// Polyline paths
    /// </summary>
    public double[][][]? Paths { get; init; }

    /// <summary>
    /// Polygon rings
    /// </summary>
    public double[][][]? Rings { get; init; }

    /// <summary>
    /// Spatial reference information
    /// </summary>
    public GeoServicesSpatialReference? SpatialReference { get; init; }
}

/// <summary>
/// GeoServices spatial reference representation
/// </summary>
public sealed class GeoServicesSpatialReference
{
    /// <summary>
    /// Well-known ID for the spatial reference
    /// </summary>
    public int Wkid { get; init; }

    /// <summary>
    /// Latest well-known ID for the spatial reference
    /// </summary>
    public int? LatestWkid { get; init; }

    /// <summary>
    /// Well-known text representation (optional)
    /// </summary>
    public string? Wkt { get; init; }
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
    /// Fields to order results by (comma-separated list with optional ASC/DESC)
    /// </summary>
    public string? OrderByFields { get; init; }

    /// <summary>
    /// Whether to return geometry
    /// </summary>
    public bool ReturnGeometry { get; init; } = true;

    /// <summary>
    /// Whether to return only object IDs
    /// </summary>
    public bool ReturnIdsOnly { get; init; }

    /// <summary>
    /// Whether to return only the total count
    /// </summary>
    public bool ReturnCountOnly { get; init; }

    /// <summary>
    /// Whether to return only the extent of matching features
    /// </summary>
    public bool ReturnExtentOnly { get; init; }

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

    /// <summary>
    /// Filter geometry in GeoServices JSON format for spatial queries
    /// </summary>
    [JsonConverter(typeof(RawJsonStringConverter))]
    public string? Geometry { get; init; }

    /// <summary>
    /// Input spatial reference for geometry (WKID or WKT)
    /// </summary>
    [JsonPropertyName("inSR")]
    [JsonConverter(typeof(RawJsonStringConverter))]
    public string? InSr { get; init; }

    /// <summary>
    /// Output spatial reference for response geometry (WKID or WKT)
    /// </summary>
    [JsonPropertyName("outSR")]
    [JsonConverter(typeof(RawJsonStringConverter))]
    public string? OutSr { get; init; }

    /// <summary>
    /// Type of filter geometry (esriGeometryPoint, esriGeometryPolygon, esriGeometryEnvelope)
    /// </summary>
    public string? GeometryType { get; init; }

    /// <summary>
    /// Spatial relationship for filter (esriSpatialRelIntersects, esriSpatialRelContains, esriSpatialRelWithin,
    /// esriSpatialRelCrosses, esriSpatialRelTouches, esriSpatialRelOverlaps, esriSpatialRelDisjoint,
    /// esriSpatialRelEquals, esriSpatialRelWithinDistance, esriSpatialRelBeyondDistance)
    /// </summary>
    public string? SpatialRel { get; init; }

    /// <summary>
    /// Distance value for distance-based spatial queries (esriSpatialRelWithinDistance, esriSpatialRelBeyondDistance).
    /// Required when using distance-based spatial relationships.
    /// </summary>
    public double? Distance { get; init; }

    /// <summary>
    /// Unit of measure for distance queries. Supported values: esriSRUnit_Meter (default),
    /// esriSRUnit_Foot, esriSRUnit_Kilometer, esriSRUnit_StatuteMile.
    /// </summary>
    public string? Units { get; init; }

    /// <summary>
    /// Number of nearest neighbors to return for KNN queries.
    /// When specified with a geometry, returns the K closest features to that geometry.
    /// </summary>
    public int? NearestCount { get; init; }

    /// <summary>
    /// Whether to include the computed distance value in results for nearest neighbor queries.
    /// When true, a "distance" field will be added to each feature's attributes.
    /// </summary>
    public bool ReturnDistance { get; init; }

    /// <summary>
    /// Array of object IDs to retrieve. When specified, only features with these IDs will be returned.
    /// This parameter provides an alternative to using a WHERE clause for object ID filtering.
    /// </summary>
    public long[]? ObjectIds { get; init; }
}

internal sealed class RawJsonStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var longValue)
                ? longValue.ToString(CultureInfo.InvariantCulture)
                : reader.GetDouble().ToString(CultureInfo.InvariantCulture),
            JsonTokenType.StartObject or JsonTokenType.StartArray => JsonDocument.ParseValue(ref reader).RootElement.GetRawText(),
            JsonTokenType.Null => null,
            _ => throw new JsonException("Unsupported JSON token for string conversion.")
        };
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        if (value.StartsWith('{') || value.StartsWith('['))
        {
            using var document = JsonDocument.Parse(value);
            document.RootElement.WriteTo(writer);
            return;
        }

        writer.WriteStringValue(value);
    }
}

/// <summary>
/// GeoJSON FeatureSet response for query endpoint
/// </summary>
public sealed class GeoJsonFeatureSet
{
    /// <summary>
    /// GeoJSON type - always "FeatureCollection"
    /// </summary>
    public string Type { get; init; } = "FeatureCollection";

    /// <summary>
    /// Array of GeoJSON features
    /// </summary>
    public GeoJsonFeature[] Features { get; init; } = [];

    /// <summary>
    /// Additional properties (metadata)
    /// </summary>
    public Dictionary<string, object?>? Properties { get; init; }
}

/// <summary>
/// GeoJSON Feature representation
/// </summary>
public sealed class GeoJsonFeature
{
    /// <summary>
    /// GeoJSON type - always "Feature"
    /// </summary>
    public string Type { get; init; } = "Feature";

    /// <summary>
    /// Feature properties (attributes)
    /// </summary>
    public required Dictionary<string, object?> Properties { get; init; }

    /// <summary>
    /// Feature geometry (optional if returnGeometry=false)
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public GeoJsonGeometry? Geometry { get; init; }

    /// <summary>
    /// Feature ID (typically the objectid)
    /// </summary>
    public object? Id { get; init; }
}

/// <summary>
/// GeoJSON Geometry representation
/// </summary>
public sealed class GeoJsonGeometry
{
    /// <summary>
    /// Geometry type (Point, LineString, Polygon, etc.)
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Coordinate array - format depends on geometry type
    /// For Point: [x, y] or [x, y, z]
    /// For LineString: [[x, y], [x, y], ...]
    /// For Polygon: [[[x, y], [x, y], ...], ...]
    /// </summary>
    public required object? Coordinates { get; init; }

    /// <summary>
    /// Coordinate Reference System (optional)
    /// </summary>
    public GeoJsonCrs? Crs { get; init; }

    /// <summary>
    /// Geometry collection members (only when Type=GeometryCollection)
    /// </summary>
    public GeoJsonGeometry[]? Geometries { get; init; }
}

/// <summary>
/// GeoJSON Coordinate Reference System
/// </summary>
public sealed class GeoJsonCrs
{
    /// <summary>
    /// CRS type - typically "name"
    /// </summary>
    public string Type { get; init; } = "name";

    /// <summary>
    /// CRS properties
    /// </summary>
    public required Dictionary<string, object> Properties { get; init; }
}

/// <summary>
/// Attachment information for feature attachments
/// </summary>
public sealed class AttachmentInfo
{
    /// <summary>
    /// Attachment identifier
    /// </summary>
    public required long Id { get; init; }

    /// <summary>
    /// Original filename
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// MIME content type
    /// </summary>
    public required string ContentType { get; init; }

    /// <summary>
    /// File size in bytes
    /// </summary>
    public required long Size { get; init; }

    /// <summary>
    /// Optional keywords for the attachment
    /// </summary>
    public string? Keywords { get; init; }
}

/// <summary>
/// Response for querying feature attachments
/// </summary>
public sealed class AttachmentQueryResponse
{
    /// <summary>
    /// Array of attachment information
    /// </summary>
    public required AttachmentInfo[] AttachmentInfos { get; init; }
}

/// <summary>
/// Result of adding an attachment
/// </summary>
public sealed class AddAttachmentResult
{
    /// <summary>
    /// Feature ID that owns the attachment
    /// </summary>
    public required long ObjectId { get; init; }

    /// <summary>
    /// Whether the operation was successful
    /// </summary>
    public required bool Success { get; init; }
}

/// <summary>
/// Response for adding an attachment
/// </summary>
public sealed class AddAttachmentResponse
{
    /// <summary>
    /// Add attachment result
    /// </summary>
    public required AddAttachmentResult AddAttachmentResult { get; init; }
}

/// <summary>
/// Result of updating an attachment
/// </summary>
public sealed class UpdateAttachmentResult
{
    /// <summary>
    /// Feature ID that owns the attachment
    /// </summary>
    public required long ObjectId { get; init; }

    /// <summary>
    /// Whether the operation was successful
    /// </summary>
    public required bool Success { get; init; }
}

/// <summary>
/// Response for updating an attachment
/// </summary>
public sealed class UpdateAttachmentResponse
{
    /// <summary>
    /// Update attachment result
    /// </summary>
    public required UpdateAttachmentResult UpdateAttachmentResult { get; init; }
}

/// <summary>
/// Result of deleting an attachment
/// </summary>
public sealed class DeleteAttachmentResult
{
    /// <summary>
    /// Feature ID that owns the attachment
    /// </summary>
    public required long ObjectId { get; init; }

    /// <summary>
    /// Whether the operation was successful
    /// </summary>
    public required bool Success { get; init; }
}

/// <summary>
/// Response for deleting attachments
/// </summary>
public sealed class DeleteAttachmentsResponse
{
    /// <summary>
    /// Delete attachment results
    /// </summary>
    public required DeleteAttachmentResult[] DeleteAttachmentResults { get; init; }
}

/// <summary>
/// Request parameters for queryRelatedRecords endpoint
/// </summary>
public sealed class QueryRelatedRecordsParameters
{
    /// <summary>
    /// Array of object IDs for source features
    /// </summary>
    public required long[] ObjectIds { get; init; }

    /// <summary>
    /// ID of the relationship to traverse
    /// </summary>
    public required int RelationshipId { get; init; }

    /// <summary>
    /// Comma-separated list of field names to return (default: all fields)
    /// </summary>
    public string? OutFields { get; init; }

    /// <summary>
    /// SQL WHERE clause to filter related features
    /// </summary>
    public string? Where { get; init; }

    /// <summary>
    /// Whether to return geometry information
    /// </summary>
    public bool ReturnGeometry { get; init; } = true;

    /// <summary>
    /// Response format (json, geojson)
    /// </summary>
    public string F { get; init; } = "json";

    /// <summary>
    /// Starting offset for pagination
    /// </summary>
    public int? ResultOffset { get; init; }

    /// <summary>
    /// Maximum number of related records to return
    /// </summary>
    public int? ResultRecordCount { get; init; }
}

/// <summary>
/// Response model for queryRelatedRecords endpoint
/// </summary>
public sealed class QueryRelatedRecordsResponse
{
    /// <summary>
    /// Array of related record groups, one per source object ID
    /// </summary>
    public required RelatedRecordGroup[] RelatedRecordGroups { get; init; }
}

/// <summary>
/// Related records grouped by source object ID
/// </summary>
public sealed class RelatedRecordGroup
{
    /// <summary>
    /// Object ID of the source feature
    /// </summary>
    public required long ObjectId { get; init; }

    /// <summary>
    /// Related records for this source feature (null if no related records)
    /// </summary>
    public RelatedRecords? RelatedRecords { get; init; }
}

/// <summary>
/// Related records result set
/// </summary>
public sealed class RelatedRecords
{
    /// <summary>
    /// Object ID field name
    /// </summary>
    public string ObjectIdFieldName { get; init; } = "objectid";

    /// <summary>
    /// Global ID field name (if used)
    /// </summary>
    public string? GlobalIdFieldName { get; init; }

    /// <summary>
    /// Array of field definitions
    /// </summary>
    public GeoServicesFieldInfo[] Fields { get; init; } = [];

    /// <summary>
    /// Spatial reference system for geometries
    /// </summary>
    public GeoServicesSpatialReference? SpatialReference { get; init; }

    /// <summary>
    /// Array of related features
    /// </summary>
    public GeoServicesFeature[] Features { get; init; } = [];
}

/// <summary>
/// JSON source generation context for FeatureServer models (AOT compatibility)
/// </summary>
/// <summary>
/// Request model for the applyEdits endpoint
/// </summary>
public class ApplyEditsRequest
{
    /// <summary>
    /// Array of features to add
    /// </summary>
    [JsonPropertyName("adds")]
    public GeoServicesFeature[]? Adds { get; set; }

    /// <summary>
    /// Array of features to update
    /// </summary>
    [JsonPropertyName("updates")]
    public GeoServicesFeature[]? Updates { get; set; }

    /// <summary>
    /// Array of objectIds to delete
    /// </summary>
    [JsonPropertyName("deletes")]
    public object[]? Deletes { get; set; }

    /// <summary>
    /// Whether to rollback all changes on failure
    /// </summary>
    [JsonPropertyName("rollbackOnFailure")]
    public bool RollbackOnFailure { get; set; } = false; // GeoServices default is false

    /// <summary>
    /// Whether to use global IDs
    /// </summary>
    [JsonPropertyName("useGlobalIds")]
    public bool UseGlobalIds { get; set; } = false;
}

/// <summary>
/// Response model for the applyEdits endpoint
/// </summary>
public class ApplyEditsResponse
{
    /// <summary>
    /// Results of add operations
    /// </summary>
    [JsonPropertyName("addResults")]
    public EditResult[]? AddResults { get; set; }

    /// <summary>
    /// Results of update operations
    /// </summary>
    [JsonPropertyName("updateResults")]
    public EditResult[]? UpdateResults { get; set; }

    /// <summary>
    /// Results of delete operations
    /// </summary>
    [JsonPropertyName("deleteResults")]
    public EditResult[]? DeleteResults { get; set; }

    /// <summary>
    /// Whether the entire transaction succeeded
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; } = true;
}

/// <summary>
/// Result of an individual edit operation
/// </summary>
public class EditResult
{
    /// <summary>
    /// Object ID of the affected feature
    /// </summary>
    [JsonPropertyName("objectId")]
    public long? ObjectId { get; set; }

    /// <summary>
    /// Global ID of the affected feature (if applicable)
    /// </summary>
    [JsonPropertyName("globalId")]
    public string? GlobalId { get; set; }

    /// <summary>
    /// Whether this operation succeeded
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; } = true;

    /// <summary>
    /// Error information if operation failed
    /// </summary>
    [JsonPropertyName("error")]
    public EditError? Error { get; set; }
}

/// <summary>
/// Error information for failed edit operations
/// </summary>
public class EditError
{
    /// <summary>
    /// Error code
    /// </summary>
    [JsonPropertyName("code")]
    public int Code { get; set; }

    /// <summary>
    /// Error description
    /// </summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// JSON serialization context for FeatureServer API models with source generation for AOT compatibility.
/// </summary>
[JsonSerializable(typeof(FeatureServerResponse))]
[JsonSerializable(typeof(LayerResponse))]
[JsonSerializable(typeof(LayerInfo))]
[JsonSerializable(typeof(SpatialReferenceInfo))]
[JsonSerializable(typeof(ExtentInfo))]
[JsonSerializable(typeof(GeoServicesFieldInfo))]
[JsonSerializable(typeof(QueryResponse))]
[JsonSerializable(typeof(GeoServicesFeature))]
[JsonSerializable(typeof(GeoServicesFeature[]), TypeInfoPropertyName = "GeoServicesFeatureArray")]
[JsonSerializable(typeof(GeoServicesGeometry))]
[JsonSerializable(typeof(GeoServicesSpatialReference))]
[JsonSerializable(typeof(QueryParameters))]
[JsonSerializable(typeof(GeoJsonFeatureSet))]
[JsonSerializable(typeof(GeoJsonFeature), TypeInfoPropertyName = "FeatureServerGeoJsonFeature")]
[JsonSerializable(typeof(GeoJsonFeature[]), TypeInfoPropertyName = "FeatureServerGeoJsonFeatureArray")]
[JsonSerializable(typeof(GeoJsonGeometry))]
[JsonSerializable(typeof(GeoJsonCrs))]
[JsonSerializable(typeof(ApplyEditsRequest))]
[JsonSerializable(typeof(ApplyEditsResponse))]
[JsonSerializable(typeof(EditResult))]
[JsonSerializable(typeof(EditError))]
[JsonSerializable(typeof(QueryRelatedRecordsParameters))]
[JsonSerializable(typeof(QueryRelatedRecordsResponse))]
[JsonSerializable(typeof(RelatedRecordGroup))]
[JsonSerializable(typeof(RelatedRecords))]
[JsonSerializable(typeof(double[]))]
[JsonSerializable(typeof(double[][]))]
[JsonSerializable(typeof(double[][][]))]
[JsonSerializable(typeof(ApiErrorResponse))]
[JsonSerializable(typeof(GeoServicesError))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(AttachmentInfo))]
[JsonSerializable(typeof(AttachmentQueryResponse))]
[JsonSerializable(typeof(AddAttachmentResult))]
[JsonSerializable(typeof(AddAttachmentResponse))]
[JsonSerializable(typeof(UpdateAttachmentResult))]
[JsonSerializable(typeof(UpdateAttachmentResponse))]
[JsonSerializable(typeof(DeleteAttachmentResult))]
[JsonSerializable(typeof(DeleteAttachmentsResponse))]
// OData v4 types (temporarily disabled for Issue 46 performance testing)
// [JsonSerializable(typeof(ODataServiceRoot))]
// [JsonSerializable(typeof(ODataEntitySetInfo))]
// [JsonSerializable(typeof(ODataEntitySetResponse))]
// [JsonSerializable(typeof(ODataSingleEntityResponse))]
// [JsonSerializable(typeof(ODataErrorResponse))]
// [JsonSerializable(typeof(ODataErrorDetails))]
// [JsonSerializable(typeof(ODataFeatureEntity))]
// [JsonSerializable(typeof(IReadOnlyList<ODataEntitySetInfo>))]
[JsonSerializable(typeof(IReadOnlyList<object>))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(DateTimeOffset))]
// ASP.NET Core types
[JsonSerializable(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails))]
// OGC API Features models
[JsonSerializable(typeof(Honua.Server.Features.OgcFeatures.Models.LandingPage))]
[JsonSerializable(typeof(Honua.Server.Features.OgcFeatures.Models.ConformanceDeclaration))]
[JsonSerializable(typeof(Honua.Server.Features.OgcFeatures.Models.Link))]
[JsonSerializable(typeof(Honua.Server.Features.OgcFeatures.Models.Collections))]
[JsonSerializable(typeof(Honua.Server.Features.OgcFeatures.Models.CollectionInfo))]
[JsonSerializable(typeof(Honua.Server.Features.OgcFeatures.Models.Extent))]
[JsonSerializable(typeof(Honua.Server.Features.OgcFeatures.Models.SpatialExtent))]
[JsonSerializable(typeof(Honua.Server.Features.OgcFeatures.Models.TemporalExtent))]
[JsonSerializable(typeof(Honua.Server.Features.OgcFeatures.Models.SimpleGeoJsonGeometry))]
[JsonSerializable(typeof(Honua.Server.Features.OgcFeatures.Models.GeoJsonFeature), TypeInfoPropertyName = "OgcGeoJsonFeature")]
[JsonSerializable(typeof(Honua.Server.Features.OgcFeatures.Models.GeoJsonFeature[]), TypeInfoPropertyName = "OgcGeoJsonFeatureArray")]
[JsonSerializable(typeof(Honua.Server.Features.OgcFeatures.Models.FeatureCollection))]
[JsonSerializable(typeof(ImmutableArray<Honua.Server.Features.OgcFeatures.Models.Link>))]
[JsonSerializable(typeof(ImmutableArray<Honua.Server.Features.OgcFeatures.Models.CollectionInfo>))]
[JsonSerializable(typeof(ImmutableArray<string>))]
[JsonSerializable(typeof(ImmutableArray<ImmutableArray<double>>))]
[JsonSerializable(typeof(ImmutableArray<ImmutableArray<string?>>))]
[JsonSerializable(typeof(ImmutableArray<string>?))]

// Interface types for AOT compatibility
[JsonSerializable(typeof(Honua.Core.Features.FeatureStore.Abstractions.IFeatureStore))]
[JsonSerializable(typeof(Honua.Core.Features.Catalog.Abstractions.ILayerCatalog))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public sealed partial class FeatureServerJsonContext : JsonSerializerContext
{
}
