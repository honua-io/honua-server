// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// Response model for FeatureServer service metadata endpoint
/// </summary>
public sealed class FeatureServerResponse
{
    // No ArcGIS Server version (currentVersion/fullVersion) is advertised. Honua is an
    // independent, Esri-compatible server and must not impersonate a specific ArcGIS Server
    // release. Do NOT add a currentVersion/fullVersion field (guarded by NoArcGisServerVersionTests).

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

    /// <summary>
    /// Whether the service is sync-enabled (supports offline replicas:
    /// createReplica / synchronizeReplica / extractChanges). Emitted so Esri
    /// clients (ArcGIS API for Python <c>FeatureLayerCollection</c>, ArcGIS Pro
    /// offline workflows) discover the replication surface the server exposes.
    /// </summary>
    public bool SyncEnabled { get; init; }

    /// <summary>
    /// Service-level synchronization capabilities (supported sync directions, sync models,
    /// rollbackOnFailure, and <c>supportedSyncDataOptions</c>). Emitted only when the service is
    /// sync-enabled so Esri clients discover the offline replica behavior the server honors before they
    /// attempt createReplica / synchronizeReplica; null (omitted) otherwise (#2136).
    /// </summary>
    public FeatureServerSyncCapabilities? SyncCapabilities { get; init; }

    /// <summary>
    /// Whether the service exposes branch-versioned data. Emitted so Esri clients discover the
    /// VersionManagementServer surface (#1272, ADR-0051). True only when the active provider supports
    /// branch versioning (Postgres) and the Enterprise branch-versioning entitlement is active.
    /// </summary>
    public bool HasVersionedData { get; init; }

    /// <summary>
    /// Whether the service's data is branch-versioned. Mirrors Esri's service-level
    /// <c>isDataVersioned</c> flag; tracks <see cref="HasVersionedData"/>.
    /// </summary>
    public bool IsDataVersioned { get; init; }

    /// <summary>
    /// Whether the service supports Esri-style branch versioning over the VersionManagementServer.
    /// </summary>
    public bool SupportsBranchVersioning { get; init; }

    /// <summary>
    /// Relative URL of the companion VersionManagementServer when branch versioning is available;
    /// null otherwise. Emitted so Esri clients can locate the version lifecycle surface.
    /// </summary>
    public string? VersionManagementServerUrl { get; init; }
}
