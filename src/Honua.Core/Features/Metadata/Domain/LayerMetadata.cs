// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.Metadata.Domain;

/// <summary>
/// Unified metadata for a geospatial layer, containing all information needed by protocol formatters.
/// Consolidates layer definition, computed statistics, and protocol-specific information.
/// </summary>
public sealed record LayerMetadata
{
    /// <summary>
    /// Core layer definition from the catalog.
    /// </summary>
    public LayerDefinition Definition { get; init; } = null!;

    /// <summary>
    /// Layer identity and descriptive information.
    /// </summary>
    public LayerIdentity Identity { get; init; } = null!;

    /// <summary>
    /// Field schema information with enhanced metadata.
    /// </summary>
    public LayerFieldInfo[] Fields { get; init; } = Array.Empty<LayerFieldInfo>();

    /// <summary>
    /// Computed statistics about the layer data.
    /// </summary>
    public LayerStatistics? Statistics { get; init; }

    /// <summary>
    /// Spatial information including geometry and extent details.
    /// </summary>
    public LayerSpatialInfo SpatialInfo { get; init; } = new();

    /// <summary>
    /// Temporal information for time-aware layers.
    /// </summary>
    public LayerTemporalInfo? TemporalInfo { get; init; }

    /// <summary>
    /// Styling and rendering information.
    /// </summary>
    public LayerStyleInfo? StyleInfo { get; init; }

    /// <summary>
    /// Relationship metadata for related tables/layers.
    /// </summary>
    public LayerRelationshipInfo[] Relationships { get; init; } = Array.Empty<LayerRelationshipInfo>();

    /// <summary>
    /// Layer-specific capabilities and operations.
    /// </summary>
    public LayerCapabilities Capabilities { get; init; } = new();

    /// <summary>
    /// Access control information for this layer.
    /// </summary>
    public LayerAccessInfo AccessControl { get; init; } = new();

    /// <summary>
    /// Protocol-specific layer information.
    /// </summary>
    public Dictionary<string, object> ProtocolExtensions { get; init; } = new();

    /// <summary>
    /// When this layer metadata was generated.
    /// </summary>
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Layer identity and descriptive information.
/// </summary>
public sealed record LayerIdentity
{
    /// <summary>
    /// Layer ID (unique within service).
    /// </summary>
    public int Id { get; init; }

    /// <summary>
    /// Layer name (URL segment identifier).
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Human-readable layer title.
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Layer description.
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Keywords for layer discovery.
    /// </summary>
    public string[] Keywords { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Copyright or attribution text.
    /// </summary>
    public string? Copyright { get; init; }

    /// <summary>
    /// Layer type classification.
    /// </summary>
    public string LayerType { get; init; } = "Feature Layer";

    /// <summary>
    /// Whether this layer is visible by default.
    /// </summary>
    public bool DefaultVisibility { get; init; } = true;

    /// <summary>
    /// Minimum scale for layer visibility.
    /// </summary>
    public double? MinScale { get; init; }

    /// <summary>
    /// Maximum scale for layer visibility.
    /// </summary>
    public double? MaxScale { get; init; }
}

/// <summary>
/// Enhanced field information with computed metadata.
/// </summary>
public sealed record LayerFieldInfo
{
    /// <summary>
    /// Core field definition.
    /// </summary>
    public FieldDefinition Definition { get; init; } = null!;

    /// <summary>
    /// Field statistics (for numeric fields).
    /// </summary>
    public FieldStatistics? Statistics { get; init; }

    /// <summary>
    /// Unique values (for categorical fields, limited set).
    /// </summary>
    public object[]? UniqueValues { get; init; }

    /// <summary>
    /// Whether this field is indexed for performance.
    /// </summary>
    public bool IsIndexed { get; init; } = false;

    /// <summary>
    /// Whether this field supports full-text search.
    /// </summary>
    public bool IsSearchable { get; init; } = false;

    /// <summary>
    /// Domain information for coded values.
    /// </summary>
    public FieldDomain? Domain { get; init; }

    /// <summary>
    /// Field validation rules.
    /// </summary>
    public FieldValidation? Validation { get; init; }
}

/// <summary>
/// Statistical information for numeric fields.
/// </summary>
public sealed record FieldStatistics
{
    /// <summary>
    /// Minimum value.
    /// </summary>
    public object? Min { get; init; }

    /// <summary>
    /// Maximum value.
    /// </summary>
    public object? Max { get; init; }

    /// <summary>
    /// Average value (numeric fields only).
    /// </summary>
    public double? Average { get; init; }

    /// <summary>
    /// Standard deviation (numeric fields only).
    /// </summary>
    public double? StandardDeviation { get; init; }

    /// <summary>
    /// Count of non-null values.
    /// </summary>
    public long NonNullCount { get; init; }

    /// <summary>
    /// Count of null values.
    /// </summary>
    public long NullCount { get; init; }

    /// <summary>
    /// Count of distinct values.
    /// </summary>
    public long? DistinctCount { get; init; }
}

/// <summary>
/// Field domain information for coded values and ranges.
/// </summary>
public sealed record FieldDomain
{
    /// <summary>
    /// Domain type (coded, range, etc.).
    /// </summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>
    /// Domain name.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Coded values for domain (type=coded).
    /// </summary>
    public CodedValue[]? CodedValues { get; init; }

    /// <summary>
    /// Range information (type=range).
    /// </summary>
    public RangeDomain? Range { get; init; }
}

/// <summary>
/// Coded value definition.
/// </summary>
public sealed record CodedValue(object Code, string Name, string? Description = null);

/// <summary>
/// Range domain definition.
/// </summary>
public sealed record RangeDomain(object MinValue, object MaxValue);

/// <summary>
/// Field validation rules.
/// </summary>
public sealed record FieldValidation
{
    /// <summary>
    /// Whether the field is required.
    /// </summary>
    public bool IsRequired { get; init; } = false;

    /// <summary>
    /// Maximum length for text fields.
    /// </summary>
    public int? MaxLength { get; init; }

    /// <summary>
    /// Regular expression pattern for validation.
    /// </summary>
    public string? Pattern { get; init; }

    /// <summary>
    /// Custom validation rules.
    /// </summary>
    public Dictionary<string, object>? CustomRules { get; init; }
}

/// <summary>
/// Computed statistics about layer data.
/// </summary>
public sealed record LayerStatistics
{
    /// <summary>
    /// Total feature count in the layer.
    /// </summary>
    public long? FeatureCount { get; init; }

    /// <summary>
    /// Whether feature count is approximate.
    /// </summary>
    public bool FeatureCountIsApproximate { get; init; } = false;

    /// <summary>
    /// Data size information.
    /// </summary>
    public DataSizeInfo? SizeInfo { get; init; }

    /// <summary>
    /// When statistics were last computed.
    /// </summary>
    public DateTimeOffset? LastUpdated { get; init; }

    /// <summary>
    /// Statistics computation time.
    /// </summary>
    public TimeSpan? ComputationTime { get; init; }
}

/// <summary>
/// Data size information.
/// </summary>
public sealed record DataSizeInfo
{
    /// <summary>
    /// Estimated storage size in bytes.
    /// </summary>
    public long? StorageSizeBytes { get; init; }

    /// <summary>
    /// Average feature size in bytes.
    /// </summary>
    public double? AverageFeatureSizeBytes { get; init; }

    /// <summary>
    /// Index size in bytes.
    /// </summary>
    public long? IndexSizeBytes { get; init; }
}

/// <summary>
/// Layer spatial information including geometry details.
/// </summary>
public sealed record LayerSpatialInfo
{
    /// <summary>
    /// Primary geometry type for the layer.
    /// </summary>
    public GeometryType GeometryType { get; init; } = GeometryType.None;

    /// <summary>
    /// Whether the layer has geometry data.
    /// </summary>
    public bool HasGeometry { get; init; } = false;

    /// <summary>
    /// Spatial reference system for the layer.
    /// </summary>
    public SpatialReference SpatialReference { get; init; } = SpatialReference.WGS84;

    /// <summary>
    /// Layer extent in layer coordinate system.
    /// </summary>
    public FeatureExtent? Extent { get; init; }

    /// <summary>
    /// Whether extent is computed or cached.
    /// </summary>
    public bool ExtentIsComputed { get; init; } = false;

    /// <summary>
    /// Supported spatial operations.
    /// </summary>
    public SpatialOperations SupportedOperations { get; init; } = new();

    /// <summary>
    /// Geometry complexity information.
    /// </summary>
    public GeometryComplexity? Complexity { get; init; }
}

/// <summary>
/// Supported spatial operations for the layer.
/// </summary>
public sealed record SpatialOperations
{
    /// <summary>
    /// Supported spatial relationships for queries.
    /// </summary>
    public string[] SpatialRelations { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Whether spatial indexing is available.
    /// </summary>
    public bool HasSpatialIndex { get; init; } = false;

    /// <summary>
    /// Supported geometry analysis operations.
    /// </summary>
    public string[] AnalysisOperations { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Geometry complexity information.
/// </summary>
public sealed record GeometryComplexity
{
    /// <summary>
    /// Average vertex count per geometry.
    /// </summary>
    public double? AverageVertexCount { get; init; }

    /// <summary>
    /// Maximum vertex count in any geometry.
    /// </summary>
    public int? MaxVertexCount { get; init; }

    /// <summary>
    /// Whether geometries have curves.
    /// </summary>
    public bool HasCurves { get; init; } = false;

    /// <summary>
    /// Whether geometries have Z values.
    /// </summary>
    public bool HasZ { get; init; } = false;

    /// <summary>
    /// Whether geometries have M values.
    /// </summary>
    public bool HasM { get; init; } = false;
}

/// <summary>
/// Temporal information for time-aware layers.
/// </summary>
public sealed record LayerTemporalInfo
{
    /// <summary>
    /// Start time field name.
    /// </summary>
    public string? StartTimeField { get; init; }

    /// <summary>
    /// End time field name (for intervals).
    /// </summary>
    public string? EndTimeField { get; init; }

    /// <summary>
    /// Track identifier field for temporal sequences.
    /// </summary>
    public string? TrackIdField { get; init; }

    /// <summary>
    /// Time extent of the layer data.
    /// </summary>
    public TemporalExtent? TimeExtent { get; init; }

    /// <summary>
    /// Supported temporal capabilities.
    /// </summary>
    public TemporalCapabilities Capabilities { get; init; } = new();
}

/// <summary>
/// Temporal extent information.
/// </summary>
public sealed record TemporalExtent
{
    /// <summary>
    /// Start of time extent.
    /// </summary>
    public DateTimeOffset Start { get; init; }

    /// <summary>
    /// End of time extent.
    /// </summary>
    public DateTimeOffset End { get; init; }

    /// <summary>
    /// Whether extent is computed or cached.
    /// </summary>
    public bool IsComputed { get; init; } = false;
}

/// <summary>
/// Layer styling and rendering information.
/// </summary>
public sealed record LayerStyleInfo
{
    /// <summary>
    /// Default renderer configuration.
    /// </summary>
    public JsonElement? DefaultRenderer { get; init; }

    /// <summary>
    /// Drawing information for the layer.
    /// </summary>
    public JsonElement? DrawingInfo { get; init; }

    /// <summary>
    /// Available predefined styles.
    /// </summary>
    public StyleDefinition[]? PredefinedStyles { get; init; }

    /// <summary>
    /// Symbology capabilities.
    /// </summary>
    public SymbologyCapabilities Capabilities { get; init; } = new();
}

/// <summary>
/// Style definition.
/// </summary>
public sealed record StyleDefinition
{
    /// <summary>
    /// Style name.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Style title.
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Style definition (renderer JSON).
    /// </summary>
    public JsonElement Definition { get; init; }
}

/// <summary>
/// Symbology capabilities for the layer.
/// </summary>
public sealed record SymbologyCapabilities
{
    /// <summary>
    /// Supported renderer types.
    /// </summary>
    public string[] RendererTypes { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Whether custom symbols are supported.
    /// </summary>
    public bool SupportsCustomSymbols { get; init; } = false;

    /// <summary>
    /// Whether transparency is supported.
    /// </summary>
    public bool SupportsTransparency { get; init; } = true;
}

/// <summary>
/// Layer relationship information.
/// </summary>
public sealed record LayerRelationshipInfo
{
    /// <summary>
    /// Relationship definition.
    /// </summary>
    public Relationship? Definition { get; init; }

    /// <summary>
    /// Related layer/table information.
    /// </summary>
    public RelatedResourceInfo RelatedResource { get; init; } = null!;

    /// <summary>
    /// Relationship capabilities.
    /// </summary>
    public RelationshipCapabilities Capabilities { get; init; } = new();
}

/// <summary>
/// Related resource information.
/// </summary>
public sealed record RelatedResourceInfo
{
    /// <summary>
    /// Resource ID.
    /// </summary>
    public int Id { get; init; }

    /// <summary>
    /// Resource name.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Resource type (layer, table).
    /// </summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>
    /// Whether resource is available.
    /// </summary>
    public bool IsAvailable { get; init; } = true;
}

/// <summary>
/// Relationship operation capabilities.
/// </summary>
public sealed record RelationshipCapabilities
{
    /// <summary>
    /// Whether relationship queries are supported.
    /// </summary>
    public bool SupportsQuery { get; init; } = true;

    /// <summary>
    /// Whether relationship editing is supported.
    /// </summary>
    public bool SupportsEdit { get; init; } = false;

    /// <summary>
    /// Maximum related records returned.
    /// </summary>
    public int MaxRelatedRecords { get; init; } = 1000;
}

/// <summary>
/// Layer-specific capabilities and operations.
/// </summary>
public sealed record LayerCapabilities
{
    /// <summary>
    /// Query capabilities for this layer.
    /// </summary>
    public LayerQueryCapabilities Query { get; init; } = new();

    /// <summary>
    /// Edit capabilities for this layer (null if read-only).
    /// </summary>
    public LayerEditCapabilities? Edit { get; init; }

    /// <summary>
    /// Attachment capabilities (null if not supported).
    /// </summary>
    public AttachmentCapabilities? Attachments { get; init; }

    /// <summary>
    /// Sync capabilities for offline usage.
    /// </summary>
    public SyncCapabilities? Sync { get; init; }
}

/// <summary>
/// Layer-specific query capabilities.
/// </summary>
public sealed record LayerQueryCapabilities
{
    /// <summary>
    /// Whether SQL queries are supported.
    /// </summary>
    public bool SupportsSql { get; init; } = true;

    /// <summary>
    /// Whether spatial queries are supported.
    /// </summary>
    public bool SupportsSpatialQuery { get; init; } = true;

    /// <summary>
    /// Whether temporal queries are supported.
    /// </summary>
    public bool SupportsTemporalQuery { get; init; } = false;

    /// <summary>
    /// Maximum features per query.
    /// </summary>
    public int MaxRecordCount { get; init; } = 1000;

    /// <summary>
    /// Whether pagination is supported.
    /// </summary>
    public bool SupportsPagination { get; init; } = true;

    /// <summary>
    /// Whether query statistics are supported.
    /// </summary>
    public bool SupportsStatistics { get; init; } = true;
}

/// <summary>
/// Layer editing capabilities.
/// </summary>
public sealed record LayerEditCapabilities
{
    /// <summary>
    /// Whether creating features is allowed.
    /// </summary>
    public bool SupportsCreate { get; init; } = false;

    /// <summary>
    /// Whether updating features is allowed.
    /// </summary>
    public bool SupportsUpdate { get; init; } = false;

    /// <summary>
    /// Whether deleting features is allowed.
    /// </summary>
    public bool SupportsDelete { get; init; } = false;

    /// <summary>
    /// Whether geometry editing is allowed.
    /// </summary>
    public bool SupportsGeometryUpdate { get; init; } = false;

    /// <summary>
    /// Whether attribute editing is allowed.
    /// </summary>
    public bool SupportsAttributeUpdate { get; init; } = false;

    /// <summary>
    /// Fields that can be edited.
    /// </summary>
    public string[] EditableFields { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Attachment capabilities.
/// </summary>
public sealed record AttachmentCapabilities
{
    /// <summary>
    /// Whether attachments are supported.
    /// </summary>
    public bool SupportsAttachments { get; init; } = false;

    /// <summary>
    /// Maximum attachment size in bytes.
    /// </summary>
    public long MaxAttachmentSize { get; init; } = 10 * 1024 * 1024; // 10 MB

    /// <summary>
    /// Supported attachment formats.
    /// </summary>
    public string[] SupportedFormats { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Maximum number of attachments per feature.
    /// </summary>
    public int MaxAttachmentsPerFeature { get; init; } = 10;
}

/// <summary>
/// Synchronization capabilities for offline usage.
/// </summary>
public sealed record SyncCapabilities
{
    /// <summary>
    /// Whether sync is supported.
    /// </summary>
    public bool SupportsSync { get; init; } = false;

    /// <summary>
    /// Whether bidirectional sync is supported.
    /// </summary>
    public bool SupportsBidirectionalSync { get; init; } = false;

    /// <summary>
    /// Sync data retention period.
    /// </summary>
    public TimeSpan? DataRetentionPeriod { get; init; }
}