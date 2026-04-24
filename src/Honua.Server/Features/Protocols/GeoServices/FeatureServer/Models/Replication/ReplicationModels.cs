// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// Request model for createReplica endpoint.
/// </summary>
public sealed class CreateReplicaRequest
{
    /// <summary>
    /// Name for the replica.
    /// </summary>
    [JsonPropertyName("replicaName")]
    public string? ReplicaName { get; set; }

    /// <summary>
    /// Comma-separated layer IDs to include in the replica.
    /// </summary>
    [JsonPropertyName("layers")]
    public string? Layers { get; set; }

    /// <summary>
    /// Geometry filter for the replica extent.
    /// </summary>
    [JsonPropertyName("geometry")]
    public string? Geometry { get; set; }

    /// <summary>
    /// Return attachments with the replica.
    /// </summary>
    [JsonPropertyName("returnAttachments")]
    public bool ReturnAttachments { get; set; }

    /// <summary>
    /// Sync model: none, perLayer, perReplica.
    /// </summary>
    [JsonPropertyName("syncModel")]
    public string? SyncModel { get; set; }

    /// <summary>
    /// Data format: json, sqlite, filegdb.
    /// </summary>
    [JsonPropertyName("dataFormat")]
    public string? DataFormat { get; set; }

    /// <summary>
    /// Output format parameter.
    /// </summary>
    [JsonPropertyName("f")]
    public string? F { get; set; }
}

/// <summary>
/// Response model for createReplica endpoint.
/// </summary>
public sealed class CreateReplicaResponse
{
    /// <summary>
    /// Unique identifier for the created replica.
    /// </summary>
    [JsonPropertyName("replicaID")]
    public string? ReplicaId { get; set; }

    /// <summary>
    /// Name of the replica.
    /// </summary>
    [JsonPropertyName("replicaName")]
    public string? ReplicaName { get; set; }

    /// <summary>
    /// Sync model used for this replica.
    /// </summary>
    [JsonPropertyName("syncModel")]
    public string? SyncModel { get; set; }

    /// <summary>
    /// Layers included in the replica.
    /// </summary>
    [JsonPropertyName("layers")]
    public ReplicaLayerInfo[]? Layers { get; set; }

    /// <summary>
    /// Creation date as Unix timestamp in milliseconds.
    /// </summary>
    [JsonPropertyName("creationDate")]
    public long CreationDate { get; set; }
}

/// <summary>
/// Summary metadata returned by the replicas resource.
/// </summary>
public sealed class ReplicaSummary
{
    /// <summary>
    /// Name of the replica.
    /// </summary>
    [JsonPropertyName("replicaName")]
    public string? ReplicaName { get; set; }

    /// <summary>
    /// Unique identifier for the replica.
    /// </summary>
    [JsonPropertyName("replicaID")]
    public string? ReplicaId { get; set; }

    /// <summary>
    /// Timestamp of the last successful synchronization, in Unix milliseconds.
    /// </summary>
    [JsonPropertyName("lastSyncDate")]
    public long? LastSyncDate { get; set; }
}

/// <summary>
/// Detailed metadata returned by the replica info resource.
/// </summary>
public sealed class ReplicaInfoResponse
{
    /// <summary>
    /// Name of the replica.
    /// </summary>
    [JsonPropertyName("replicaName")]
    public string? ReplicaName { get; set; }

    /// <summary>
    /// Unique identifier for the replica.
    /// </summary>
    [JsonPropertyName("replicaID")]
    public string? ReplicaId { get; set; }

    /// <summary>
    /// Synchronization model used by the replica.
    /// </summary>
    [JsonPropertyName("syncModel")]
    public string? SyncModel { get; set; }

    /// <summary>
    /// Replica-wide server generation for per-replica sync models.
    /// </summary>
    [JsonPropertyName("replicaServerGen")]
    public long? ReplicaServerGen { get; set; }

    /// <summary>
    /// Layer generation metadata encoded using the ArcGIS replicas resource shape.
    /// </summary>
    [JsonPropertyName("layerServerGens")]
    public string? LayerServerGens { get; set; }

    /// <summary>
    /// Timestamp when the replica was created, in Unix milliseconds.
    /// </summary>
    [JsonPropertyName("creationDate")]
    public long CreationDate { get; set; }

    /// <summary>
    /// Timestamp of the last successful synchronization, in Unix milliseconds.
    /// </summary>
    [JsonPropertyName("lastSyncDate")]
    public long LastSyncDate { get; set; }

    /// <summary>
    /// Layers included in the replica definition.
    /// </summary>
    [JsonPropertyName("layers")]
    public ReplicaInfoLayer[]? Layers { get; set; }
}

/// <summary>
/// Layer metadata returned in the replica info resource.
/// </summary>
public sealed class ReplicaInfoLayer
{
    /// <summary>
    /// Layer identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>
    /// Replica query option applied to the layer.
    /// </summary>
    [JsonPropertyName("queryOption")]
    public string QueryOption { get; set; } = "all";

    /// <summary>
    /// Indicates whether the replica uses the layer geometry.
    /// </summary>
    [JsonPropertyName("useGeometry")]
    public bool UseGeometry { get; set; } = true;

    /// <summary>
    /// Indicates whether related records are included for the layer.
    /// </summary>
    [JsonPropertyName("includeRelated")]
    public bool IncludeRelated { get; set; }

    /// <summary>
    /// Definition query associated with the layer replica entry.
    /// </summary>
    [JsonPropertyName("where")]
    public string Where { get; set; } = string.Empty;
}

/// <summary>
/// Layer generation metadata encoded into the replica info response.
/// </summary>
public sealed class ReplicaInfoLayerServerGeneration
{
    /// <summary>
    /// Layer identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>
    /// Current server generation recorded for the layer.
    /// </summary>
    [JsonPropertyName("serverGen")]
    public long ServerGen { get; set; }

    /// <summary>
    /// Current sibling generation recorded for the layer.
    /// </summary>
    [JsonPropertyName("serverSibGen")]
    public long ServerSibGen { get; set; }
}

/// <summary>
/// Layer information within a replica.
/// </summary>
public sealed class ReplicaLayerInfo
{
    /// <summary>
    /// Layer identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>
    /// Server generation number at creation time.
    /// </summary>
    [JsonPropertyName("serverGen")]
    public long ServerGen { get; set; }
}

/// <summary>
/// Request model for extractChanges endpoint.
/// </summary>
public sealed class ExtractChangesRequest
{
    /// <summary>
    /// Replica identifier to extract changes for.
    /// </summary>
    [JsonPropertyName("replicaID")]
    public string? ReplicaId { get; set; }

    /// <summary>
    /// Layers for which to extract changes.
    /// </summary>
    [JsonPropertyName("layers")]
    public string? Layers { get; set; }

    /// <summary>
    /// Output format parameter.
    /// </summary>
    [JsonPropertyName("f")]
    public string? F { get; set; }
}

/// <summary>
/// Response model for extractChanges endpoint.
/// </summary>
public sealed class ExtractChangesResponse
{
    /// <summary>
    /// Indicates if the operation was successful.
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>
    /// Replica identifier.
    /// </summary>
    [JsonPropertyName("replicaID")]
    public string? ReplicaId { get; set; }

    /// <summary>
    /// Layer-level change results.
    /// </summary>
    [JsonPropertyName("layerChanges")]
    public LayerChanges[]? LayerChanges { get; set; }

    /// <summary>
    /// Current server generation number.
    /// </summary>
    [JsonPropertyName("serverGen")]
    public long ServerGen { get; set; }

    /// <summary>
    /// Minimum generation number in the returned change set.
    /// </summary>
    [JsonPropertyName("minServerGen")]
    public long MinServerGen { get; set; }

    /// <summary>
    /// Maximum generation number in the returned change set.
    /// </summary>
    [JsonPropertyName("maxServerGen")]
    public long MaxServerGen { get; set; }
}

/// <summary>
/// Change information for a single layer within a replica sync.
/// </summary>
public sealed class LayerChanges
{
    /// <summary>
    /// Layer identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>
    /// Number of features added since last sync.
    /// </summary>
    [JsonPropertyName("adds")]
    public int Adds { get; set; }

    /// <summary>
    /// Number of features updated since last sync.
    /// </summary>
    [JsonPropertyName("updates")]
    public int Updates { get; set; }

    /// <summary>
    /// Number of features deleted since last sync.
    /// </summary>
    [JsonPropertyName("deletes")]
    public int Deletes { get; set; }

    /// <summary>
    /// Actual feature data for inserts since last sync.
    /// </summary>
    [JsonPropertyName("addFeatures")]
    public GeoServicesFeature[]? AddFeatures { get; set; }

    /// <summary>
    /// Actual feature data for updates since last sync.
    /// </summary>
    [JsonPropertyName("updateFeatures")]
    public GeoServicesFeature[]? UpdateFeatures { get; set; }

    /// <summary>
    /// Object IDs of features deleted since last sync.
    /// </summary>
    [JsonPropertyName("deleteIds")]
    public long[]? DeleteIds { get; set; }
}

/// <summary>
/// Request model for synchronizeReplica endpoint.
/// </summary>
public sealed class SynchronizeReplicaRequest
{
    /// <summary>
    /// Replica identifier.
    /// </summary>
    [JsonPropertyName("replicaID")]
    public string? ReplicaId { get; set; }

    /// <summary>
    /// Sync direction: upload, download, bidirectional.
    /// </summary>
    [JsonPropertyName("syncDirection")]
    public string? SyncDirection { get; set; }

    /// <summary>
    /// Edits to apply during synchronization (JSON array).
    /// </summary>
    [JsonPropertyName("edits")]
    public string? Edits { get; set; }

    /// <summary>
    /// Output format parameter.
    /// </summary>
    [JsonPropertyName("f")]
    public string? F { get; set; }
}

/// <summary>
/// Response model for synchronizeReplica endpoint.
/// </summary>
public sealed class SynchronizeReplicaResponse
{
    /// <summary>
    /// Indicates if the operation was successful.
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>
    /// Replica identifier.
    /// </summary>
    [JsonPropertyName("replicaID")]
    public string? ReplicaId { get; set; }

    /// <summary>
    /// Sync direction used.
    /// </summary>
    [JsonPropertyName("syncDirection")]
    public string? SyncDirection { get; set; }

    /// <summary>
    /// Current server generation number after synchronization.
    /// </summary>
    [JsonPropertyName("serverGen")]
    public long ServerGen { get; set; }
}

/// <summary>
/// Request model for unRegisterReplica endpoint.
/// </summary>
public sealed class UnRegisterReplicaRequest
{
    /// <summary>
    /// Replica identifier to unregister.
    /// </summary>
    [JsonPropertyName("replicaID")]
    public string? ReplicaId { get; set; }

    /// <summary>
    /// Output format parameter.
    /// </summary>
    [JsonPropertyName("f")]
    public string? F { get; set; }
}

/// <summary>
/// Generic success response with a boolean success flag.
/// </summary>
public sealed class SuccessResponse
{
    /// <summary>
    /// Indicates if the operation was successful.
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; }
}
