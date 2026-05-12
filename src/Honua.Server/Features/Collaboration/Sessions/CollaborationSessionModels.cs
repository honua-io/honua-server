// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;

namespace Honua.Server.Features.Collaboration.Sessions;

internal static class CollaborationSessionEventTypes
{
    public const string ParticipantJoined = "participant.joined";
    public const string ParticipantHeartbeat = "participant.heartbeat";
    public const string ParticipantLeft = "participant.left";
    public const string CursorUpdated = "cursor.updated";
    public const string SelectionUpdated = "selection.updated";
    public const string FollowUpdated = "follow.updated";
    public const string FollowCleared = "follow.cleared";
    public const string Snapshot = "snapshot";
}

internal sealed record CollaborationJoinRequest
{
    public string? DisplayName { get; init; }

    public string? ClientInstanceId { get; init; }
}

internal sealed record CollaborationCursor
{
    public required double Longitude { get; init; }

    public required double Latitude { get; init; }

    public double? Zoom { get; init; }

    public double? ScreenX { get; init; }

    public double? ScreenY { get; init; }
}

internal sealed record CollaborationSelection
{
    public string? LayerId { get; init; }

    public string[] FeatureIds { get; init; } = [];
}

internal sealed record CollaborationFollowState
{
    public Guid? FollowingSessionId { get; init; }

    public bool IsFollowing => FollowingSessionId.HasValue;
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

    public CollaborationFollowState Follow { get; init; } = new();
}

internal sealed record CollaborationSessionSnapshot
{
    public required string MapId { get; init; }

    public CollaborationParticipantPresence[] Participants { get; init; } = [];

    public required DateTimeOffset GeneratedAt { get; init; }
}

internal sealed record CollaborationEventEnvelope
{
    public required string Type { get; init; }

    public required Guid EventId { get; init; }

    public required string MapId { get; init; }

    public Guid? SessionId { get; init; }

    public string? ActorParticipantId { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public CollaborationParticipantPresence? Participant { get; init; }

    public CollaborationCursor? Cursor { get; init; }

    public CollaborationSelection? Selection { get; init; }

    public CollaborationFollowState? Follow { get; init; }

    public CollaborationSessionSnapshot? Snapshot { get; init; }
}

internal sealed record CollaborationJoinResponse
{
    public required Guid SessionId { get; init; }

    public required CollaborationParticipantPresence Participant { get; init; }

    public required CollaborationSessionSnapshot Snapshot { get; init; }
}

internal sealed record CollaborationFanOutResult
{
    public required CollaborationEventEnvelope Event { get; init; }

    public required int DeliveredCount { get; init; }
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
