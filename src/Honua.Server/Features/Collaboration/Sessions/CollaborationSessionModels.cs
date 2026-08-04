// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using Honua.Core.Features.Collaboration.Operations;

namespace Honua.Server.Features.Collaboration.Sessions;

/// <summary>
/// Wire constants for the saved-map collaboration envelope contract shared with the JS SDK
/// (honua-sdk-js <c>src/collaboration/types.ts</c>, honua-server#2999). Every frame emitted on the
/// session stream is a <see cref="CollaborationEventEnvelope"/> carrying exactly one event from
/// the SDK's discriminated union, so <c>reduceSavedMapCollaborationSnapshot</c> can consume the
/// transport without adaptation.
/// </summary>
internal static class CollaborationEnvelopeContract
{
    /// <summary>Envelope schema identifier expected by the SDK reducer.</summary>
    public const string Version = "honua.saved-map-collaboration.v1";
}

/// <summary>
/// Event names for the SDK collaboration event union. These strings are wire contract — they must
/// match honua-sdk-js <c>SavedMapCollaborationEvent["type"]</c> exactly.
/// </summary>
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

/// <summary>
/// Typed error codes for collaboration error events (SDK <c>SavedMapCollaborationErrorCode</c>).
/// </summary>
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

/// <summary>
/// Request body for the explicit session-leave endpoint.
/// </summary>
internal sealed record CollaborationLeaveRequest
{
    public Guid SessionId { get; init; }
}

/// <summary>
/// Response for the explicit session-leave endpoint.
/// </summary>
internal sealed record CollaborationLeaveResponse
{
    public required bool Left { get; init; }
}

/// <summary>
/// Participant cursor in the SDK wire shape (<c>SavedMapCursor</c>): <c>x</c>/<c>y</c> are map
/// coordinates in the composition's CRS (longitude/latitude for EPSG:4326 compositions).
/// </summary>
internal sealed record CollaborationCursor
{
    public required double X { get; init; }

    public required double Y { get; init; }

    public string? SourceId { get; init; }

    public string? LayerId { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>
/// Participant selection in the SDK wire shape (<c>SavedMapSelection</c>).
/// </summary>
internal sealed record CollaborationSelection
{
    public string[] Ids { get; init; } = [];

    public string? SourceId { get; init; }

    public string? LayerId { get; init; }

    public string? Mode { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>
/// Follow state in the SDK wire shape (<c>SavedMapFollowTarget</c>). <see cref="Following"/> false
/// with no target clears a follow.
/// </summary>
internal sealed record CollaborationFollowTarget
{
    public string? TargetParticipantId { get; init; }

    public required bool Following { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>
/// Server-side presence state for one joined participant. This is the internal model; the wire
/// projection is <see cref="CollaborationParticipantWire"/>.
/// </summary>
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

/// <summary>
/// Participant projection in the SDK wire shape (<c>SavedMapCollaborationParticipant</c>).
/// </summary>
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

/// <summary>
/// Session capability flags in the SDK wire shape (<c>SavedMapCollaborationCapabilities</c>).
/// <c>FeatureLocks</c> is intentionally false: the saved-map feature-lock REST endpoints exist,
/// but lock events are not bridged onto the session stream yet, so the stream contract does not
/// advertise them.
/// </summary>
internal sealed record CollaborationCapabilities
{
    public static CollaborationCapabilities Default { get; } = new();

    public bool Cursors { get; init; } = true;

    public bool Selections { get; init; } = true;

    public bool Follow { get; init; } = true;

    public bool FeatureLocks { get; init; }

    public bool Operations { get; init; } = true;

    public bool Replay { get; init; } = true;

    /// <summary>
    /// Whether the session can be checkpointed into an immutable Studio content version. False
    /// when the operation log cannot prove it observed every accepted edit (a process-local log
    /// loses acknowledged operations on restart), so clients learn from the handshake instead of
    /// discovering it through a failed checkpoint (honua-server#2999 review).
    /// </summary>
    public bool Checkpoints { get; init; } = true;
}

/// <summary>
/// Committed saved-map operation in the SDK wire shape (<c>SavedMapCommittedOperation</c>). The
/// operation-level <see cref="Sequence"/>/<see cref="Cursor"/>/<see cref="Revision"/> come from
/// the op-log's monotonic server cursor — the op-log is the authoritative order for edits, while
/// the envelope-level sequence orders the whole stream (presence included).
/// </summary>
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

    /// <summary>
    /// Maps the server operation kind onto the SDK's open string kind vocabulary
    /// (<c>SavedMapEditOperation["kind"]</c>).
    /// </summary>
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

/// <summary>
/// Session snapshot in the SDK wire shape (<c>SavedMapCollaborationSnapshot</c>). The reducer
/// rebuilds presence entirely from this snapshot and drops any envelope whose sequence is at or
/// below <see cref="Sequence"/>, which is why reconnecting clients are re-snapshotted rather than
/// resumed for presence (the bounded per-participant outbox cannot replay presence history).
/// </summary>
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

/// <summary>
/// One event from the SDK collaboration event union. <see cref="Type"/> discriminates; only the
/// fields relevant to that type are populated (null fields are omitted on the wire), which makes
/// this single record serialize exactly like the SDK's discriminated union members.
/// </summary>
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

/// <summary>
/// The v1 collaboration envelope (SDK <c>SavedMapCollaborationEnvelope</c>). <see cref="Sequence"/>
/// is strictly monotonic per map on the emitting node; <see cref="Cursor"/> is an opaque string
/// projection of the sequence.
/// </summary>
/// <remarks>
/// Multi-node scoping (honua-server#2999): the per-map sequence counter lives in the node-local
/// session service, and backplane broadcasts carry the origin node's sequence unchanged. Strict
/// cross-node monotonicity therefore requires single-writer-per-map routing (node affinity for a
/// given <c>mapId</c>). Without affinity, remote presence envelopes may arrive with sequences at
/// or below a client's snapshot sequence and are dropped by the SDK reducer — degrading remote
/// presence freshness, never corrupting state. Operation order is unaffected either way: it is
/// carried by the op-log server cursor inside the operation payload, not the envelope sequence.
/// </remarks>
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

/// <summary>
/// Join response for the REST join endpoint and the stream handshake, mirroring the SDK
/// <c>SavedMapCollaborationJoinResult</c> (mapId/sessionId/participantId/capabilities/snapshot).
/// The REST join snapshot carries presence only; operation tails are served by the replay
/// endpoint or the stream handshake snapshot.
/// </summary>
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

/// <summary>
/// Cross-node collaboration broadcast envelope published over the Redis backplane. The
/// originating instance id lets each node ignore its own echoes and prevents fan-out loops.
/// </summary>
internal sealed record CollaborationBackplaneMessage
{
    public required string OriginInstanceId { get; init; }

    public required CollaborationEventEnvelope Event { get; init; }
}

/// <summary>
/// WebSocket control frame sent by a collaboration client to update its presence/cursor/follow
/// state over the live session stream. The frame <see cref="Type"/> mirrors the SDK event
/// vocabulary (<c>cursor</c>, <c>selection</c>, <c>follow</c>) plus <c>heartbeat</c>/<c>ping</c>.
/// </summary>
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

internal sealed record CollaborationJoinResult
{
    public required SavedMapCollaborationAuthorizationResult Authorization { get; init; }

    public CollaborationJoinResponse? Response { get; init; }
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
