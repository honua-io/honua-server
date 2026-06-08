// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Admin.Domain;

/// <summary>
/// Request payload for publishing a PostGIS table as a layer.
/// </summary>
public sealed class LayerPublishRequest
{
    /// <summary>
    /// Schema containing the source table (e.g., "public").
    /// </summary>
    public required string Schema { get; init; }

    /// <summary>
    /// Source table name.
    /// </summary>
    public required string Table { get; init; }

    /// <summary>
    /// Layer display name.
    /// </summary>
    public required string LayerName { get; init; }

    /// <summary>
    /// Optional layer description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Name of the geometry column (optional for attribute-only tables).
    /// </summary>
    public string? GeometryColumn { get; init; }

    /// <summary>
    /// Geometry type (e.g., "Point", "Polygon").
    /// </summary>
    public string? GeometryType { get; init; }

    /// <summary>
    /// Spatial reference identifier (SRID).
    /// </summary>
    public int? Srid { get; init; }

    /// <summary>
    /// Primary key column name.
    /// </summary>
    public string? PrimaryKey { get; init; }

    /// <summary>
    /// List of attribute fields to publish (empty means include all).
    /// </summary>
    public IReadOnlyList<string> Fields { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Optional service name for publishing (defaults to "default").
    /// </summary>
    public string? ServiceName { get; init; }

    /// <summary>
    /// Optional connection identifier to associate with the service.
    /// </summary>
    public Guid? ConnectionId { get; init; }

    /// <summary>
    /// Whether the layer should be enabled after publishing.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Optional per-field value domains captured from the migration source,
    /// keyed by source field name (case-insensitive). When a published column
    /// matches a key, the domain is carried into the Metadata v2 field so it
    /// persists into the graph and is served via the FeatureServer field
    /// <c>domain</c> and <c>queryDomains</c> surfaces. Fields without an entry
    /// publish without a domain.
    /// </summary>
    public IReadOnlyDictionary<string, Honua.Core.Features.Metadata.Domain.V2.MetadataV2FieldDomain> FieldDomains { get; init; }
        = new Dictionary<string, Honua.Core.Features.Metadata.Domain.V2.MetadataV2FieldDomain>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Optional Esri-style subtype set captured from the migration source. When the
    /// declared <see cref="Honua.Core.Features.Metadata.Domain.V2.MetadataV2Subtypes.SubtypeField"/>
    /// matches a published column, the subtypes are carried into the Metadata v2
    /// resource so they persist into the graph and are served on the FeatureServer
    /// layer metadata (<c>subtypeField</c> / <c>subtypes</c> / <c>defaultSubtypeCode</c>).
    /// Null when the source layer declared no subtypes.
    /// </summary>
    public Honua.Core.Features.Metadata.Domain.V2.MetadataV2Subtypes? Subtypes { get; init; }

    /// <summary>
    /// Optional Esri-style attribute rules (calculation / constraint / validation)
    /// captured from the migration source. Calculation rules whose target field is
    /// published, and constraint/validation rules, are carried into the Metadata v2
    /// resource so they fire on the shared edit path (FeatureServer <c>applyEdits</c>).
    /// Null when the source layer declared none.
    /// </summary>
    public IReadOnlyList<Honua.Core.Features.Metadata.Domain.V2.MetadataV2AttributeRule>? AttributeRules { get; init; }
}

/// <summary>
/// Request payload for validating a PostGIS table before publishing it as a layer.
/// </summary>
public sealed class TablePublishValidationRequest
{
    /// <summary>
    /// Schema containing the source table.
    /// </summary>
    public required string Schema { get; init; }

    /// <summary>
    /// Source table name.
    /// </summary>
    public required string Table { get; init; }

    /// <summary>
    /// Optional layer display name.
    /// </summary>
    public string? LayerName { get; init; }

    /// <summary>
    /// Optional service name for projection compatibility checks.
    /// </summary>
    public string? ServiceName { get; init; }

    /// <summary>
    /// Requested target spatial reference identifier. When omitted, an existing service SRID or table SRID is used.
    /// </summary>
    public int? TargetSrid { get; init; }

    /// <summary>
    /// Requested geometry column name. When omitted, discovery metadata is used.
    /// </summary>
    public string? GeometryColumn { get; init; }

    /// <summary>
    /// Requested primary key column name. When omitted, the table primary key or common object id names are used.
    /// </summary>
    public string? PrimaryKey { get; init; }

    /// <summary>
    /// Selected attribute fields to publish. Empty means all discovered columns.
    /// </summary>
    public IReadOnlyList<string> Fields { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Structured result for validating a table before layer publishing.
/// </summary>
public sealed class TablePublishValidationResult
{
    /// <summary>
    /// Whether validation passed without errors.
    /// </summary>
    public bool IsValid { get; init; }

    /// <summary>
    /// Validation status: valid, warning, or invalid.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Source schema.
    /// </summary>
    public required string Schema { get; init; }

    /// <summary>
    /// Source table.
    /// </summary>
    public required string Table { get; init; }

    /// <summary>
    /// Normalized service name used for compatibility checks.
    /// </summary>
    public required string ServiceName { get; init; }

    /// <summary>
    /// Optional layer display name.
    /// </summary>
    public string? LayerName { get; init; }

    /// <summary>
    /// Resolved geometry column.
    /// </summary>
    public string? GeometryColumn { get; init; }

    /// <summary>
    /// Resolved geometry type.
    /// </summary>
    public string? GeometryType { get; init; }

    /// <summary>
    /// Resolved primary key column.
    /// </summary>
    public string? PrimaryKey { get; init; }

    /// <summary>
    /// Object id strategy selected for layer publishing.
    /// </summary>
    public string ObjectIdStrategy { get; init; } = "unresolved";

    /// <summary>
    /// Source table SRID from discovery metadata.
    /// </summary>
    public int? SourceSrid { get; init; }

    /// <summary>
    /// Existing service SRID when the target service already exists.
    /// </summary>
    public int? ServiceSrid { get; init; }

    /// <summary>
    /// Target SRID the publish operation should use.
    /// </summary>
    public int? TargetSrid { get; init; }

    /// <summary>
    /// Estimated row count from discovery metadata.
    /// </summary>
    public long? EstimatedRows { get; init; }

    /// <summary>
    /// Actual source row count when it could be sampled.
    /// </summary>
    public long? FeatureCount { get; init; }

    /// <summary>
    /// Count of rows with NULL geometry.
    /// </summary>
    public long? NullGeometryCount { get; init; }

    /// <summary>
    /// Count of rows with invalid geometry.
    /// </summary>
    public long? InvalidGeometryCount { get; init; }

    /// <summary>
    /// Discovered field metadata.
    /// </summary>
    public TablePublishValidationField[] Fields { get; init; } = [];

    /// <summary>
    /// Individual validation checks.
    /// </summary>
    public TablePublishValidationCheck[] Checks { get; init; } = [];
}

/// <summary>
/// Field metadata returned by table publish validation.
/// </summary>
public sealed class TablePublishValidationField
{
    /// <summary>
    /// Source column name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Source database type.
    /// </summary>
    public required string DataType { get; init; }

    /// <summary>
    /// Honua field type inferred from the source type.
    /// </summary>
    public required string FieldType { get; init; }

    /// <summary>
    /// Whether the source column allows null values.
    /// </summary>
    public bool IsNullable { get; init; }

    /// <summary>
    /// Whether the source column is part of the table primary key.
    /// </summary>
    public bool IsPrimaryKey { get; init; }

    /// <summary>
    /// Whether this field is selected for publishing.
    /// </summary>
    public bool IsSelected { get; init; }

    /// <summary>
    /// Maximum length for character types.
    /// </summary>
    public int? MaxLength { get; init; }
}

/// <summary>
/// Individual validation check for pre-publish table validation.
/// </summary>
public sealed class TablePublishValidationCheck
{
    /// <summary>
    /// Stable machine-readable check code.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// Severity: pass, warning, or error.
    /// </summary>
    public required string Severity { get; init; }

    /// <summary>
    /// Human-readable check result.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Expected value when applicable.
    /// </summary>
    public string? Expected { get; init; }

    /// <summary>
    /// Actual value when applicable.
    /// </summary>
    public string? Actual { get; init; }
}

/// <summary>
/// Summary information about a published layer.
/// </summary>
public sealed class PublishedLayerSummary
{
    /// <summary>
    /// Unique layer identifier.
    /// </summary>
    public required int LayerId { get; init; }

    /// <summary>
    /// Display name of the layer.
    /// </summary>
    public required string LayerName { get; init; }

    /// <summary>
    /// Database schema containing the source table.
    /// </summary>
    public required string Schema { get; init; }

    /// <summary>
    /// Source table name.
    /// </summary>
    public required string Table { get; init; }

    /// <summary>
    /// Optional layer description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Geometry type (e.g., "Point", "Polygon").
    /// </summary>
    public required string GeometryType { get; init; }

    /// <summary>
    /// Spatial reference identifier (SRID).
    /// </summary>
    public int Srid { get; init; }

    /// <summary>
    /// Primary key column name.
    /// </summary>
    public string? PrimaryKey { get; init; }

    /// <summary>
    /// Number of published attribute fields.
    /// </summary>
    public int FieldCount { get; init; }

    /// <summary>
    /// Whether the layer is enabled for serving.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Name of the service this layer belongs to.
    /// </summary>
    public required string ServiceName { get; init; }

    /// <summary>
    /// Cached spatial extent of the layer in EPSG:4326 (longitude/latitude), recomputed whenever the
    /// layer is published or its extent is refreshed. Null when the layer has no stored extent yet
    /// (e.g. an empty table). Lets clients frame a map preview on the data instead of the whole world.
    /// </summary>
    public LayerExtentBounds? Extent { get; init; }
}

/// <summary>
/// Axis-aligned bounding box of a layer's data in EPSG:4326 (longitude/latitude degrees).
/// </summary>
public sealed class LayerExtentBounds
{
    /// <summary>Minimum longitude (west).</summary>
    public required double MinX { get; init; }

    /// <summary>Minimum latitude (south).</summary>
    public required double MinY { get; init; }

    /// <summary>Maximum longitude (east).</summary>
    public required double MaxX { get; init; }

    /// <summary>Maximum latitude (north).</summary>
    public required double MaxY { get; init; }
}

/// <summary>
/// Result of refreshing catalog extents for published layers in a service.
/// </summary>
public sealed class LayerExtentRefreshResult
{
    /// <summary>
    /// Service whose layer extents were refreshed.
    /// </summary>
    public required string ServiceName { get; init; }

    /// <summary>
    /// Number of layers processed by the refresh.
    /// </summary>
    public int RefreshedLayerCount { get; init; }

    /// <summary>
    /// Number of processed layers that produced a non-null extent.
    /// </summary>
    public int LayersWithExtent { get; init; }

    /// <summary>
    /// Number of processed layers that produced a null extent.
    /// </summary>
    public int LayersWithoutExtent { get; init; }

    /// <summary>
    /// Whether the containing service extent was recomputed.
    /// </summary>
    public bool ServiceExtentUpdated { get; init; }

    /// <summary>
    /// Per-layer refresh results.
    /// </summary>
    public IReadOnlyList<LayerExtentRefreshLayerResult> Layers { get; init; } = [];
}

/// <summary>
/// Per-layer result of a catalog extent refresh.
/// </summary>
public sealed class LayerExtentRefreshLayerResult
{
    /// <summary>
    /// Refreshed layer identifier.
    /// </summary>
    public int LayerId { get; init; }

    /// <summary>
    /// Layer display name.
    /// </summary>
    public required string LayerName { get; init; }

    /// <summary>
    /// Whether the source table produced a non-null extent.
    /// </summary>
    public bool HasExtent { get; init; }

    /// <summary>
    /// Spatial reference identifier of the refreshed extent.
    /// </summary>
    public int? ExtentSrid { get; init; }
}

/// <summary>
/// Error categories for layer publishing operations.
/// </summary>
public enum LayerPublishingErrorKind
{
    /// <summary>
    /// A validation rule was violated.
    /// </summary>
    Validation,

    /// <summary>
    /// The requested resource was not found.
    /// </summary>
    NotFound,

    /// <summary>
    /// The operation conflicts with existing state.
    /// </summary>
    Conflict,

    /// <summary>
    /// An unclassified error occurred.
    /// </summary>
    Unknown
}

/// <summary>
/// Exception raised when layer publishing fails with a known error category.
/// </summary>
public sealed class LayerPublishingException : Exception
{
    /// <summary>
    /// Gets the error category for this publishing failure.
    /// </summary>
    public LayerPublishingErrorKind ErrorKind { get; }

    /// <summary>
    /// Gets the existing layer identifier associated with a conflict, when known.
    /// </summary>
    public int? LayerId { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="LayerPublishingException"/>.
    /// </summary>
    /// <param name="errorKind">The error category.</param>
    /// <param name="message">A message describing the failure.</param>
    /// <param name="layerId">Existing layer identifier associated with a conflict, when known.</param>
    public LayerPublishingException(LayerPublishingErrorKind errorKind, string message, int? layerId = null)
        : base(message)
    {
        ErrorKind = errorKind;
        LayerId = layerId;
    }
}
