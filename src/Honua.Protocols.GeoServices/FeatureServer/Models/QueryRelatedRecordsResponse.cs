// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// Response model for queryRelatedRecords endpoint.
/// Matches the Esri queryRelatedRecords response contract: field/geometry
/// metadata lives at the top level and each group's <c>relatedRecords</c> is a
/// flat array of records (see Esri REST API queryRelatedRecords).
/// </summary>
public sealed class QueryRelatedRecordsResponse
{
    /// <summary>
    /// Geometry type for related records that include geometry (layers only).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GeometryType { get; init; }

    /// <summary>
    /// Spatial reference for returned geometries (layers only).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GeoServicesSpatialReference? SpatialReference { get; init; }

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
    /// Object ID field name for the related records.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ObjectIdFieldName { get; init; }

    /// <summary>
    /// Field definitions for the attributes returned in the related records.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GeoServicesFieldInfo[]? Fields { get; init; }

    /// <summary>
    /// Array of related record groups, one per source object ID
    /// </summary>
    public required RelatedRecordGroup[] RelatedRecordGroups { get; init; }
}
