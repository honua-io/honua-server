// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Operator-facing summary of a named disconnected replica (#1167).
/// </summary>
public sealed class ReplicaAdminSummary
{
    [JsonPropertyName("replicaId")]
    public required string ReplicaId { get; init; }

    [JsonPropertyName("replicaName")]
    public required string ReplicaName { get; init; }

    [JsonPropertyName("serviceId")]
    public required string ServiceId { get; init; }

    [JsonPropertyName("owner")]
    public string? Owner { get; init; }

    [JsonPropertyName("deviceClient")]
    public string? DeviceClient { get; init; }

    [JsonPropertyName("syncModel")]
    public required string SyncModel { get; init; }

    [JsonPropertyName("syncDirection")]
    public required string SyncDirection { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("lastSyncTime")]
    public required DateTimeOffset LastSyncTime { get; init; }

    [JsonPropertyName("lastSyncGeneration")]
    public required long LastSyncGeneration { get; init; }

    [JsonPropertyName("pendingConflicts")]
    public required int PendingConflicts { get; init; }
}

/// <summary>
/// Full operator-facing detail for a single named replica.
/// </summary>
public sealed class ReplicaAdminDetail
{
    [JsonPropertyName("replicaId")]
    public required string ReplicaId { get; init; }

    [JsonPropertyName("replicaName")]
    public required string ReplicaName { get; init; }

    [JsonPropertyName("serviceId")]
    public required string ServiceId { get; init; }

    [JsonPropertyName("owner")]
    public string? Owner { get; init; }

    [JsonPropertyName("deviceClient")]
    public string? DeviceClient { get; init; }

    [JsonPropertyName("syncModel")]
    public required string SyncModel { get; init; }

    [JsonPropertyName("syncDirection")]
    public required string SyncDirection { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("layerIds")]
    public required int[] LayerIds { get; init; }

    [JsonPropertyName("replicaGeometryJson")]
    public string? ReplicaGeometryJson { get; init; }

    [JsonPropertyName("branchVersionId")]
    public string? BranchVersionId { get; init; }

    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("lastSyncTime")]
    public required DateTimeOffset LastSyncTime { get; init; }

    [JsonPropertyName("lastSyncGeneration")]
    public required long LastSyncGeneration { get; init; }

    [JsonPropertyName("pendingConflicts")]
    public required int PendingConflicts { get; init; }
}

/// <summary>
/// Lightweight conflict entry returned by the conflict list endpoint.
/// </summary>
public sealed class ConflictSummary
{
    [JsonPropertyName("conflictId")]
    public required string ConflictId { get; init; }

    [JsonPropertyName("syncOpId")]
    public required string SyncOpId { get; init; }

    [JsonPropertyName("layerId")]
    public required int LayerId { get; init; }

    [JsonPropertyName("objectId")]
    public required long ObjectId { get; init; }

    [JsonPropertyName("conflictType")]
    public required string ConflictType { get; init; }

    [JsonPropertyName("resolution")]
    public string? Resolution { get; init; }

    [JsonPropertyName("baseGeneration")]
    public required long BaseGeneration { get; init; }

    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Full conflict detail including base/client/server feature states and a forward link to the
/// feature's temporal history (#1166). The link is present even when the history API is not deployed.
/// </summary>
public sealed class ConflictDetail
{
    [JsonPropertyName("conflictId")]
    public required string ConflictId { get; init; }

    [JsonPropertyName("replicaId")]
    public required string ReplicaId { get; init; }

    [JsonPropertyName("syncOpId")]
    public required string SyncOpId { get; init; }

    [JsonPropertyName("serviceId")]
    public required string ServiceId { get; init; }

    [JsonPropertyName("layerId")]
    public required int LayerId { get; init; }

    [JsonPropertyName("objectId")]
    public required long ObjectId { get; init; }

    [JsonPropertyName("conflictType")]
    public required string ConflictType { get; init; }

    [JsonPropertyName("baseGeneration")]
    public required long BaseGeneration { get; init; }

    /// <summary>Submitted client feature state.</summary>
    [JsonPropertyName("clientFeature")]
    public JsonElement? ClientFeature { get; init; }

    /// <summary>Current server feature state at detection time; JSON null when the server deleted it.</summary>
    [JsonPropertyName("serverFeature")]
    public JsonElement? ServerFeature { get; init; }

    /// <summary>Common-ancestor feature state; null in the first slice (reserved for #1166).</summary>
    [JsonPropertyName("baseFeature")]
    public JsonElement? BaseFeature { get; init; }

    /// <summary>Field-level differences between the base, client, and server states.</summary>
    [JsonPropertyName("fieldChanges")]
    public required FieldChange[] FieldChanges { get; init; }

    /// <summary>Geometry-level difference metadata for the base, client, and server states.</summary>
    [JsonPropertyName("geometryChange")]
    public required GeometryChangeMetadata GeometryChange { get; init; }

    [JsonPropertyName("resolution")]
    public string? Resolution { get; init; }

    [JsonPropertyName("resolvedBy")]
    public string? ResolvedBy { get; init; }

    [JsonPropertyName("resolvedAt")]
    public DateTimeOffset? ResolvedAt { get; init; }

    /// <summary>Operator-supplied merged feature payload for merge-field resolutions.</summary>
    [JsonPropertyName("resolutionFeature")]
    public JsonElement? ResolutionFeature { get; init; }

    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Forward link to the feature's temporal history (#1166).</summary>
    [JsonPropertyName("temporalHistoryHref")]
    public required string TemporalHistoryHref { get; init; }
}

/// <summary>
/// Field-level conflict metadata comparing a single attribute across base/client/server states.
/// When the first slice has no base snapshot, change flags compare client and server directly.
/// </summary>
public sealed class FieldChange
{
    [JsonPropertyName("fieldName")]
    public required string FieldName { get; init; }

    [JsonPropertyName("baseValue")]
    public JsonElement? BaseValue { get; init; }

    [JsonPropertyName("clientValue")]
    public JsonElement? ClientValue { get; init; }

    [JsonPropertyName("serverValue")]
    public JsonElement? ServerValue { get; init; }

    [JsonPropertyName("clientChanged")]
    public required bool ClientChanged { get; init; }

    [JsonPropertyName("serverChanged")]
    public required bool ServerChanged { get; init; }

    [JsonPropertyName("clientDiffersFromServer")]
    public required bool ClientDiffersFromServer { get; init; }
}

/// <summary>
/// Geometry-level conflict metadata comparing base/client/server geometry presence and equality.
/// </summary>
public sealed class GeometryChangeMetadata
{
    [JsonPropertyName("baseHasGeometry")]
    public required bool BaseHasGeometry { get; init; }

    [JsonPropertyName("clientHasGeometry")]
    public required bool ClientHasGeometry { get; init; }

    [JsonPropertyName("serverHasGeometry")]
    public required bool ServerHasGeometry { get; init; }

    [JsonPropertyName("clientChanged")]
    public required bool ClientChanged { get; init; }

    [JsonPropertyName("serverChanged")]
    public required bool ServerChanged { get; init; }

    [JsonPropertyName("clientDiffersFromServer")]
    public required bool ClientDiffersFromServer { get; init; }
}

/// <summary>
/// Request body for resolving a pending conflict.
/// </summary>
public sealed class ResolveConflictRequest
{
    /// <summary>One of: accept_client, keep_server, merge_fields, reject_client, defer.</summary>
    [JsonPropertyName("resolution")]
    public string? Resolution { get; set; }

    /// <summary>Merged feature payload (GeoServices feature JSON); required for merge_fields.</summary>
    [JsonPropertyName("mergedPayloadJson")]
    public string? MergedPayloadJson { get; set; }
}

/// <summary>
/// Response returned after a conflict is resolved.
/// </summary>
public sealed class ResolveConflictResponse
{
    [JsonPropertyName("conflictId")]
    public required string ConflictId { get; init; }

    [JsonPropertyName("resolution")]
    public required string Resolution { get; init; }
}

/// <summary>
/// Source-generated JSON context for the admin replica/conflict API surface (#1167).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ReplicaAdminSummary))]
[JsonSerializable(typeof(ReplicaAdminSummary[]), TypeInfoPropertyName = "ReplicaAdminSummaryArray")]
[JsonSerializable(typeof(ReplicaAdminDetail))]
[JsonSerializable(typeof(ConflictSummary))]
[JsonSerializable(typeof(ConflictSummary[]), TypeInfoPropertyName = "ConflictSummaryArray")]
[JsonSerializable(typeof(ConflictDetail))]
[JsonSerializable(typeof(FieldChange))]
[JsonSerializable(typeof(FieldChange[]), TypeInfoPropertyName = "FieldChangeArray")]
[JsonSerializable(typeof(GeometryChangeMetadata))]
[JsonSerializable(typeof(ResolveConflictRequest))]
[JsonSerializable(typeof(ResolveConflictResponse))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class ReplicaConflictsJsonContext : JsonSerializerContext;
