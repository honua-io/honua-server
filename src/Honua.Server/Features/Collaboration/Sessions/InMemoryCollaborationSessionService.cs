// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;

namespace Honua.Server.Features.Collaboration.Sessions;

/// <summary>
/// Node-local saved-map collaboration session state: per-map presence channels, bounded
/// per-participant outboxes, and the per-map strictly monotonic envelope sequence that stamps
/// every emitted <see cref="CollaborationEventEnvelope"/> (honua-server#2999).
/// </summary>
/// <remarks>
/// Multi-node scoping: the sequence counter is node-local and backplane broadcasts carry the
/// origin node's sequence unchanged, so strict per-map envelope monotonicity requires
/// single-writer-per-map routing (node affinity). See the remarks on
/// <see cref="CollaborationEventEnvelope"/> for the degradation contract without affinity.
/// </remarks>
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
    private readonly CollaborationCapabilities _capabilities;

    public InMemoryCollaborationSessionService(
        ISavedMapCollaborationAuthorizer authorizer,
        ICollaborationSessionClock clock,
        ICollaborationSessionBackplane? backplane = null,
        CollaborationCapabilities? capabilities = null)
    {
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _backplane = backplane ?? NullCollaborationSessionBackplane.Instance;
        // Advertised capabilities must match what the endpoints will actually accept: when the
        // deployment fails edits closed, the stream must not claim operations are available
        // (honua-server#2999 review).
        _capabilities = capabilities ?? CollaborationCapabilities.Default;
    }

    /// <summary>
    /// Capabilities advertised to every joining participant. Read paths must gate on the same
    /// flags they advertise: a surface that answers a request it declared unavailable is worse
    /// than one that refuses it, because the client cannot tell the answer is untrustworthy
    /// (honua-server#2999 review).
    /// </summary>
    public CollaborationCapabilities Capabilities => _capabilities;

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

        CollaborationSnapshot snapshot;
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

                joined = CreateEnvelopeLocked(
                    channel,
                    participant.SessionId,
                    participant.ParticipantId,
                    now,
                    new CollaborationSessionEvent
                    {
                        Type = CollaborationSessionEventTypes.ParticipantJoined,
                        Participant = CollaborationParticipantWire.FromPresence(participant)
                    });
                FanOutLocked(channel, joined, excludeSessionId: sessionId);
                snapshot = CreateSnapshotLocked(channel);
                break;
            }
        }

        _backplane.Publish(joined);

        return new CollaborationJoinResult
        {
            Authorization = authorization,
            Response = new CollaborationJoinResponse
            {
                MapId = mapId,
                SessionId = sessionId,
                ParticipantId = participant.ParticipantId,
                Capabilities = _capabilities,
                Participant = CollaborationParticipantWire.FromPresence(participant),
                Snapshot = snapshot
            }
        };
    }

    public CollaborationSnapshot GetSnapshot(string mapId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        // A read must not register a channel: a GetOrAdd here would leave an empty MapChannel in
        // _channels for every map id ever queried (nothing removes channels that never had a
        // participant), growing the singleton without bound.
        if (!_channels.TryGetValue(mapId, out var channel))
        {
            return new CollaborationSnapshot
            {
                MapId = mapId,
                Sequence = 0,
                Capabilities = _capabilities
            };
        }

        lock (channel.Sync)
        {
            return CreateSnapshotLocked(channel);
        }
    }

    /// <summary>
    /// Refreshes the participant's liveness timestamp. Heartbeats are intentionally not fanned
    /// out: the SDK event union has no heartbeat event, and presence freshness is re-established
    /// by snapshots.
    /// </summary>
    public void Heartbeat(Guid sessionId)
    {
        if (!TryResolveSession(sessionId, out _, out var channel))
        {
            throw new KeyNotFoundException($"Collaboration session '{sessionId}' was not found.");
        }

        lock (channel.Sync)
        {
            if (!channel.Participants.TryGetValue(sessionId, out var state))
            {
                throw new KeyNotFoundException($"Collaboration session '{sessionId}' was not found.");
            }

            state.Presence = state.Presence with { LastSeenAt = _clock.UtcNow };
        }
    }

    public CollaborationFanOutResult UpdateCursor(Guid sessionId, CollaborationCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        return UpdateParticipant(
            sessionId,
            (presence, now) => presence with { LastSeenAt = now, Cursor = cursor },
            presence => new CollaborationSessionEvent
            {
                Type = CollaborationSessionEventTypes.Cursor,
                ParticipantId = presence.ParticipantId,
                Cursor = cursor
            });
    }

    public CollaborationFanOutResult UpdateSelection(Guid sessionId, CollaborationSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return UpdateParticipant(
            sessionId,
            (presence, now) => presence with { LastSeenAt = now, Selection = selection },
            presence => new CollaborationSessionEvent
            {
                Type = CollaborationSessionEventTypes.Selection,
                ParticipantId = presence.ParticipantId,
                Selection = selection
            });
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

            if (!channel.Participants.TryGetValue(targetSessionId, out var target))
            {
                throw new KeyNotFoundException(
                    $"Collaboration follow target '{targetSessionId}' was not found in map '{mapId}'.");
            }

            var now = _clock.UtcNow;
            var updatedPresence = state.Presence with
            {
                LastSeenAt = now,
                FollowingSessionId = targetSessionId
            };
            state.Presence = updatedPresence;

            var ev = CreateEnvelopeLocked(
                channel,
                updatedPresence.SessionId,
                updatedPresence.ParticipantId,
                now,
                new CollaborationSessionEvent
                {
                    Type = CollaborationSessionEventTypes.Follow,
                    ParticipantId = updatedPresence.ParticipantId,
                    Follow = new CollaborationFollowTarget
                    {
                        TargetParticipantId = target.Presence.ParticipantId,
                        Following = true,
                        UpdatedAt = now
                    }
                });
            var delivered = FanOutLocked(channel, ev, excludeSessionId: sessionId);
            return Publish(new CollaborationFanOutResult { Event = ev, DeliveredCount = delivered });
        }
    }

    public CollaborationFanOutResult Unfollow(Guid sessionId)
    {
        return UpdateParticipant(
            sessionId,
            static (presence, now) => presence with
            {
                LastSeenAt = now,
                FollowingSessionId = null
            },
            presence => new CollaborationSessionEvent
            {
                Type = CollaborationSessionEventTypes.Follow,
                ParticipantId = presence.ParticipantId,
                Follow = new CollaborationFollowTarget { Following = false, UpdatedAt = presence.LastSeenAt }
            });
    }

    /// <summary>
    /// Removes a participant session. When <paramref name="requiredMapId"/> and/or
    /// <paramref name="requiredOwner"/> are supplied (the REST leave path), the session must
    /// belong to that map and to the same resolved user identity that joined it — participant
    /// ids equal the session id, so possession of the id is visible to every participant via
    /// snapshots and is NOT proof of ownership (honua-server#2999 review). A scope mismatch
    /// returns <see langword="false"/>, deliberately indistinguishable from an unknown session
    /// so probing cannot confirm a session exists. Server-internal callers (socket cleanup,
    /// pruning) omit the constraints.
    /// </summary>
    public bool Leave(
        Guid sessionId,
        string? reason = null,
        string? requiredMapId = null,
        ClaimsPrincipal? requiredOwner = null)
    {
        if (!TryResolveSession(sessionId, out var mapId, out var channel))
        {
            return false;
        }

        if (requiredMapId is not null && !string.Equals(mapId, requiredMapId, StringComparison.Ordinal))
        {
            return false;
        }

        CollaborationEventEnvelope left;
        lock (channel.Sync)
        {
            if (requiredOwner is not null &&
                channel.Participants.TryGetValue(sessionId, out var owned))
            {
                // Fail closed: without a resolvable, matching user identity the caller cannot
                // prove it owns this session.
                var callerUserId = ResolveUserId(requiredOwner);
                if (callerUserId is null ||
                    !string.Equals(owned.Presence.UserId, callerUserId, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            if (!channel.Participants.Remove(sessionId, out var state))
            {
                _sessionMapIndex.TryRemove(sessionId, out _);
                return false;
            }

            // Waking the leaving participant's writer lets it observe the removed session and
            // terminate promptly instead of blocking until its next idle timeout.
            state.OutboxSignal.Set();
            _sessionMapIndex.TryRemove(sessionId, out _);
            var now = _clock.UtcNow;
            ClearFollowsTargetingLocked(channel, sessionId, now);
            left = CreateEnvelopeLocked(
                channel,
                state.Presence.SessionId,
                state.Presence.ParticipantId,
                now,
                new CollaborationSessionEvent
                {
                    Type = CollaborationSessionEventTypes.ParticipantLeft,
                    ParticipantId = state.Presence.ParticipantId,
                    SessionId = state.Presence.SessionId.ToString("N")
                });
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
                    ClearFollowsTargetingLocked(channel, sessionId, now);

                    var left = CreateEnvelopeLocked(
                        channel,
                        state.Presence.SessionId,
                        state.Presence.ParticipantId,
                        now,
                        new CollaborationSessionEvent
                        {
                            Type = CollaborationSessionEventTypes.ParticipantLeft,
                            ParticipantId = state.Presence.ParticipantId,
                            SessionId = state.Presence.SessionId.ToString("N")
                        });
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

    /// <summary>
    /// Fans a committed op-log operation out to every participant of its map (including the
    /// submitter — the socket echoes the committed op so all clients apply the same server order)
    /// and broadcasts it to peer nodes. The op-log server cursor inside
    /// <paramref name="operation"/> is the authoritative edit order; the envelope sequence orders
    /// the local stream.
    /// </summary>
    public CollaborationEventEnvelope PublishOperation(string mapId, CollaborationOperationWire operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        ArgumentNullException.ThrowIfNull(operation);

        var now = _clock.UtcNow;
        var ev = new CollaborationSessionEvent
        {
            Type = CollaborationSessionEventTypes.OperationAppended,
            Operation = operation
        };

        CollaborationEventEnvelope envelope;
        if (_channels.TryGetValue(mapId, out var channel))
        {
            lock (channel.Sync)
            {
                envelope = CreateEnvelopeLocked(channel, sessionId: null, operation.AuthorId, now, ev);
                FanOutLocked(channel, envelope, excludeSessionId: Guid.Empty);
            }
        }
        else
        {
            // No local participants: still broadcast for peer nodes, sequencing off the op-log
            // cursor so the envelope remains ordered for any remote consumer.
            envelope = new CollaborationEventEnvelope
            {
                MapId = mapId,
                EventId = Guid.NewGuid().ToString("N"),
                Sequence = operation.Sequence,
                Cursor = operation.Cursor,
                ServerTime = now,
                ActorId = operation.AuthorId,
                Event = ev
            };
        }

        _backplane.Publish(envelope);
        return envelope;
    }

    /// <summary>
    /// Stamps a locally-delivered envelope (status/error/snapshot handshake frames) with the next
    /// per-map sequence. Falls back to a zero-sequence envelope when the map has no channel (the
    /// caller's session raced a prune); the reducer treats it as pre-snapshot and drops it.
    /// </summary>
    public CollaborationEventEnvelope StampEnvelope(
        string mapId,
        Guid? sessionId,
        string? actorId,
        CollaborationSessionEvent ev)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        ArgumentNullException.ThrowIfNull(ev);

        var now = _clock.UtcNow;
        if (_channels.TryGetValue(mapId, out var channel))
        {
            lock (channel.Sync)
            {
                return CreateEnvelopeLocked(channel, sessionId, actorId, now, ev);
            }
        }

        return new CollaborationEventEnvelope
        {
            MapId = mapId,
            EventId = Guid.NewGuid().ToString("N"),
            Sequence = 0,
            Cursor = "0",
            ServerTime = now,
            SessionId = sessionId?.ToString("N"),
            ActorId = actorId,
            Event = ev
        };
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
    /// so a participant does not receive an echo of its own action from a peer node. The envelope
    /// is re-stamped with THIS node's next per-map sequence before fan-out: the origin sequence
    /// is only monotonic on the origin node, and local clients — already past a higher local
    /// sequence from their own join/status/snapshot frames — would drop a lower-sequenced remote
    /// envelope per the v1 reducer contract, losing cross-replica operations (honua-server#2999).
    /// The event payload (including the authoritative op-log cursor inside operation events) is
    /// preserved unchanged.
    /// </summary>
    public void ApplyRemoteEvent(CollaborationEventEnvelope ev)
    {
        ArgumentNullException.ThrowIfNull(ev);
        if (!_channels.TryGetValue(ev.MapId, out var channel))
        {
            // No local participants for this map on this node; nothing to deliver.
            return;
        }

        var excludeSessionId = Guid.Empty;
        if (ev.SessionId is { Length: > 0 } sessionText &&
            Guid.TryParseExact(sessionText, "N", out var parsed))
        {
            excludeSessionId = parsed;
        }

        lock (channel.Sync)
        {
            if (channel.Removed)
            {
                return;
            }

            // Advance past BOTH streams before stamping: past this node's own sequence so local
            // clients (already beyond their snapshot sequence) accept the event, and past the
            // origin sequence so later local envelopes also stay ahead of anything a client may
            // have observed with origin stamping.
            var sequence = Math.Max(channel.LastSequence, ev.Sequence) + 1;
            channel.LastSequence = sequence;
            var restamped = ev with
            {
                Sequence = sequence,
                Cursor = sequence.ToString(CultureInfo.InvariantCulture)
            };
            FanOutLocked(channel, restamped, excludeSessionId);
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
        Func<CollaborationParticipantPresence, DateTimeOffset, CollaborationParticipantPresence> update,
        Func<CollaborationParticipantPresence, CollaborationSessionEvent> createEvent)
    {
        if (!TryResolveSession(sessionId, out _, out var channel))
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

            var ev = CreateEnvelopeLocked(
                channel,
                updatedPresence.SessionId,
                updatedPresence.ParticipantId,
                now,
                createEvent(updatedPresence));
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

            // Presence frames are disposable, but a committed operation (or a not-yet-delivered
            // resync notice) falling off the bounded outbox would permanently desync the client:
            // later operations keep arriving while a cursor range silently vanishes
            // (honua-server#2999). Turn any such eviction into an explicit resync-required error
            // so the client replays the op log from its last known cursor.
            var operationEvicted = false;
            while (state.Outbox.Count >= MaxOutboxDepth)
            {
                operationEvicted |= IsOperationBearing(state.Outbox.Dequeue());
            }

            // Queue order must stay sequence order: `ev` is already stamped, so the resync
            // notice — which necessarily draws a HIGHER sequence — has to follow it. Queuing the
            // notice first would hand the client S+1 before S, and the monotonic v1 reducer
            // would discard `ev` itself; for a cursor/selection/presence frame that loss is not
            // recoverable by replaying the operation log (honua-server#2999 review).
            state.Outbox.Enqueue(ev);

            if (operationEvicted)
            {
                // Keep the outbox at its cap now that the notice is being added as well.
                if (state.Outbox.Count >= MaxOutboxDepth)
                {
                    _ = IsOperationBearing(state.Outbox.Dequeue());
                }

                state.Outbox.Enqueue(CreateEnvelopeLocked(
                    channel,
                    state.Presence.SessionId,
                    state.Presence.ParticipantId,
                    ev.ServerTime,
                    new CollaborationSessionEvent
                    {
                        Type = CollaborationSessionEventTypes.Error,
                        Code = CollaborationErrorCodes.ResyncRequired,
                        Message = "Committed operations were evicted from the delivery buffer before this client drained them; replay the operation log from your last cursor.",
                        Terminal = false,
                        ResyncRequired = true
                    }));
            }

            state.OutboxSignal.Set();
            delivered++;
        }

        return delivered;

        static bool IsOperationBearing(CollaborationEventEnvelope evicted) =>
            evicted.Event.Type == CollaborationSessionEventTypes.OperationAppended ||
            evicted.Event.ResyncRequired == true;
    }

    private static int ClearFollowsTargetingLocked(
        MapChannel channel,
        Guid targetSessionId,
        DateTimeOffset now)
    {
        var cleared = 0;
        foreach (var state in channel.Participants.Values)
        {
            if (state.Presence.FollowingSessionId != targetSessionId)
            {
                continue;
            }

            var updatedPresence = state.Presence with
            {
                LastSeenAt = now,
                FollowingSessionId = null
            };
            state.Presence = updatedPresence;
            var ev = CreateEnvelopeLocked(
                channel,
                updatedPresence.SessionId,
                updatedPresence.ParticipantId,
                now,
                new CollaborationSessionEvent
                {
                    Type = CollaborationSessionEventTypes.Follow,
                    ParticipantId = updatedPresence.ParticipantId,
                    Follow = new CollaborationFollowTarget { Following = false, UpdatedAt = now }
                });
            FanOutLocked(channel, ev, excludeSessionId: Guid.Empty);
            cleared++;
        }

        return cleared;
    }

    private CollaborationSnapshot CreateSnapshotLocked(MapChannel channel)
    {
        var ordered = channel.Participants.Values
            .Select(static state => state.Presence)
            .OrderBy(static presence => presence.JoinedAt)
            .ThenBy(static presence => presence.ParticipantId, StringComparer.Ordinal)
            .ToArray();

        var cursors = new Dictionary<string, CollaborationCursor>(StringComparer.Ordinal);
        var selections = new Dictionary<string, CollaborationSelection>(StringComparer.Ordinal);
        var followTargets = new Dictionary<string, CollaborationFollowTarget>(StringComparer.Ordinal);
        foreach (var presence in ordered)
        {
            if (presence.Cursor is not null)
            {
                cursors[presence.ParticipantId] = presence.Cursor;
            }

            if (presence.Selection is not null)
            {
                selections[presence.ParticipantId] = presence.Selection;
            }

            if (presence.FollowingSessionId is { } target)
            {
                followTargets[presence.ParticipantId] = new CollaborationFollowTarget
                {
                    TargetParticipantId = target.ToString("N"),
                    Following = true
                };
            }
        }

        return new CollaborationSnapshot
        {
            MapId = channel.MapId,
            Sequence = channel.LastSequence,
            Capabilities = _capabilities,
            Participants = ordered.Select(CollaborationParticipantWire.FromPresence).ToArray(),
            Cursors = cursors,
            Selections = selections,
            FollowTargets = followTargets
        };
    }

    /// <summary>
    /// Builds a v1 envelope stamped with the channel's next strictly monotonic sequence. Callers
    /// must hold <c>channel.Sync</c> so sequence assignment and outbox delivery are atomic.
    /// </summary>
    private static CollaborationEventEnvelope CreateEnvelopeLocked(
        MapChannel channel,
        Guid? sessionId,
        string? actorId,
        DateTimeOffset now,
        CollaborationSessionEvent ev)
    {
        var sequence = ++channel.LastSequence;
        return new CollaborationEventEnvelope
        {
            MapId = channel.MapId,
            EventId = Guid.NewGuid().ToString("N"),
            Sequence = sequence,
            Cursor = sequence.ToString(CultureInfo.InvariantCulture),
            ServerTime = now,
            SessionId = sessionId?.ToString("N"),
            ActorId = actorId,
            Event = ev
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

    /// <summary>
    /// Resolves the stable identity used to decide who owns a presence record, most-specific
    /// first.
    /// </summary>
    /// <remarks>
    /// <c>api_key_id</c> must be preferred over <see cref="ClaimTypes.Name"/>:
    /// <c>ApiKeyAuthenticationHandler</c> gives EVERY full-admin API key the literal name
    /// <c>"admin"</c> and no <see cref="ClaimTypes.NameIdentifier"/>/<c>sub</c>, so falling
    /// through to the name collapses distinct admin collaborators onto one identity — and the
    /// ownership check on leave would then let one of them act on another's presence record.
    /// <c>api_key_id</c> is unique per key, so it separates them (honua-server#2999 review).
    /// The name stays as a last resort for principals that carry nothing more specific.
    /// </remarks>
    private static string? ResolveUserId(ClaimsPrincipal principal)
    {
        return NormalizeOptional(principal.FindFirstValue(ClaimTypes.NameIdentifier)) ??
            NormalizeOptional(principal.FindFirstValue("sub")) ??
            NormalizeOptional(principal.FindFirstValue("api_key_id")) ??
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
        /// Last stamped envelope sequence for this map on this node. Mutated only under
        /// <see cref="Sync"/>, which makes every locally emitted envelope strictly monotonic
        /// per map.
        /// </summary>
        public long LastSequence { get; set; }

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
