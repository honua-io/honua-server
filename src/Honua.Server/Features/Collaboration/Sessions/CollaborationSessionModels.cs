// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using Honua.Core.Features.Collaboration.Operations;

namespace Honua.Server.Features.Collaboration.Sessions;

internal static class CollaborationEnvelopeContract
{
    public const string Version = "honua.saved-map-collaboration.v1";
}

// These event-name strings must match the SDK union exactly.
internal static class CollaborationSessionEventTypes
{
    public const string Snapshot = "snapshot";
    public const string ParticipantJoined = "participant-joined";
    public const string ParticipantLeft = "participant-left";
    public const string Cursor = "cursor";
    public const string Selection = "selection";
    public const string Follow = "follow";
    public const string OperationAppended = "operation-appended";
    public const string Status = "status";
    public const string Error = "error";
}

internal static class CollaborationErrorCodes
{
    public const string PermissionDenied = "permission-denied";
    public const string UnsupportedCollaboration = "unsupported-collaboration";
    public const string LockHeld = "lock-held";
    public const string StaleCursor = "stale-cursor";
    public const string Conflict = "conflict";
    public const string TransportFailure = "transport-failure";
    public const string ResyncRequired = "resync-required";
}

internal sealed record CollaborationJoinRequest
{
    public string? DisplayName { get; init; }

    public string? ClientInstanceId { get; init; }
}

internal sealed record CollaborationLeaveRequest
{
    public Guid SessionId { get; init; }
}

internal sealed record CollaborationLeaveResponse
{
    public required bool Left { get; init; }
}

internal sealed record CollaborationCursor
{
    public required double X { get; init; }

    public required double Y { get; init; }

    public string? SourceId { get; init; }

    public string? LayerId { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}

internal sealed record CollaborationSelection
{
    public string[] Ids { get; init; } = [];

    public string? SourceId { get; init; }

    public string? LayerId { get; init; }

    public string? Mode { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}

internal sealed record CollaborationFollowTarget
{
    public string? TargetParticipantId { get; init; }

    public required bool Following { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}

internal sealed record CollaborationParticipantPresence
{
    public required Guid SessionId { get; init; }

    public required string ParticipantId { get; init; }

    public required string DisplayName { get; init; }

    public string? UserId { get; init; }

    public string? ClientInstanceId { get; init; }

    public required DateTimeOffset JoinedAt { get; init; }

    public required DateTimeOffset LastSeenAt { get; init; }

    public CollaborationCursor? Cursor { get; init; }

    public CollaborationSelection? Selection { get; init; }

    public Guid? FollowingSessionId { get; init; }
}

internal sealed record CollaborationParticipantWire
{
    public required string Id { get; init; }

    public required string SessionId { get; init; }

    public string? DisplayName { get; init; }

    public required DateTimeOffset JoinedAt { get; init; }

    public DateTimeOffset? LastSeenAt { get; init; }

    public static CollaborationParticipantWire FromPresence(CollaborationParticipantPresence presence) => new()
    {
        Id = presence.ParticipantId,
        SessionId = presence.SessionId.ToString("N"),
        DisplayName = presence.DisplayName,
        JoinedAt = presence.JoinedAt,
        LastSeenAt = presence.LastSeenAt
    };
}

internal sealed record CollaborationCapabilities
{
    public static CollaborationCapabilities Default { get; } = new();

    public bool Cursors { get; init; } = true;

    public bool Selections { get; init; } = true;

    public bool Follow { get; init; } = true;

    public bool FeatureLocks { get; init; }

    public bool Operations { get; init; } = true;

    public bool Replay { get; init; } = true;

    // Checkpointing is advertised false when the log cannot prove it saw every accepted edit, so the client learns at the handshake.
    public bool Checkpoints { get; init; } = true;
}

internal sealed record CollaborationOperationWire
{
    public required string Id { get; init; }

    public required string MapId { get; init; }

    public required string Kind { get; init; }

    public JsonElement Payload { get; init; }

    public long BaseRevision { get; init; }

    public string? IdempotencyKey { get; init; }

    public required long Revision { get; init; }

    public required long Sequence { get; init; }

    public required string Cursor { get; init; }

    public required string AuthorId { get; init; }

    public required DateTimeOffset SubmittedAt { get; init; }

    public static CollaborationOperationWire FromEnvelope(SavedMapOperationEnvelope operation) => new()
    {
        Id = operation.OperationId.Value,
        MapId = operation.MapId.Value,
        Kind = ToWireKind(operation.Kind),
        Payload = operation.Payload,
        BaseRevision = operation.BaseCursor.Value,
        IdempotencyKey = operation.IdempotencyKey,
        Revision = operation.ServerCursor.Value,
        Sequence = operation.ServerCursor.Value,
        Cursor = operation.ServerCursor.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        AuthorId = operation.ActorId.Value,
        SubmittedAt = operation.AcceptedAt
    };

    public static string ToWireKind(SavedMapOperationKind kind) => kind switch
    {
        SavedMapOperationKind.SetMetadataField => "metadata",
        SavedMapOperationKind.SetViewport => "view",
        SavedMapOperationKind.SetLayerVisibility => "layer-visibility",
        SavedMapOperationKind.ReorderLayers => "layer-order",
        SavedMapOperationKind.PatchStyle => "style",
        SavedMapOperationKind.ReplaceWebMapDocument => "webmap-document",
        _ => "update"
    };
}

// Presence is rebuilt from the snapshot, and any envelope at or below that sequence is dropped, so reconnects are re-snapshotted.
internal sealed record CollaborationSnapshot
{
    public required string MapId { get; init; }

    public required long Sequence { get; init; }

    public string? Cursor { get; init; }

    public CollaborationCapabilities Capabilities { get; init; } = CollaborationCapabilities.Default;

    public CollaborationParticipantWire[] Participants { get; init; } = [];

    public Dictionary<string, CollaborationCursor> Cursors { get; init; } = [];

    public Dictionary<string, CollaborationSelection> Selections { get; init; } = [];

    public Dictionary<string, CollaborationFollowTarget> FollowTargets { get; init; } = [];

    public JsonElement[] FeatureLocks { get; init; } = [];

    public CollaborationOperationWire[] Operations { get; init; } = [];

    public string Status { get; init; } = "live";
}

internal sealed record CollaborationSessionEvent
{
    public required string Type { get; init; }

    public CollaborationSnapshot? Snapshot { get; init; }

    public CollaborationParticipantWire? Participant { get; init; }

    public string? ParticipantId { get; init; }

    public string? SessionId { get; init; }

    public CollaborationCursor? Cursor { get; init; }

    public CollaborationSelection? Selection { get; init; }

    public CollaborationFollowTarget? Follow { get; init; }

    public CollaborationOperationWire? Operation { get; init; }

    public string? Status { get; init; }

    public string? Reason { get; init; }

    public string? Code { get; init; }

    public string? Message { get; init; }

    public bool? Terminal { get; init; }

    public bool? ResyncRequired { get; init; }
}

// Without single-writer affinity, remote presence sequences may be dropped; that degrades freshness and does not corrupt state, because operation order is the op-log cursor.
internal sealed record CollaborationEventEnvelope
{
    public string EnvelopeVersion { get; init; } = CollaborationEnvelopeContract.Version;

    public required string MapId { get; init; }

    public required string EventId { get; init; }

    public required long Sequence { get; init; }

    public required string Cursor { get; init; }

    public required DateTimeOffset ServerTime { get; init; }

    public string? SessionId { get; init; }

    public string? ActorId { get; init; }

    public required CollaborationSessionEvent Event { get; init; }
}

internal sealed record CollaborationJoinResponse
{
    public required string MapId { get; init; }

    public required Guid SessionId { get; init; }

    public required string ParticipantId { get; init; }

    public CollaborationCapabilities Capabilities { get; init; } = CollaborationCapabilities.Default;

    public required CollaborationParticipantWire Participant { get; init; }

    public required CollaborationSnapshot Snapshot { get; init; }
}

internal sealed record CollaborationFanOutResult
{
    public required CollaborationEventEnvelope Event { get; init; }

    public required int DeliveredCount { get; init; }
}

internal sealed record CollaborationBackplaneMessage
{
    public required string OriginInstanceId { get; init; }

    public required CollaborationEventEnvelope Event { get; init; }
}

internal sealed record CollaborationClientFrame
{
    public string? Type { get; init; }

    public CollaborationCursor? Cursor { get; init; }

    public CollaborationSelection? Selection { get; init; }

    public CollaborationFollowTarget? Follow { get; init; }
}

internal enum SavedMapCollaborationAuthorizationStatus
{
    Authorized,
    RequiresAuthentication,
    Forbidden
}

internal sealed record SavedMapCollaborationAuthorizationResult
{
    private SavedMapCollaborationAuthorizationResult(
        SavedMapCollaborationAuthorizationStatus status,
        string? detail)
    {
        Status = status;
        Detail = detail;
    }

    public SavedMapCollaborationAuthorizationStatus Status { get; }

    public string? Detail { get; }

    public bool Authorized => Status == SavedMapCollaborationAuthorizationStatus.Authorized;

    public static SavedMapCollaborationAuthorizationResult Allow() =>
        new(SavedMapCollaborationAuthorizationStatus.Authorized, detail: null);

    public static SavedMapCollaborationAuthorizationResult RequireAuthentication(string detail) =>
        new(SavedMapCollaborationAuthorizationStatus.RequiresAuthentication, detail);

    public static SavedMapCollaborationAuthorizationResult Forbid(string detail) =>
        new(SavedMapCollaborationAuthorizationStatus.Forbidden, detail);
}

internal enum CollaborationJoinRefusal
{
    None = 0,
    InvalidRequest = 400,
    CapacityExceeded = 429
}

internal sealed record CollaborationJoinResult
{
    public required SavedMapCollaborationAuthorizationResult Authorization { get; init; }

    public CollaborationJoinResponse? Response { get; init; }

    public CollaborationJoinRefusal Refusal { get; init; }

    public string? Detail { get; init; }
}

internal interface ISavedMapCollaborationAuthorizer
{
    ValueTask<SavedMapCollaborationAuthorizationResult> AuthorizeJoinAsync(
        string mapId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken);
}

internal interface ICollaborationSessionClock
{
    DateTimeOffset UtcNow { get; }
}
