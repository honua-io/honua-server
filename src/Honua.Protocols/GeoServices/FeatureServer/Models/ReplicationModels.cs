// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

public sealed class CreateReplicaRequest
{
    [JsonPropertyName("replicaName")]
    public string? ReplicaName { get; set; }

    [JsonPropertyName("layers")]
    public string? Layers { get; set; }

    [JsonPropertyName("geometry")]
    public string? Geometry { get; set; }

    [JsonPropertyName("returnAttachments")]
    public bool ReturnAttachments { get; set; }

    [JsonPropertyName("syncModel")]
    public string? SyncModel { get; set; }

    [JsonPropertyName("dataFormat")]
    public string? DataFormat { get; set; }

    [JsonPropertyName("f")]
    public string? F { get; set; }
}

public sealed class CreateReplicaResponse
{
    [JsonPropertyName("replicaID")]
    public string? ReplicaId { get; set; }

    [JsonPropertyName("replicaName")]
    public string? ReplicaName { get; set; }

    [JsonPropertyName("syncModel")]
    public string? SyncModel { get; set; }

    [JsonPropertyName("serverGen")]
    public long ServerGen { get; set; }

    [JsonPropertyName("layers")]
    public ReplicaLayerInfo[]? Layers { get; set; }

    [JsonPropertyName("creationDate")]
    public long CreationDate { get; set; }

    // Transport is always embedded, and a URL request is answered as embedded, because this server publishes no replica files.
    [JsonPropertyName("transportType")]
    public string? TransportType { get; set; }

    [JsonPropertyName("responseType")]
    public string? ResponseType { get; set; }

    [JsonPropertyName("layerServerGens")]
    public ReplicaInfoLayerServerGeneration[]? LayerServerGens { get; set; }

    [JsonPropertyName("exceededTransferLimit")]
    public bool? ExceededTransferLimit { get; set; }
}

public sealed class ReplicaSummary
{
    [JsonPropertyName("replicaName")]
    public string? ReplicaName { get; set; }

    [JsonPropertyName("replicaID")]
    public string? ReplicaId { get; set; }

    [JsonPropertyName("lastSyncDate")]
    public long? LastSyncDate { get; set; }
}

public sealed class ReplicaInfoResponse
{
    [JsonPropertyName("replicaName")]
    public string? ReplicaName { get; set; }

    [JsonPropertyName("replicaID")]
    public string? ReplicaId { get; set; }

    [JsonPropertyName("syncModel")]
    public string? SyncModel { get; set; }

    [JsonPropertyName("replicaServerGen")]
    public long? ReplicaServerGen { get; set; }

    [JsonPropertyName("layerServerGens")]
    public ReplicaInfoLayerServerGeneration[]? LayerServerGens { get; set; }

    [JsonPropertyName("creationDate")]
    public long CreationDate { get; set; }

    [JsonPropertyName("lastSyncDate")]
    public long LastSyncDate { get; set; }

    [JsonPropertyName("layers")]
    public ReplicaInfoLayer[]? Layers { get; set; }
}

public sealed class ReplicaInfoLayer
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("queryOption")]
    public string QueryOption { get; set; } = "all";

    [JsonPropertyName("useGeometry")]
    public bool UseGeometry { get; set; } = true;

    [JsonPropertyName("includeRelated")]
    public bool IncludeRelated { get; set; }

    [JsonPropertyName("where")]
    public string Where { get; set; } = string.Empty;
}

public sealed class ReplicaInfoLayerServerGeneration
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("serverGen")]
    public long ServerGen { get; set; }

    [JsonPropertyName("serverSibGen")]
    public long ServerSibGen { get; set; }
}

public sealed class ReplicaLayerInfo
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("serverGen")]
    public long ServerGen { get; set; }

    [JsonPropertyName("features")]
    public GeoServicesFeature[]? Features { get; set; }
}

public sealed class ExtractChangesRequest
{
    [JsonPropertyName("replicaID")]
    public string? ReplicaId { get; set; }

    [JsonPropertyName("layers")]
    public string? Layers { get; set; }

    [JsonPropertyName("f")]
    public string? F { get; set; }
}

public sealed class ExtractChangesResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("replicaID")]
    public string? ReplicaId { get; set; }

    [JsonPropertyName("layerChanges")]
    public LayerChanges[]? LayerChanges { get; set; }

    [JsonPropertyName("serverGen")]
    public long ServerGen { get; set; }

    [JsonPropertyName("minServerGen")]
    public long MinServerGen { get; set; }

    [JsonPropertyName("maxServerGen")]
    public long MaxServerGen { get; set; }

    [JsonPropertyName("transportType")]
    public string? TransportType { get; set; }

    [JsonPropertyName("responseType")]
    public string? ResponseType { get; set; }

    [JsonPropertyName("layerServerGens")]
    public ReplicaInfoLayerServerGeneration[]? LayerServerGens { get; set; }

    [JsonPropertyName("edits")]
    public ExtractChangesLayerEdits[]? Edits { get; set; }

    [JsonPropertyName("exceededTransferLimit")]
    public bool? ExceededTransferLimit { get; set; }
}

public sealed class ExtractChangesLayerEdits
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("features")]
    public ExtractChangesFeatureEdits? Features { get; set; }

    [JsonPropertyName("objectIds")]
    public ExtractChangesObjectIdEdits? ObjectIds { get; set; }
}

public sealed class ExtractChangesFeatureEdits
{
    [JsonPropertyName("adds")]
    public GeoServicesFeature[] Adds { get; set; } = [];

    [JsonPropertyName("updates")]
    public GeoServicesFeature[] Updates { get; set; } = [];

    [JsonPropertyName("deleteIds")]
    public long[] DeleteIds { get; set; } = [];
}

public sealed class ExtractChangesObjectIdEdits
{
    [JsonPropertyName("adds")]
    public long[] Adds { get; set; } = [];

    [JsonPropertyName("updates")]
    public long[] Updates { get; set; } = [];

    [JsonPropertyName("deletes")]
    public long[] Deletes { get; set; } = [];
}

public sealed class LayerChanges
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("adds")]
    public int Adds { get; set; }

    [JsonPropertyName("updates")]
    public int Updates { get; set; }

    [JsonPropertyName("deletes")]
    public int Deletes { get; set; }

    [JsonPropertyName("spatialReference")]
    public GeoServicesSpatialReference? SpatialReference { get; set; }

    [JsonPropertyName("addFeatures")]
    public GeoServicesFeature[]? AddFeatures { get; set; }

    [JsonPropertyName("updateFeatures")]
    public GeoServicesFeature[]? UpdateFeatures { get; set; }

    [JsonPropertyName("deleteIds")]
    public long[]? DeleteIds { get; set; }
}

public sealed class SynchronizeReplicaRequest
{
    [JsonPropertyName("replicaID")]
    public string? ReplicaId { get; set; }

    [JsonPropertyName("syncDirection")]
    public string? SyncDirection { get; set; }

    [JsonPropertyName("edits")]
    public string? Edits { get; set; }

    [JsonPropertyName("rollbackOnFailure")]
    public bool RollbackOnFailure { get; set; } = true;

    [JsonPropertyName("f")]
    public string? F { get; set; }
}

public sealed class FeatureServerSyncCapabilities
{
    [JsonPropertyName("supportsAsync")]
    public bool SupportsAsync { get; init; }

    [JsonPropertyName("supportsRegisteringExistingData")]
    public bool SupportsRegisteringExistingData { get; init; }

    [JsonPropertyName("supportsSyncDirectionControl")]
    public bool SupportsSyncDirectionControl { get; init; } = true;

    [JsonPropertyName("supportsPerLayerSync")]
    public bool SupportsPerLayerSync { get; init; } = true;

    [JsonPropertyName("supportsPerReplicaSync")]
    public bool SupportsPerReplicaSync { get; init; } = true;

    // syncModel=none returns the scoped data without registering a replica.
    [JsonPropertyName("supportsSyncModelNone")]
    public bool SupportsSyncModelNone { get; init; } = true;

    [JsonPropertyName("supportsRollbackOnFailure")]
    public bool SupportsRollbackOnFailure { get; init; } = true;

    [JsonPropertyName("supportsAttachmentsSyncDirection")]
    public bool SupportsAttachmentsSyncDirection { get; init; }

    [JsonPropertyName("supportedSyncDataOptions")]
    public int SupportedSyncDataOptions { get; init; } = 1;
}

public sealed class SynchronizeReplicaResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("replicaID")]
    public string? ReplicaId { get; set; }

    [JsonPropertyName("syncDirection")]
    public string? SyncDirection { get; set; }

    [JsonPropertyName("serverGen")]
    public long ServerGen { get; set; }

    [JsonPropertyName("edits")]
    public LayerChanges[]? Edits { get; set; }

    [JsonPropertyName("layerServerGens")]
    public ReplicaInfoLayerServerGeneration[]? LayerServerGens { get; set; }

    [JsonPropertyName("appliedAdds")]
    public int? AppliedAdds { get; set; }

    [JsonPropertyName("appliedUpdates")]
    public int? AppliedUpdates { get; set; }

    [JsonPropertyName("appliedDeletes")]
    public int? AppliedDeletes { get; set; }

    [JsonPropertyName("editResults")]
    public ServiceLayerEditResult[]? EditResults { get; set; }

    [JsonPropertyName("conflicts")]
    public SynchronizeReplicaConflict[]? Conflicts { get; set; }

    [JsonPropertyName("exceededTransferLimit")]
    public bool? ExceededTransferLimit { get; set; }
}

public sealed class SynchronizeReplicaConflict
{
    [JsonPropertyName("layerId")]
    public int LayerId { get; set; }

    [JsonPropertyName("objectId")]
    public long ObjectId { get; set; }

    [JsonPropertyName("conflictType")]
    public int ConflictType { get; set; }

    [JsonPropertyName("applied")]
    public bool Applied { get; set; }

    [JsonPropertyName("editIndex")]
    public int EditIndex { get; set; }

    [JsonPropertyName("conflictId")]
    public string? ConflictId { get; set; }
}

public sealed class SynchronizeReplicaLayerEdits
{
    [JsonPropertyName("features")]
    public SynchronizeReplicaFeatureEdits? Features { get; set; }

    [JsonPropertyName("deleteIds")]
    public long[]? DeleteIds { get; set; }

    [JsonPropertyName("attachments")]
    public System.Text.Json.JsonElement Attachments { get; set; }

    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("adds")]
    public GeoServicesFeature[]? Adds { get; set; }

    [JsonPropertyName("updates")]
    public GeoServicesFeature[]? Updates { get; set; }

    [JsonPropertyName("deletes")]
    public long[]? Deletes { get; set; }
}

public sealed class SynchronizeReplicaFeatureEdits
{
    [JsonPropertyName("adds")]
    public GeoServicesFeature[]? Adds { get; set; }

    [JsonPropertyName("updates")]
    public GeoServicesFeature[]? Updates { get; set; }

    [JsonPropertyName("deleteIds")]
    public long[]? DeleteIds { get; set; }
}

public sealed class UnRegisterReplicaRequest
{
    [JsonPropertyName("replicaID")]
    public string? ReplicaId { get; set; }

    [JsonPropertyName("f")]
    public string? F { get; set; }
}

public sealed class SuccessResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
}

internal sealed class ReplicaScopeDefinition
{
    [JsonPropertyName("geometry")]
    public string? Geometry { get; set; }

    [JsonPropertyName("geometryType")]
    public string? GeometryType { get; set; }

    [JsonPropertyName("inSR")]
    public int? InputSrid { get; set; }

    [JsonPropertyName("replicaSR")]
    public int? OutputSrid { get; set; }

    [JsonPropertyName("layerQueries")]
    public Dictionary<string, ReplicaLayerQueryDefinition>? LayerQueries { get; set; }
}

internal sealed class ReplicaLayerQueryDefinition
{
    [JsonPropertyName("queryOption")]
    public string? QueryOption { get; set; }

    [JsonPropertyName("where")]
    public string? Where { get; set; }

    [JsonPropertyName("useGeometry")]
    public bool? UseGeometry { get; set; }
}
