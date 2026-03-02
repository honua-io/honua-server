// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Sdk.Admin.Models;

/// <summary>
/// Request payload for publishing a PostGIS table as a layer.
/// </summary>
public sealed class PublishLayerRequest
{
    /// <summary>
    /// Schema containing the source table.
    /// </summary>
    [JsonPropertyName("schema")]
    public string Schema { get; init; } = string.Empty;

    /// <summary>
    /// Source table name.
    /// </summary>
    [JsonPropertyName("table")]
    public string Table { get; init; } = string.Empty;

    /// <summary>
    /// Display name for the layer.
    /// </summary>
    [JsonPropertyName("layerName")]
    public string LayerName { get; init; } = string.Empty;

    /// <summary>
    /// Optional layer description.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Geometry column name.
    /// </summary>
    [JsonPropertyName("geometryColumn")]
    public string? GeometryColumn { get; init; }

    /// <summary>
    /// Geometry type (Point, Polygon, etc).
    /// </summary>
    [JsonPropertyName("geometryType")]
    public string? GeometryType { get; init; }

    /// <summary>
    /// Spatial reference identifier.
    /// </summary>
    [JsonPropertyName("srid")]
    public int? Srid { get; init; }

    /// <summary>
    /// Primary key field name.
    /// </summary>
    [JsonPropertyName("primaryKey")]
    public string? PrimaryKey { get; init; }

    /// <summary>
    /// Selected attribute fields to publish (empty means include all).
    /// </summary>
    [JsonPropertyName("fields")]
    public IReadOnlyList<string> Fields { get; init; } = [];

    /// <summary>
    /// Optional service name (defaults to "default").
    /// </summary>
    [JsonPropertyName("serviceName")]
    public string? ServiceName { get; init; }

    /// <summary>
    /// Whether to enable the layer after publishing.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// Summary information about a published layer.
/// </summary>
public sealed class PublishedLayerSummary
{
    /// <summary>
    /// Layer identifier.
    /// </summary>
    [JsonPropertyName("layerId")]
    public int LayerId { get; init; }

    /// <summary>
    /// Layer display name.
    /// </summary>
    [JsonPropertyName("layerName")]
    public string LayerName { get; init; } = string.Empty;

    /// <summary>
    /// Database schema containing the source table.
    /// </summary>
    [JsonPropertyName("schema")]
    public string Schema { get; init; } = string.Empty;

    /// <summary>
    /// Source table name.
    /// </summary>
    [JsonPropertyName("table")]
    public string Table { get; init; } = string.Empty;

    /// <summary>
    /// Optional layer description.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Geometry type (Point, Polygon, etc).
    /// </summary>
    [JsonPropertyName("geometryType")]
    public string GeometryType { get; init; } = string.Empty;

    /// <summary>
    /// Spatial reference identifier.
    /// </summary>
    [JsonPropertyName("srid")]
    public int Srid { get; init; }

    /// <summary>
    /// Primary key field name.
    /// </summary>
    [JsonPropertyName("primaryKey")]
    public string? PrimaryKey { get; init; }

    /// <summary>
    /// Number of attribute fields published.
    /// </summary>
    [JsonPropertyName("fieldCount")]
    public int FieldCount { get; init; }

    /// <summary>
    /// Whether the layer is enabled.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    /// <summary>
    /// Service name the layer belongs to.
    /// </summary>
    [JsonPropertyName("serviceName")]
    public string ServiceName { get; init; } = string.Empty;
}

/// <summary>
/// Request payload for enabling/disabling a layer.
/// </summary>
public sealed class LayerEnabledRequest
{
    /// <summary>
    /// Whether the layer should be enabled.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }
}
