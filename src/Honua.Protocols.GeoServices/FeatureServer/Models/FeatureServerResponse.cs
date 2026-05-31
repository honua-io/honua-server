// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// Response model for FeatureServer service metadata endpoint
/// </summary>
public sealed class FeatureServerResponse
{
    /// <summary>
    /// Current version of the service
    /// </summary>
    public double CurrentVersion { get; init; } = 10.81;

    /// <summary>
    /// Name of the service
    /// </summary>
    public required string ServiceName { get; init; }

    /// <summary>
    /// Human-readable description of the service
    /// </summary>
    public required string ServiceDescription { get; init; }

    /// <summary>
    /// Layers available in this service
    /// </summary>
    public required LayerInfo[] Layers { get; init; }

    /// <summary>
    /// Tables available in this service (typically empty for basic implementation)
    /// </summary>
    public object[] Tables { get; init; } = [];

    /// <summary>
    /// Default spatial reference system for the service
    /// </summary>
    public required SpatialReferenceInfo SpatialReference { get; init; }

    /// <summary>
    /// Initial extent of the service
    /// </summary>
    public ExtentInfo? InitialExtent { get; init; }

    /// <summary>
    /// Full extent of all data in the service
    /// </summary>
    public ExtentInfo? FullExtent { get; init; }

    /// <summary>
    /// Units used for measurements
    /// </summary>
    public string Units { get; init; } = "esriMeters";

    /// <summary>
    /// Supported query formats. The GeoServices spec defines this as a
    /// comma-delimited string (the ArcGIS Maps SDK for JavaScript calls
    /// <c>value.split(",")</c> on it), so it is serialized as one.
    /// </summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(CommaDelimitedStringArrayConverter))]
    public string[] SupportedQueryFormats { get; init; } = ["JSON", "GeoJSON"];

    /// <summary>
    /// Service capabilities
    /// </summary>
    public string Capabilities { get; init; } = "Query,Extract";

    /// <summary>
    /// Maximum number of records returned in a single query
    /// </summary>
    public int MaxRecordCount { get; init; } = 1000;

    /// <summary>
    /// Whether the service supports advanced queries
    /// </summary>
    public bool SupportsAdvancedQueries { get; init; } = true;

    /// <summary>
    /// Whether the service supports statistics queries
    /// </summary>
    public bool SupportsStatistics { get; init; } = true;

    /// <summary>
    /// Whether the service supports spatial queries
    /// </summary>
    public bool HasGeometryProperties { get; init; } = true;

    /// <summary>
    /// Object ID field name used across the service
    /// </summary>
    public string ObjectIdField { get; init; } = FieldNames.ObjectId;

    /// <summary>
    /// Global ID field name (if used)
    /// </summary>
    public string? GlobalIdField { get; init; }

    /// <summary>
    /// Type ID field name (if used for symbology)
    /// </summary>
    public string? TypeIdField { get; init; }

    /// <summary>
    /// Fields common across all layers
    /// </summary>
    public GeoServicesFieldInfo[] Fields { get; init; } = [];

    /// <summary>
    /// Relationships between layers (typically empty for basic implementation)
    /// </summary>
    public object[] Relationships { get; init; } = [];

    /// <summary>
    /// Whether the service allows geometry updates on features
    /// </summary>
    public bool AllowGeometryUpdates { get; init; } = true;
}
