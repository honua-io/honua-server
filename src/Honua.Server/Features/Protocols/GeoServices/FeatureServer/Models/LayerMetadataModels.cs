// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// Information about editor tracking fields on a layer
/// </summary>
public sealed class EditFieldsInfo
{
    /// <summary>
    /// Field name that tracks the creator
    /// </summary>
    public string? CreatorField { get; init; }

    /// <summary>
    /// Field name that tracks the creation date
    /// </summary>
    public string? CreationDateField { get; init; }

    /// <summary>
    /// Field name that tracks the last editor
    /// </summary>
    public string? EditorField { get; init; }

    /// <summary>
    /// Field name that tracks the last edit date
    /// </summary>
    public string? EditDateField { get; init; }
}

/// <summary>
/// Information about the last edit on a layer
/// </summary>
public sealed class EditingInfo
{
    /// <summary>
    /// Unix timestamp (milliseconds) of the last edit, or null if unknown
    /// </summary>
    public long? LastEditDate { get; init; }
}

/// <summary>
/// Unique identifier field metadata per the GeoServices REST specification
/// </summary>
public sealed class UniqueIdFieldInfo
{
    /// <summary>
    /// Name of the unique identifier field
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Whether the field value is system-maintained (auto-generated)
    /// </summary>
    public bool IsSystemMaintained { get; init; }
}

/// <summary>
/// Advanced query capabilities reported in layer metadata per the GeoServices REST spec.
/// Esri clients (ArcGIS Pro, JS API) use this object to determine available query features.
/// </summary>
public sealed class AdvancedQueryCapabilities
{
    /// <summary>
    /// Whether the layer uses standardized queries
    /// </summary>
    public bool UseStandardizedQueries { get; init; } = true;

    /// <summary>
    /// Whether the layer supports statistics queries
    /// </summary>
    public bool SupportsStatistics { get; init; } = true;

    /// <summary>
    /// Whether the layer supports percentile statistics
    /// </summary>
    public bool SupportsPercentileStatistics { get; init; }

    /// <summary>
    /// Whether the layer supports HAVING clause for statistics
    /// </summary>
    public bool SupportsHavingClause { get; init; }

    /// <summary>
    /// Whether the layer supports ordering by fields
    /// </summary>
    public bool SupportsOrderBy { get; init; } = true;

    /// <summary>
    /// Whether the layer supports distinct values
    /// </summary>
    public bool SupportsDistinct { get; init; } = true;

    /// <summary>
    /// Whether the layer supports count distinct queries
    /// </summary>
    public bool SupportsCountDistinct { get; init; } = true;

    /// <summary>
    /// Whether the layer supports pagination
    /// </summary>
    public bool SupportsPagination { get; init; } = true;

    /// <summary>
    /// Whether the layer supports level-of-detail queries
    /// </summary>
    public bool SupportsLod { get; init; }

    /// <summary>
    /// Whether the layer supports querying with LOD spatial reference
    /// </summary>
    public bool SupportsQueryWithLodSR { get; init; }

    /// <summary>
    /// Whether the layer supports TrueCurve geometries
    /// </summary>
    public bool SupportsTrueCurve { get; init; }

    /// <summary>
    /// Whether the layer supports returning geometry centroid
    /// </summary>
    public bool SupportsReturningGeometryCentroid { get; init; }

    /// <summary>
    /// Whether the layer supports returning query extent
    /// </summary>
    public bool SupportsReturningQueryExtent { get; init; } = true;

    /// <summary>
    /// Whether the layer supports distance-based spatial queries
    /// </summary>
    public bool SupportsQueryWithDistance { get; init; } = true;

    /// <summary>
    /// Whether the layer supports SQL expressions in queries
    /// </summary>
    public bool SupportsSqlExpression { get; init; } = true;

    /// <summary>
    /// Whether the layer supports top features query
    /// </summary>
    public bool SupportsTopFeaturesQuery { get; init; }

    /// <summary>
    /// Whether the layer supports batch editing
    /// </summary>
    public bool SupportsBatchEditing { get; init; } = true;

    /// <summary>
    /// Whether the layer supports query analytics
    /// </summary>
    public bool SupportsQueryAnalytic { get; init; }
}

/// <summary>
/// Feature template for creating new features
/// </summary>
public sealed class FeatureTemplate
{
    /// <summary>
    /// Template name
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Template description
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Default drawing tool for the template
    /// </summary>
    public string? DrawingTool { get; init; }

    /// <summary>
    /// Prototype attributes for new features created with this template
    /// </summary>
    public object? Prototype { get; init; }
}
