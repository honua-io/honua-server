// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Security.Claims;

namespace Honua.Server.Features.Collaboration.Sessions;

internal sealed class InMemoryCollaborationSessionService
{
    private readonly ConcurrentDictionary<string, MapChannel> _channels = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, string> _sessionMapIndex = new();
    private readonly ISavedMapCollaborationAuthorizer _authorizer;
    private readonly ICollaborationSessionClock _clock;

    public InMemoryCollaborationSessionService(
        ISavedMapCollaborationAuthorizer authorizer,
        ICollaborationSessionClock clock)
    {
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask<CollaborationJoinResult> JoinAsync(
        string mapId,
        CollaborationJoinRequest request,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(principal);

        var authorization = await _authorizer.AuthorizeJoinAsync(mapId, principal, cancellationToken).ConfigureAwait(false);
        if (!authorization.Authorized)
        {
            return new CollaborationJoinResult { Authorization = authorization };
        }

        var now = _clock.UtcNow;
        var sessionId = Guid.NewGuid();
        var userId = ResolveUserId(principal);
        var participant = new CollaborationParticipantPresence
        {
            SessionId = sessionId,
            ParticipantId = sessionId.ToString("N"),
            DisplayName = ResolveDisplayName(request, principal, sessionId),
            UserId = userId,
            ClientInstanceId = NormalizeOptional(request.ClientInstanceId),
            JoinedAt = now,
            LastSeenAt = now
        };

        var channel = _channels.GetOrAdd(mapId, static id => new MapChannel(id));
        CollaborationSessionSnapshot snapshot;
        lock (channel.Sync)
        {
            channel.Participants.Add(sessionId, new ParticipantState(participant));
            _sessionMapIndex[sessionId] = mapId;

            var joined = CreateEvent(
                CollaborationSessionEventTypes.ParticipantJoined,
                mapId,
                participant,
                now) with
            { Participant = participant };
            FanOutLocked(channel, joined, excludeSessionId: sessionId);
            snapshot = CreateSnapshotLocked(channel, now);
        }

        return new CollaborationJoinResult
        {
            Authorization = authorization,
            Response = new CollaborationJoinResponse
            {
                SessionId = sessionId,
                Participant = participant,
                Snapshot = snapshot
            }
        };
    }

    public CollaborationSessionSnapshot GetSnapshot(string mapId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        var now = _clock.UtcNow;
        var channel = _channels.GetOrAdd(mapId, static id => new MapChannel(id));
        lock (channel.Sync)
        {
            return CreateSnapshotLocked(channel, now);
        }
    }

    public CollaborationFanOutResult Heartbeat(Guid sessionId)
    {
        return UpdateParticipant(
            sessionId,
            CollaborationSessionEventTypes.ParticipantHeartbeat,
            static (presence, now) => presence with { LastSeenAt = now },
            static (ev, _) => ev);
    }

    public CollaborationFanOutResult UpdateCursor(Guid sessionId, CollaborationCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        return UpdateParticipant(
            sessionId,
            CollaborationSessionEventTypes.CursorUpdated,
            (presence, now) => presence with { LastSeenAt = now, Cursor = cursor },
            (ev, _) => ev with { Cursor = cursor });
    }

    public CollaborationFanOutResult UpdateSelection(Guid sessionId, CollaborationSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return UpdateParticipant(
            sessionId,
            CollaborationSessionEventTypes.SelectionUpdated,
            (presence, now) => presence with { LastSeenAt = now, Selection = selection },
            (ev, _) => ev with { Selection = selection });
    }

    public CollaborationFanOutResult Follow(Guid sessionId, Guid targetSessionId)
    {
        if (!TryResolveSession(sessionId, out var mapId, out var channel))
        {
            throw new KeyNotFoundException($"Collaboration session '{sessionId}' was not found.");
        }

        lock (channel.Sync)
        {
            if (!channel.Participants.TryGetValue(sessionId, out var state))
            {
                throw new KeyNotFoundException($"Collaboration session '{sessionId}' was not found.");
            }

            if (!channel.Participants.ContainsKey(targetSessionId))
            {
                throw new KeyNotFoundException(
                    $"Collaboration follow target '{targetSessionId}' was not found in map '{mapId}'.");
            }

            var now = _clock.UtcNow;
            var updatedPresence = state.Presence with
            {
                LastSeenAt = now,
                Follow = new CollaborationFollowState { FollowingSessionId = targetSessionId }
            };
            state.Presence = updatedPresence;

            var ev = CreateEvent(
                CollaborationSessionEventTypes.FollowUpdated,
                mapId,
                updatedPresence,
                now) with
            {
                Participant = updatedPresence,
                Follow = updatedPresence.Follow
            };
            var delivered = FanOutLocked(channel, ev, excludeSessionId: sessionId);
            return new CollaborationFanOutResult { Event = ev, DeliveredCount = delivered };
        }
    }

    public CollaborationFanOutResult Unfollow(Guid sessionId)
    {
        return UpdateParticipant(
            sessionId,
            CollaborationSessionEventTypes.FollowCleared,
            static (presence, now) => presence with
            {
                LastSeenAt = now,
                Follow = new CollaborationFollowState()
            },
            static (ev, presence) => ev with { Follow = presence.Follow });
    }

    public bool Leave(Guid sessionId, string? reason = null)
    {
        if (!TryResolveSession(sessionId, out var mapId, out var channel))
        {
            return false;
        }

        lock (channel.Sync)
        {
            if (!channel.Participants.Remove(sessionId, out var state))
            {
                _sessionMapIndex.TryRemove(sessionId, out _);
                return false;
            }

            _sessionMapIndex.TryRemove(sessionId, out _);
            ClearFollowsTargetingLocked(channel, mapId, sessionId, _clock.UtcNow);
            var left = CreateEvent(
                CollaborationSessionEventTypes.ParticipantLeft,
                mapId,
                state.Presence,
                _clock.UtcNow) with
            { Participant = state.Presence };
            FanOutLocked(channel, left, excludeSessionId: sessionId);
            RemoveChannelIfEmpty(mapId, channel);
            return true;
        }
    }

    public int PruneStaleParticipants(TimeSpan maxIdle)
    {
        if (maxIdle <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxIdle), "Stale participant timeout must be positive.");
        }

        var now = _clock.UtcNow;
        var removed = 0;
        foreach (var (mapId, channel) in _channels)
        {
            lock (channel.Sync)
            {
                var stale = channel.Participants
                    .Where(kvp => now - kvp.Value.Presence.LastSeenAt > maxIdle)
                    .Select(kvp => kvp.Key)
                    .ToArray();

                foreach (var sessionId in stale)
                {
                    if (!channel.Participants.Remove(sessionId, out var state))
                    {
                        continue;
                    }

                    _sessionMapIndex.TryRemove(sessionId, out _);
                    removed++;
                    ClearFollowsTargetingLocked(channel, mapId, sessionId, now);

                    var left = CreateEvent(
                        CollaborationSessionEventTypes.ParticipantLeft,
                        mapId,
                        state.Presence,
                        now) with
                    { Participant = state.Presence };
                    FanOutLocked(channel, left, excludeSessionId: sessionId);
                }

                RemoveChannelIfEmpty(mapId, channel);
            }
        }

        return removed;
    }

    public CollaborationEventEnvelope[] DrainEvents(Guid sessionId)
    {
        if (!TryResolveSession(sessionId, out _, out var channel))
        {
            return [];
        }

        lock (channel.Sync)
        {
            if (!channel.Participants.TryGetValue(sessionId, out var state))
            {
                return [];
            }

            var events = state.Outbox.ToArray();
            state.Outbox.Clear();
            return events;
        }
    }

    private CollaborationFanOutResult UpdateParticipant(
        Guid sessionId,
        string eventType,
        Func<CollaborationParticipantPresence, DateTimeOffset, CollaborationParticipantPresence> update,
        Func<CollaborationEventEnvelope, CollaborationParticipantPresence, CollaborationEventEnvelope> enrich)
    {
        if (!TryResolveSession(sessionId, out var mapId, out var channel))
        {
            throw new KeyNotFoundException($"Collaboration session '{sessionId}' was not found.");
        }

        lock (channel.Sync)
        {
            if (!channel.Participants.TryGetValue(sessionId, out var state))
            {
                throw new KeyNotFoundException($"Collaboration session '{sessionId}' was not found.");
            }

            var now = _clock.UtcNow;
            var updatedPresence = update(state.Presence, now);
            state.Presence = updatedPresence;

            var ev = enrich(
                CreateEvent(eventType, mapId, updatedPresence, now) with { Participant = updatedPresence },
                updatedPresence);
            var delivered = FanOutLocked(channel, ev, excludeSessionId: sessionId);
            return new CollaborationFanOutResult { Event = ev, DeliveredCount = delivered };
        }
    }

    private bool TryResolveSession(Guid sessionId, out string mapId, out MapChannel channel)
    {
        mapId = string.Empty;
        channel = null!;
        return _sessionMapIndex.TryGetValue(sessionId, out mapId!) &&
            _channels.TryGetValue(mapId, out channel!);
    }

    private static int FanOutLocked(MapChannel channel, CollaborationEventEnvelope ev, Guid excludeSessionId)
    {
        var delivered = 0;
        foreach (var (sessionId, state) in channel.Participants)
        {
            if (sessionId == excludeSessionId)
            {
                continue;
            }

            state.Outbox.Enqueue(ev);
            delivered++;
        }

        return delivered;
    }

    private static int ClearFollowsTargetingLocked(
        MapChannel channel,
        string mapId,
        Guid targetSessionId,
        DateTimeOffset now)
    {
        var cleared = 0;
        foreach (var state in channel.Participants.Values)
        {
            if (state.Presence.Follow.FollowingSessionId != targetSessionId)
            {
                continue;
            }

            var updatedPresence = state.Presence with
            {
                LastSeenAt = now,
                Follow = new CollaborationFollowState()
            };
            state.Presence = updatedPresence;
            var ev = CreateEvent(
                CollaborationSessionEventTypes.FollowCleared,
                mapId,
                updatedPresence,
                now) with
            {
                Participant = updatedPresence,
                Follow = updatedPresence.Follow
            };
            FanOutLocked(channel, ev, excludeSessionId: Guid.Empty);
            cleared++;
        }

        return cleared;
    }

    private static CollaborationSessionSnapshot CreateSnapshotLocked(MapChannel channel, DateTimeOffset now)
    {
        return new CollaborationSessionSnapshot
        {
            MapId = channel.MapId,
            Participants = channel.Participants.Values
                .Select(static state => state.Presence)
                .OrderBy(static presence => presence.JoinedAt)
                .ThenBy(static presence => presence.ParticipantId, StringComparer.Ordinal)
                .ToArray(),
            GeneratedAt = now
        };
    }

    private static CollaborationEventEnvelope CreateEvent(
        string type,
        string mapId,
        CollaborationParticipantPresence participant,
        DateTimeOffset now)
    {
        return new CollaborationEventEnvelope
        {
            Type = type,
            EventId = Guid.NewGuid(),
            MapId = mapId,
            SessionId = participant.SessionId,
            ActorParticipantId = participant.ParticipantId,
            Timestamp = now
        };
    }

    private void RemoveChannelIfEmpty(string mapId, MapChannel channel)
    {
        if (channel.Participants.Count == 0)
        {
            _channels.TryRemove(new KeyValuePair<string, MapChannel>(mapId, channel));
        }
    }

    private static string ResolveDisplayName(
        CollaborationJoinRequest request,
        ClaimsPrincipal principal,
        Guid sessionId)
    {
        var requested = NormalizeOptional(request.DisplayName);
        if (requested is not null)
        {
            return requested;
        }

        var name = NormalizeOptional(principal.FindFirstValue(ClaimTypes.Name));
        if (name is not null)
        {
            return name;
        }

        return $"participant-{sessionId:N}"[..44];
    }

    private static string? ResolveUserId(ClaimsPrincipal principal)
    {
        return NormalizeOptional(principal.FindFirstValue(ClaimTypes.NameIdentifier)) ??
            NormalizeOptional(principal.FindFirstValue("sub")) ??
            NormalizeOptional(principal.FindFirstValue(ClaimTypes.Name));
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private sealed class MapChannel
    {
        public MapChannel(string mapId)
        {
            MapId = mapId;
        }

        public string MapId { get; }

        public object Sync { get; } = new();

        public Dictionary<Guid, ParticipantState> Participants { get; } = new();
    }

    private sealed class ParticipantState
    {
        public ParticipantState(CollaborationParticipantPresence presence)
        {
            Presence = presence;
        }

        public CollaborationParticipantPresence Presence { get; set; }

        public Queue<CollaborationEventEnvelope> Outbox { get; } = new();
    }
}
