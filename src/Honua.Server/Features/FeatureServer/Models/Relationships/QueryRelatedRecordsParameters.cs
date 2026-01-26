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
    /// Response format (json, geojson)
    /// </summary>
    public string F { get; init; } = "json";

    /// <summary>
    /// Starting offset for pagination
    /// </summary>
    public int? ResultOffset { get; init; }

    /// <summary>
    /// Maximum number of related records to return
    /// </summary>
    public int? ResultRecordCount { get; init; }
}
