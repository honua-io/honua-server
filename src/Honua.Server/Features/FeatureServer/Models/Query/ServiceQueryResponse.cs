// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Shared.Models;

namespace Honua.Server.Features.FeatureServer.Models;

/// <summary>
/// Response for a service-level FeatureServer query containing per-layer results.
/// </summary>
public sealed class ServiceQueryResponse
{
    /// <summary>
    /// Query results grouped by layer.
    /// </summary>
    [JsonPropertyName("layers")]
    public ServiceQueryLayerResponse[] Layers { get; init; } = [];
}

/// <summary>
/// Query response payload for a single layer inside a service-level query response.
/// </summary>
public sealed class ServiceQueryLayerResponse
{
    /// <summary>
    /// Layer identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    /// <summary>
    /// Geometry type for the features returned by the query.
    /// </summary>
    public string? GeometryType { get; init; }

    /// <summary>
    /// Spatial reference for returned geometries.
    /// </summary>
    public GeoServicesSpatialReference? SpatialReference { get; init; }

    /// <summary>
    /// Name of the display field for the result set.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayFieldName { get; init; }

    /// <summary>
    /// Field metadata for attributes returned in the result set.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GeoServicesFieldInfo[]? Fields { get; init; }

    /// <summary>
    /// Whether returned geometries include Z values.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    [JsonPropertyName("hasZ")]
    public bool HasZ { get; init; }

    /// <summary>
    /// Whether returned geometries include M values.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    [JsonPropertyName("hasM")]
    public bool HasM { get; init; }

    /// <summary>
    /// Object ID field name for the layer.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ObjectIdFieldName { get; init; }

    /// <summary>
    /// Object IDs returned by the query when <c>returnIdsOnly=true</c>.
    /// </summary>
    public long[]? ObjectIds { get; init; }

    /// <summary>
    /// Total count returned by the query when <c>returnCountOnly=true</c>.
    /// </summary>
    public long? Count { get; init; }

    /// <summary>
    /// Extent returned by the query when <c>returnExtentOnly=true</c>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExtentInfo? Extent { get; init; }

    /// <summary>
    /// Unique value field name.
    /// </summary>
    public string? UniqueIdField { get; init; }

    /// <summary>
    /// Global ID field name.
    /// </summary>
    public string? GlobalIdFieldName { get; init; }

    /// <summary>
    /// Features returned by the query.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GeoServicesFeature[]? Features { get; init; }

    /// <summary>
    /// Whether the transfer limit was exceeded.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ExceededTransferLimit { get; init; }
}
