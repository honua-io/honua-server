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
}

/// <summary>
/// Summary information about a published layer.
/// </summary>
public sealed class PublishedLayerSummary
{
    public required int LayerId { get; init; }

    public required string LayerName { get; init; }

    public required string Schema { get; init; }

    public required string Table { get; init; }

    public string? Description { get; init; }

    public required string GeometryType { get; init; }

    public int Srid { get; init; }

    public string? PrimaryKey { get; init; }

    public int FieldCount { get; init; }

    public bool Enabled { get; init; }

    public required string ServiceName { get; init; }
}

/// <summary>
/// Error categories for layer publishing operations.
/// </summary>
public enum LayerPublishingErrorKind
{
    Validation,
    NotFound,
    Conflict,
    Unknown
}

/// <summary>
/// Exception raised when layer publishing fails with a known error category.
/// </summary>
public sealed class LayerPublishingException : Exception
{
    public LayerPublishingErrorKind ErrorKind { get; }

    public LayerPublishingException(LayerPublishingErrorKind errorKind, string message)
        : base(message)
    {
        ErrorKind = errorKind;
    }
}
