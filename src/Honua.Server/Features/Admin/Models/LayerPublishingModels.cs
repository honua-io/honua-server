// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Request payload for publishing a PostGIS table as a layer.
/// </summary>
public sealed class PublishLayerRequest
{
    /// <summary>
    /// Schema containing the source table.
    /// </summary>
    [Required]
    [StringLength(63, MinimumLength = 1)]
    public required string Schema { get; init; }

    /// <summary>
    /// Source table name.
    /// </summary>
    [Required]
    [StringLength(63, MinimumLength = 1)]
    public required string Table { get; init; }

    /// <summary>
    /// Display name for the layer.
    /// </summary>
    [Required]
    [StringLength(128, MinimumLength = 1)]
    public required string LayerName { get; init; }

    /// <summary>
    /// Optional layer description.
    /// </summary>
    [StringLength(1000)]
    public string? Description { get; init; }

    /// <summary>
    /// Geometry column name.
    /// </summary>
    [StringLength(64)]
    public string? GeometryColumn { get; init; }

    /// <summary>
    /// Geometry type (Point, Polygon, etc).
    /// </summary>
    [StringLength(64)]
    public string? GeometryType { get; init; }

    /// <summary>
    /// Spatial reference identifier.
    /// </summary>
    [Range(0, int.MaxValue)]
    public int? Srid { get; init; }

    /// <summary>
    /// Primary key field name.
    /// </summary>
    [StringLength(64)]
    public string? PrimaryKey { get; init; }

    /// <summary>
    /// Selected attribute fields to publish (empty means include all).
    /// </summary>
    public IReadOnlyList<string> Fields { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Optional service name (defaults to "default").
    /// </summary>
    [StringLength(64)]
    public string? ServiceName { get; init; }

    /// <summary>
    /// Whether to enable the layer after publishing.
    /// </summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// Request payload for validating a selected table before publishing it as a layer.
/// </summary>
public sealed class ValidateTablePublishRequest
{
    /// <summary>
    /// Schema containing the source table.
    /// </summary>
    [Required]
    [StringLength(63, MinimumLength = 1)]
    public required string Schema { get; init; }

    /// <summary>
    /// Source table name.
    /// </summary>
    [Required]
    [StringLength(63, MinimumLength = 1)]
    public required string Table { get; init; }

    /// <summary>
    /// Optional display name for the layer being prepared.
    /// </summary>
    [StringLength(128, MinimumLength = 1)]
    public string? LayerName { get; init; }

    /// <summary>
    /// Optional target service name.
    /// </summary>
    [StringLength(64)]
    public string? ServiceName { get; init; }

    /// <summary>
    /// Requested target spatial reference identifier.
    /// </summary>
    [Range(0, int.MaxValue)]
    public int? TargetSrid { get; init; }

    /// <summary>
    /// Requested geometry column name.
    /// </summary>
    [StringLength(64)]
    public string? GeometryColumn { get; init; }

    /// <summary>
    /// Requested primary key field name.
    /// </summary>
    [StringLength(64)]
    public string? PrimaryKey { get; init; }

    /// <summary>
    /// Selected attribute fields to publish. Empty means include all.
    /// </summary>
    public IReadOnlyList<string> Fields { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Request payload for enabling/disabling a layer.
/// </summary>
public sealed class LayerEnabledRequest
{
    public bool Enabled { get; init; }
}
