// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.FeatureServer.Models;

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
    /// Response format (json, pjson)
    /// </summary>
    public string F { get; init; } = "json";

    /// <summary>
    /// Output spatial reference for response geometry.
    /// </summary>
    public string? OutSr { get; init; }

    /// <summary>
    /// Whether to include Z values in output geometry.
    /// </summary>
    public bool ReturnZ { get; init; }

    /// <summary>
    /// Whether to include M values in output geometry.
    /// </summary>
    public bool ReturnM { get; init; }

    /// <summary>
    /// Geometry coordinate precision override.
    /// </summary>
    public int? GeometryPrecision { get; init; }

    /// <summary>
    /// Maximum allowable offset for geometry generalization.
    /// </summary>
    public double? MaxAllowableOffset { get; init; }

    /// <summary>
    /// Geodatabase version for versioned data sources.
    /// </summary>
    public string? GdbVersion { get; init; }

    /// <summary>
    /// SQL format mode.
    /// </summary>
    public string? SqlFormat { get; init; }

    /// <summary>
    /// Historic moment timestamp in epoch milliseconds.
    /// </summary>
    public long? HistoricMoment { get; init; }

    /// <summary>
    /// Whether to return true-curve geometry payloads.
    /// </summary>
    public bool ReturnTrueCurves { get; init; }

    /// <summary>
    /// Starting offset for pagination
    /// </summary>
    public int? ResultOffset { get; init; }

    /// <summary>
    /// Maximum number of related records to return
    /// </summary>
    public int? ResultRecordCount { get; init; }
}
