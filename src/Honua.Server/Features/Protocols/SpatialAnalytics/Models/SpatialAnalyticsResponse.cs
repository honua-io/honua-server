// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Protocols.SpatialAnalytics.Models;

/// <summary>
/// GeoJSON-shaped FeatureCollection returned by the spatial analytics endpoints.
/// Used by both the FeatureServer mirror and the OGC Features mirror so clients
/// receive identical payloads regardless of route family.
/// </summary>
internal sealed class SpatialAnalyticsFeatureCollection
{
    /// <summary>
    /// Constant GeoJSON discriminator.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "FeatureCollection";

    /// <summary>
    /// Features in the collection (one row per output record from the analytics query).
    /// </summary>
    [JsonPropertyName("features")]
    public SpatialAnalyticsFeature[] Features { get; set; } = [];

    /// <summary>
    /// Number of features returned, matching the OGC Features convention.
    /// </summary>
    [JsonPropertyName("numberReturned")]
    public int NumberReturned { get; set; }

    /// <summary>
    /// Analytics-specific metadata such as overflow flags, input counts and
    /// requested parameter echoes.
    /// </summary>
    [JsonPropertyName("metadata")]
    public SpatialAnalyticsMetadata? Metadata { get; set; }
}

/// <summary>
/// Single GeoJSON feature produced by an analytics operation. Geometry may be
/// omitted for per-feature cluster output when the source row did not include it.
/// </summary>
internal sealed class SpatialAnalyticsFeature
{
    /// <summary>
    /// Constant GeoJSON discriminator.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "Feature";

    /// <summary>
    /// Inline GeoJSON geometry. Uses the local <see cref="SpatialAnalyticsGeometry"/>
    /// shape so the slice stays isolated from the OGC Features models while still
    /// emitting coordinate arrays as raw JSON for AOT safety.
    /// </summary>
    [JsonPropertyName("geometry")]
    public SpatialAnalyticsGeometry? Geometry { get; set; }

    /// <summary>
    /// Analytics attributes (cluster id, count, statistics, carry fields).
    /// </summary>
    [JsonPropertyName("properties")]
    public Dictionary<string, object?> Properties { get; set; } = [];
}

/// <summary>
/// Diagnostic metadata attached to each analytics response.
/// </summary>
internal sealed class SpatialAnalyticsMetadata
{
    /// <summary>
    /// Name of the analytics operation (cluster, spatial-join, buffer-aggregate, density).
    /// </summary>
    [JsonPropertyName("operation")]
    public string Operation { get; set; } = string.Empty;

    /// <summary>
    /// True when the SQL-side <c>LIMIT n+1</c> detected more input rows than the
    /// caller asked to process and the response was truncated.
    /// </summary>
    [JsonPropertyName("inputTruncated")]
    public bool InputTruncated { get; set; }

    /// <summary>
    /// True when the SQL-side <c>LIMIT n+1</c> detected more output rows than the
    /// caller asked to return.
    /// </summary>
    [JsonPropertyName("resultTruncated")]
    public bool ResultTruncated { get; set; }

    /// <summary>
    /// Maximum input feature count the operation was configured to process.
    /// </summary>
    [JsonPropertyName("maxInputFeatures")]
    public int MaxInputFeatures { get; set; }

    /// <summary>
    /// Maximum result row count the operation was configured to return.
    /// </summary>
    [JsonPropertyName("maxOutputRows")]
    public int? MaxOutputRows { get; set; }
}
