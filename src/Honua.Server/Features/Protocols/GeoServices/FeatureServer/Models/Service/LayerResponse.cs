// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// Response model for individual layer metadata endpoint
/// </summary>
public sealed class LayerResponse
{
    /// <summary>
    /// Current version of the service
    /// </summary>
    public double CurrentVersion { get; init; } = 10.81;

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
    /// Temporal metadata for time-aware layers.
    /// </summary>
    public FeatureServerTimeInfo? TimeInfo { get; init; }

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
    /// Unique identifier field metadata per the GeoServices REST spec
    /// </summary>
    public UniqueIdFieldInfo? UniqueIdField { get; init; }

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
    public LayerRelationshipInfo[] Relationships { get; init; } = [];

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

    /// <summary>
    /// Whether the layer allows geometry updates on features
    /// </summary>
    public bool AllowGeometryUpdates { get; init; } = true;

    /// <summary>
    /// Information about editor tracking fields (null when editor tracking is not configured)
    /// </summary>
    public EditFieldsInfo? EditFieldsInfo { get; init; }

    /// <summary>
    /// Information about last edit timestamps
    /// </summary>
    public EditingInfo? EditingInfo { get; init; }

    /// <summary>
    /// Feature templates for creating new features
    /// </summary>
    public FeatureTemplate[] Templates { get; init; } = [];

    /// <summary>
    /// Advanced query capabilities per the GeoServices REST spec.
    /// Esri clients (ArcGIS Pro, JS API) check this object to enable pagination, statistics, orderBy, etc.
    /// </summary>
    public AdvancedQueryCapabilities? AdvancedQueryCapabilities { get; init; }
}
