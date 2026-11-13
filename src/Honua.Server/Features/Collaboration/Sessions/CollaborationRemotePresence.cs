// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text.Json;
using Honua.Core.Features.Realtime;

namespace Honua.Server.Features.Collaboration.Sessions;

internal sealed partial class InMemoryCollaborationSessionService
{
    internal static readonly TimeSpan RemotePresenceLifetime = TimeSpan.FromMinutes(2);
    private const int MaxRemoteParticipants = 10_000;
    private readonly string _originInstanceId = Guid.NewGuid().ToString("N");
    // Foreign sessions never enter the local ownership index or receive a local outbox.
    private readonly ConcurrentDictionary<(string Map, Guid Session), CollaborationParticipantPresence> _remotePresence = new();

    private void PublishPresence(CollaborationEventEnvelope envelope)
    {
        if (_provider is null || !Guid.TryParse(envelope.SessionId, out var id))
        {
            return;
        }

        CollaborationParticipantPresence? presence = null;
        if (_channels.TryGetValue(envelope.MapId, out var channel))
        {
            lock (channel.Sync)
            {
                if (channel.Participants.TryGetValue(id, out var state))
                {
                    presence = state.Presence with { UserId = null, ClientInstanceId = null };
                }
            }
        }

        // A complete participant state survives coalescing a join with a cursor/selection update.
        var message = new CollaborationPresenceMessage
        {
            OriginInstanceId = _originInstanceId,
            Event = envelope,
            Presence = presence
        };
        _provider.Enqueue(new RealtimeEventDraft(RealtimeFamily.CollabPresence, envelope.MapId,
            "presence", JsonSerializer.SerializeToElement(message,
                CollaborationSessionJsonContext.Default.CollaborationPresenceMessage), id.ToString("N")));
    }

    internal void ApplyRemotePresence(CollaborationPresenceMessage message)
    {
        if (message.OriginInstanceId == _originInstanceId ||
            !Guid.TryParse(message.Event.SessionId, out var sessionId))
        {
            return;
        }

        var key = (message.Event.MapId, sessionId);
        if (message.Presence is not { } presence)
        {
            RemoveRemoteParticipant(key);
            return;
        }

        if (presence.SessionId != sessionId || _clock.UtcNow - presence.LastSeenAt > RemotePresenceLifetime)
        {
            return;
        }

        _remotePresence.TryGetValue(key, out var previous);
        if (previous is null && _remotePresence.Count >= MaxRemoteParticipants)
        {
            PruneRemotePresence();
            if (_remotePresence.Count >= MaxRemoteParticipants)
            {
                return;
            }
        }

        _remotePresence[key] = presence;
        if (previous is null)
        {
            Deliver(new CollaborationSessionEvent
            {
                Type = CollaborationSessionEventTypes.ParticipantJoined,
                Participant = CollaborationParticipantWire.FromPresence(presence)
            });
        }

        if (presence.Cursor is not null && presence.Cursor != previous?.Cursor)
        {
            Deliver(new CollaborationSessionEvent { Type = CollaborationSessionEventTypes.Cursor, Cursor = presence.Cursor });
        }

        if (presence.Selection is not null && presence.Selection != previous?.Selection)
        {
            Deliver(new CollaborationSessionEvent { Type = CollaborationSessionEventTypes.Selection, Selection = presence.Selection });
        }

        if (presence.FollowingSessionId != previous?.FollowingSessionId)
        {
            Deliver(new CollaborationSessionEvent
            {
                Type = CollaborationSessionEventTypes.Follow,
                Follow = new CollaborationFollowTarget
                {
                    Following = presence.FollowingSessionId.HasValue,
                    TargetParticipantId = presence.FollowingSessionId?.ToString("N")
                }
            });
        }

        void Deliver(CollaborationSessionEvent ev) => ApplyRemoteEvent(message.Event with
        {
            Event = ev with { ParticipantId = presence.ParticipantId }
        });
    }

    private bool HasRemoteParticipant(string mapId, Guid sessionId) =>
        _remotePresence.TryGetValue((mapId, sessionId), out var presence) &&
        _clock.UtcNow - presence.LastSeenAt <= RemotePresenceLifetime;

    private IEnumerable<CollaborationParticipantPresence> GetRemoteParticipants(string mapId) =>
        _remotePresence.Where(pair => pair.Key.Map == mapId &&
            _clock.UtcNow - pair.Value.LastSeenAt <= RemotePresenceLifetime).Select(pair => pair.Value);

    internal void PruneRemotePresence()
    {
        foreach (var pair in _remotePresence)
        {
            if (_clock.UtcNow - pair.Value.LastSeenAt > RemotePresenceLifetime)
            {
                RemoveRemoteParticipant(pair.Key);
            }
        }
    }

    private void RemoveRemoteParticipant((string Map, Guid Session) key)
    {
        if (!_remotePresence.TryRemove(key, out var presence) || !_channels.TryGetValue(key.Map, out var channel))
        {
            return;
        }

        lock (channel.Sync)
        {
            ClearFollowsTargetingLocked(channel, key.Session, _clock.UtcNow);
            var envelope = CreateEnvelopeLocked(channel, key.Session, presence.ParticipantId, _clock.UtcNow,
                new CollaborationSessionEvent { Type = CollaborationSessionEventTypes.ParticipantLeft, ParticipantId = presence.ParticipantId });
            FanOutLocked(channel, envelope, Guid.Empty);
        }
    }

    internal void RequireResync(bool clearPresence)
    {
        if (clearPresence)
        {
            _remotePresence.Clear();
        }

        foreach (var channel in _channels.Values)
        {
            lock (channel.Sync)
            {
                var envelope = CreateEnvelopeLocked(channel, null, null, _clock.UtcNow,
                    new CollaborationSessionEvent
                    {
                        Type = CollaborationSessionEventTypes.Error,
                        Code = CollaborationErrorCodes.ResyncRequired,
                        ResyncRequired = true,
                        Message = "Collaboration delivery was interrupted. Reconnect with the last operation cursor."
                    });
                FanOutLocked(channel, envelope, Guid.Empty);
            }
        }
    }
}
