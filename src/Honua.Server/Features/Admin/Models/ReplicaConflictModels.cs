// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

public sealed class ReplicaConflictSummary
{
    public required string ConflictId { get; init; }

    public required string ReplicaId { get; init; }

    public required string ServiceId { get; init; }

    public required int LayerId { get; init; }

    public required long ObjectId { get; init; }

    public required string ConflictType { get; init; }

    public required string Status { get; init; }

    public required long ServerGeneration { get; init; }

    public required bool ClientEditApplied { get; init; }

    public bool ClientEditOutcomeUnknown { get; init; }

    public bool ClientEditSuperseded { get; init; }

    public required DateTimeOffset DetectedAt { get; init; }
}

public sealed class ReplicaConflictDetail
{
    public required string ConflictId { get; init; }

    public required string ReplicaId { get; init; }

    public required string ServiceId { get; init; }

    public required int LayerId { get; init; }

    public required long ObjectId { get; init; }

    public required string ConflictType { get; init; }

    public required string Status { get; init; }

    public string? SyncOperationId { get; init; }

    public string? DeviceId { get; init; }

    public string? UserId { get; init; }

    public required long ServerGeneration { get; init; }

    public required bool ClientEditApplied { get; init; }

    public bool ClientEditOutcomeUnknown { get; init; }

    public bool ClientEditSuperseded { get; init; }

    public System.Text.Json.JsonElement? BaseState { get; init; }

    public System.Text.Json.JsonElement? ClientState { get; init; }

    public System.Text.Json.JsonElement? ServerState { get; init; }

    // Geometry difference is null when either side has no captured geometry.
    public bool? GeometryChanged { get; init; }

    public ReplicaConflictFieldChange[]? FieldChanges { get; init; }

    public required DateTimeOffset DetectedAt { get; init; }

    public string? ResolutionAction { get; init; }

    public string? ResolvedBy { get; init; }

    public DateTimeOffset? ResolvedAt { get; init; }

    public long? ResolvedServerGeneration { get; init; }
}

public sealed class ReplicaConflictFieldChange
{
    public required string Field { get; init; }

    public System.Text.Json.JsonElement? BaseValue { get; init; }

    public System.Text.Json.JsonElement? ClientValue { get; init; }

    public System.Text.Json.JsonElement? ServerValue { get; init; }

    public required bool ChangedOnClient { get; init; }

    public required bool ChangedOnServer { get; init; }
}

public sealed class ReplicaConflictListResponse
{
    public required string ServiceId { get; init; }

    public required string ReplicaId { get; init; }

    public string? StatusFilter { get; init; }

    public required ReplicaConflictSummary[] Conflicts { get; init; }
}

public sealed class ReplicaConflictResolutionRequest
{
    public string? Action { get; init; }

    // mergeFields values match captured attributes without case sensitivity and are ignored by every other resolution.
    public Dictionary<string, System.Text.Json.JsonElement>? FieldValues { get; init; }

    // chooseGeometry accepts client or server and is ignored by every other resolution.
    public string? Geometry { get; init; }
}

public sealed class ReplicaConflictResolutionResponse
{
    public required ReplicaConflictDetail Conflict { get; init; }

    public required bool CommittedNewServerState { get; init; }

    public required string Effect { get; init; }
}

public sealed class ReplicaConflictAuditDetails
{
    public required string Action { get; init; }

    public required string Effect { get; init; }

    public required bool CommittedNewServerState { get; init; }

    public required string ServiceId { get; init; }

    public required int LayerId { get; init; }

    public required long ObjectId { get; init; }

    public long? ResolvedServerGeneration { get; init; }
}
