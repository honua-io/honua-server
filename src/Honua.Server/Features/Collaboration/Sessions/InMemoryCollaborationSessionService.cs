// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Security.Claims;

namespace Honua.Server.Features.Collaboration.Sessions;

internal sealed class InMemoryCollaborationSessionService
{
    // Per-participant outbox cap. Outboxes are only drained by the participant's own poll; a
    // participant that never drains (crashed client, no events surface wired) must not grow the
    // singleton service without bound, so the oldest events are dropped once the cap is reached.
    internal const int MaxOutboxDepth = 256;

    private readonly ConcurrentDictionary<string, MapChannel> _channels = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, string> _sessionMapIndex = new();
    private readonly ISavedMapCollaborationAuthorizer _authorizer;
    private readonly ICollaborationSessionClock _clock;
    private readonly ICollaborationSessionBackplane _backplane;

    public InMemoryCollaborationSessionService(
        ISavedMapCollaborationAuthorizer authorizer,
        ICollaborationSessionClock clock,
        ICollaborationSessionBackplane? backplane = null)
    {
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _backplane = backplane ?? NullCollaborationSessionBackplane.Instance;
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

        CollaborationSessionSnapshot snapshot;
        CollaborationEventEnvelope joined;
        while (true)
        {
            var channel = _channels.GetOrAdd(mapId, static id => new MapChannel(id));
            lock (channel.Sync)
            {
                if (channel.Removed)
                {
                    // Leave/prune emptied this channel and unregistered it from _channels after we
                    // resolved it but before we acquired its lock. Joining the orphaned instance
                    // would make the session invisible to every later TryResolveSession lookup and
                    // permanently leak its _sessionMapIndex entry, so re-resolve and retry.
                    continue;
                }

                channel.Participants.Add(sessionId, new ParticipantState(participant));
                _sessionMapIndex[sessionId] = mapId;

                joined = CreateEvent(
                    CollaborationSessionEventTypes.ParticipantJoined,
                    mapId,
                    participant,
                    now) with
                { Participant = participant };
                FanOutLocked(channel, joined, excludeSessionId: sessionId);
                snapshot = CreateSnapshotLocked(channel, now);
                break;
            }
        }

        _backplane.Publish(joined);

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
        // A read must not register a channel: a GetOrAdd here would leave an empty MapChannel in
        // _channels for every map id ever queried (nothing removes channels that never had a
        // participant), growing the singleton without bound.
        if (!_channels.TryGetValue(mapId, out var channel))
        {
            return new CollaborationSessionSnapshot
            {
                MapId = mapId,
                Participants = [],
                GeneratedAt = now
            };
        }

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
            return Publish(new CollaborationFanOutResult { Event = ev, DeliveredCount = delivered });
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

        CollaborationEventEnvelope left;
        lock (channel.Sync)
        {
            if (!channel.Participants.Remove(sessionId, out var state))
            {
                _sessionMapIndex.TryRemove(sessionId, out _);
                return false;
            }

            // Waking the leaving participant's writer lets it observe the removed session and
            // terminate promptly instead of blocking until its next idle timeout.
            state.OutboxSignal.Set();
            _sessionMapIndex.TryRemove(sessionId, out _);
            ClearFollowsTargetingLocked(channel, mapId, sessionId, _clock.UtcNow);
            left = CreateEvent(
                CollaborationSessionEventTypes.ParticipantLeft,
                mapId,
                state.Presence,
                _clock.UtcNow) with
            { Participant = state.Presence };
            FanOutLocked(channel, left, excludeSessionId: sessionId);
            RemoveChannelIfEmpty(mapId, channel);
        }

        _backplane.Publish(left);
        return true;
    }

    public int PruneStaleParticipants(TimeSpan maxIdle)
    {
        if (maxIdle <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxIdle), "Stale participant timeout must be positive.");
        }

        var now = _clock.UtcNow;
        var removed = 0;
        var publishQueue = new List<CollaborationEventEnvelope>();
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

                    // Wake the pruned participant's writer so a stale WebSocket terminates promptly.
                    state.OutboxSignal.Set();
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
                    publishQueue.Add(left);
                }

                RemoveChannelIfEmpty(mapId, channel);
            }
        }

        foreach (var ev in publishQueue)
        {
            _backplane.Publish(ev);
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
            // Reset the signal only while holding the lock so a concurrent enqueue either
            // happens-before this drain (already captured above) or re-sets afterwards.
            state.OutboxSignal.Reset();
            return events;
        }
    }

    /// <summary>
    /// Waits until the participant's outbox has pending events or the session ends. Returns
    /// <see langword="false"/> when the participant is no longer present (left or pruned), which
    /// the WebSocket writer treats as a terminal condition. Used by the streaming transport to
    /// avoid busy-polling <see cref="DrainEvents"/>.
    /// </summary>
    public async Task<bool> WaitForEventsAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (!TryResolveSession(sessionId, out _, out var channel))
        {
            return false;
        }

        AsyncManualResetSignal signal;
        lock (channel.Sync)
        {
            if (!channel.Participants.TryGetValue(sessionId, out var state))
            {
                return false;
            }

            if (state.Outbox.Count > 0)
            {
                return true;
            }

            signal = state.OutboxSignal;
        }

        await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        return TryResolveSession(sessionId, out _, out _);
    }

    /// <summary>
    /// Applies a collaboration event that originated on another node (delivered over the Redis
    /// backplane) to the local participant outboxes for its map. Remote events are never
    /// re-published, preventing fan-out loops between nodes. The actor's own session is excluded
    /// so a participant does not receive an echo of its own action from a peer node.
    /// </summary>
    public void ApplyRemoteEvent(CollaborationEventEnvelope ev)
    {
        ArgumentNullException.ThrowIfNull(ev);
        if (!_channels.TryGetValue(ev.MapId, out var channel))
        {
            // No local participants for this map on this node; nothing to deliver.
            return;
        }

        lock (channel.Sync)
        {
            if (channel.Removed)
            {
                return;
            }

            FanOutLocked(channel, ev, excludeSessionId: ev.SessionId ?? Guid.Empty);
        }
    }

    private CollaborationFanOutResult Publish(CollaborationFanOutResult result)
    {
        // Cross-node fan-out happens after the local outboxes have been written so a single-node
        // deployment never pays for the backplane round-trip on its hot path. The backplane is
        // best-effort: a Redis outage degrades to local-only delivery, mirroring the feature-stream
        // cluster-broadcast contract.
        _backplane.Publish(result.Event);
        return result;
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
            return Publish(new CollaborationFanOutResult { Event = ev, DeliveredCount = delivered });
        }
    }

    private bool TryResolveSession(Guid sessionId, out string mapId, out MapChannel channel)
    {
        channel = null!;
        if (!_sessionMapIndex.TryGetValue(sessionId, out mapId!))
        {
            mapId = string.Empty;
            return false;
        }

        if (_channels.TryGetValue(mapId, out channel!))
        {
            return true;
        }

        // The index entry points at a channel that has been emptied and unregistered; the session
        // is gone, so drop the dangling mapping instead of leaking it forever.
        _sessionMapIndex.TryRemove(sessionId, out _);
        return false;
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

            while (state.Outbox.Count >= MaxOutboxDepth)
            {
                state.Outbox.Dequeue();
            }

            state.Outbox.Enqueue(ev);
            state.OutboxSignal.Set();
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
        // Caller holds channel.Sync. Marking the channel removed under the lock lets a concurrent
        // JoinAsync that already resolved this instance detect the removal and retry against a
        // fresh channel instead of joining an orphan that is no longer reachable via _channels.
        if (channel.Participants.Count == 0)
        {
            channel.Removed = true;
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

        /// <summary>
        /// Set under <see cref="Sync"/> when the channel has been unregistered from the channel
        /// map so a racing join can detect the orphaned instance and retry.
        /// </summary>
        public bool Removed { get; set; }
    }

    private sealed class ParticipantState
    {
        public ParticipantState(CollaborationParticipantPresence presence)
        {
            Presence = presence;
        }

        public CollaborationParticipantPresence Presence { get; set; }

        public Queue<CollaborationEventEnvelope> Outbox { get; } = new();

        /// <summary>
        /// Pulsed whenever an event is enqueued onto <see cref="Outbox"/> so a WebSocket
        /// writer loop can wake without polling. The signal is set when events are pending
        /// and reset by the writer when it drains them.
        /// </summary>
        public AsyncManualResetSignal OutboxSignal { get; } = new();
    }

    /// <summary>
    /// Minimal manual-reset async signal used to wake a single WebSocket writer when its
    /// participant outbox receives events. Multiple sets coalesce; a single wait observes them.
    /// </summary>
    internal sealed class AsyncManualResetSignal
    {
        private volatile TaskCompletionSource<bool> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Set() => _tcs.TrySetResult(true);

        public Task WaitAsync(CancellationToken cancellationToken)
        {
            var tcs = _tcs;
            if (tcs.Task.IsCompleted)
            {
                return tcs.Task;
            }

            // Replace the completed source on the next reset cycle; the waiter races with Set,
            // and a coalesced Set is acceptable because the writer always drains the full outbox.
            return tcs.Task.WaitAsync(cancellationToken);
        }

        public void Reset()
        {
            var current = _tcs;
            if (current.Task.IsCompleted)
            {
                Interlocked.CompareExchange(
                    ref _tcs,
                    new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
                    current);
            }
        }
    }
}
