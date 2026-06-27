// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

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
    /// Current server generation number at creation time.
    /// </summary>
    [JsonPropertyName("serverGen")]
    public long ServerGen { get; set; }

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
    /// When true, each layer's uploaded edits are applied atomically: a single failing edit rolls back
    /// that layer's whole batch, leaving the server state unchanged. Mirrors the Esri
    /// <c>rollbackOnFailure</c> sync parameter. Defaults to false (best-effort per-row apply) (#2136).
    /// </summary>
    [JsonPropertyName("rollbackOnFailure")]
    public bool RollbackOnFailure { get; set; }

    /// <summary>
    /// Output format parameter.
    /// </summary>
    [JsonPropertyName("f")]
    public string? F { get; set; }
}

/// <summary>
/// Service-level synchronization capabilities advertised on the FeatureServer metadata resource so
/// Esri clients (ArcGIS API for Python, ArcGIS Pro, Maps SDKs) discover the offline replica behavior
/// the server actually honors before they attempt createReplica / synchronizeReplica (#2136). Emitted
/// only when the service is sync-enabled.
/// </summary>
public sealed class FeatureServerSyncCapabilities
{
    /// <summary>
    /// Whether replica creation/synchronization runs asynchronously (replica jobs). Honua synchronizes
    /// inline, so this is false.
    /// </summary>
    [JsonPropertyName("supportsAsync")]
    public bool SupportsAsync { get; init; }

    /// <summary>
    /// Whether existing data can be registered as a replica without an extract. Not supported.
    /// </summary>
    [JsonPropertyName("supportsRegisteringExistingData")]
    public bool SupportsRegisteringExistingData { get; init; }

    /// <summary>
    /// Whether the client may choose the sync direction (download / upload / bidirectional). Honua
    /// honors <c>syncDirection</c>, so this is true.
    /// </summary>
    [JsonPropertyName("supportsSyncDirectionControl")]
    public bool SupportsSyncDirectionControl { get; init; } = true;

    /// <summary>
    /// Whether per-layer sync (a replica spanning a subset of layers with per-layer generations) is
    /// supported. Honua supports the <c>perLayer</c> sync model, so this is true.
    /// </summary>
    [JsonPropertyName("supportsPerLayerSync")]
    public bool SupportsPerLayerSync { get; init; } = true;

    /// <summary>
    /// Whether per-replica sync (a single replica-wide generation) is supported. True.
    /// </summary>
    [JsonPropertyName("supportsPerReplicaSync")]
    public bool SupportsPerReplicaSync { get; init; } = true;

    /// <summary>
    /// Whether the <c>none</c> sync model (snapshot replicas that never sync back) is supported. Not
    /// supported.
    /// </summary>
    [JsonPropertyName("supportsSyncModelNone")]
    public bool SupportsSyncModelNone { get; init; }

    /// <summary>
    /// Whether uploaded edits can be applied atomically via <c>rollbackOnFailure</c>. True (#2136).
    /// </summary>
    [JsonPropertyName("supportsRollbackOnFailure")]
    public bool SupportsRollbackOnFailure { get; init; } = true;

    /// <summary>
    /// Whether attachment sync direction can be controlled independently. Attachment sync direction
    /// control is not supported.
    /// </summary>
    [JsonPropertyName("supportsAttachmentsSyncDirection")]
    public bool SupportsAttachmentsSyncDirection { get; init; }

    /// <summary>
    /// Bitmask of the sync data options the service supports (Esri <c>syncDataOptions</c>). Honua syncs
    /// feature edits (bit 1, <c>esriSyncDataOptionsFeatures</c>); attachment-edit sync is not part of
    /// the replica upload model, so the attachments bit is not advertised.
    /// </summary>
    [JsonPropertyName("supportedSyncDataOptions")]
    public int SupportedSyncDataOptions { get; init; } = 1;
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

    /// <summary>
    /// Server-to-client changes assembled for a download (or bidirectional) sync: the per-layer
    /// adds/updates/deletes committed on the server since the replica's last sync generation. Omitted
    /// (null) for upload-only syncs, which carry no download payload.
    /// </summary>
    [JsonPropertyName("edits")]
    public LayerChanges[]? Edits { get; set; }

    /// <summary>
    /// Per-layer server generations advanced by this synchronization, encoded using the ArcGIS replicas
    /// resource shape. Lets a per-layer replica record the generation it has now received for each layer.
    /// Omitted (null) for upload-only syncs.
    /// </summary>
    [JsonPropertyName("layerServerGens")]
    public ReplicaInfoLayerServerGeneration[]? LayerServerGens { get; set; }

    /// <summary>
    /// Number of uploaded add operations applied during this synchronization. Omitted (null) for
    /// download-only syncs that carry no upload.
    /// </summary>
    [JsonPropertyName("appliedAdds")]
    public int? AppliedAdds { get; set; }

    /// <summary>
    /// Number of uploaded update operations applied during this synchronization.
    /// </summary>
    [JsonPropertyName("appliedUpdates")]
    public int? AppliedUpdates { get; set; }

    /// <summary>
    /// Number of uploaded delete operations applied during this synchronization.
    /// </summary>
    [JsonPropertyName("appliedDeletes")]
    public int? AppliedDeletes { get; set; }

    /// <summary>
    /// Conflicts detected while applying the uploaded edits, when any. A non-empty list does not by
    /// itself fail the sync under last-write-wins; durable conflict records are written for review
    /// when supported (#1287).
    /// </summary>
    [JsonPropertyName("conflicts")]
    public SynchronizeReplicaConflict[]? Conflicts { get; set; }
}

/// <summary>
/// Summary of a single conflict detected while applying uploaded replica edits.
/// </summary>
public sealed class SynchronizeReplicaConflict
{
    /// <summary>Service-local layer id of the conflicting feature.</summary>
    [JsonPropertyName("layerId")]
    public int LayerId { get; set; }

    /// <summary>Stable object id of the conflicting feature.</summary>
    [JsonPropertyName("objectId")]
    public long ObjectId { get; set; }

    /// <summary>
    /// Conflict classification ordinal (see <c>ReplicaConflictType</c>): 0=attribute, 1=geometry,
    /// 2=delete-update, 3=update-delete, 4=duplicate-insert, 5=attachment, 6=relationship.
    /// </summary>
    [JsonPropertyName("conflictType")]
    public int ConflictType { get; set; }

    /// <summary>
    /// Whether the client edit was still applied (last-write-wins) despite the conflict.
    /// </summary>
    [JsonPropertyName("applied")]
    public bool Applied { get; set; }

    /// <summary>
    /// Durable conflict-record id when a record was written for review; null when conflict review is
    /// unsupported by the provider.
    /// </summary>
    [JsonPropertyName("conflictId")]
    public string? ConflictId { get; set; }
}

/// <summary>
/// A single layer's uploaded edits within the <c>synchronizeReplica</c> <c>edits</c> parameter. The
/// ArcGIS sync <c>edits</c> payload is a JSON array of these per-layer objects; each carries Esri-JSON
/// adds/updates and delete object ids for one layer.
/// </summary>
public sealed class SynchronizeReplicaLayerEdits
{
    /// <summary>Service-local layer id the edits target.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Features to add (Esri JSON).</summary>
    [JsonPropertyName("adds")]
    public GeoServicesFeature[]? Adds { get; set; }

    /// <summary>Features to update (Esri JSON; each must carry its object id attribute).</summary>
    [JsonPropertyName("updates")]
    public GeoServicesFeature[]? Updates { get; set; }

    /// <summary>Object ids to delete.</summary>
    [JsonPropertyName("deletes")]
    public long[]? Deletes { get; set; }
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
