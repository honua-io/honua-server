// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Streaming;
using Honua.Server.Tests.Infrastructure.Telemetry;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Streaming;

// The periodic store read of an SSE session is skipped only while the task's relay has offered
// that session everything it has broadcast; every other case still reads the store.
public sealed class FeatureStreamSessionManagerStorePollTests : IDisposable
{
    private const int Buffer = 8;

    private readonly FeatureStreamSessionManager _manager = new(
        Options.Create(new FeatureStreamOptions { MaxBufferPerConnection = Buffer, ReplayBatchSize = 100 }),
        NullLogger<FeatureStreamSessionManager>.Instance,
        TestTelemetry.CreateFeatureStreamMetrics());

    private long _relayPosition;

    public void Dispose() => _manager.Dispose();

    [UnitTest]
    public void WithoutRelay_SessionReadsTheStore()
    {
        using var session = _manager.CreateSession("SSE", null);

        _manager.StorePollSkipCursor(session.SessionId, replayCursor: 100).Should().BeNull();
    }

    [UnitTest]
    public void RelayNotYetPositioned_SessionReadsTheStore()
    {
        using var session = _manager.CreateSession("SSE", null);
        _manager.AttachRelay(() => Volatile.Read(ref _relayPosition));

        _manager.StorePollSkipCursor(session.SessionId, replayCursor: 100).Should().BeNull();
    }

    [UnitTest]
    public void SessionAtOrPastRelayStart_SkipsTheStore_SessionBeforeItReadsOnce()
    {
        using var current = _manager.CreateSession("SSE", null);
        using var earlier = _manager.CreateSession("SSE", null);
        Attach(position: 50);

        _manager.StorePollSkipCursor(current.SessionId, replayCursor: 50).Should().NotBeNull();
        _manager.StorePollSkipCursor(earlier.SessionId, replayCursor: 40).Should().BeNull(
            "the session started before the relay and may have missed what came between");
    }

    [UnitTest]
    public void OfferedChange_IsCoveredWithoutAStoreRead()
    {
        using var session = _manager.CreateSession("SSE", null);
        Attach(position: 50);
        _manager.StorePollSkipCursor(session.SessionId, replayCursor: 50).Should().Be(50);
        _manager.Broadcast(FeatureStreamMessage.Data(Envelope(cursor: 51)));
        _manager.MarkRelayed(51);

        _manager.StorePollSkipCursor(session.SessionId, replayCursor: 50).Should().NotBeNull();
    }

    [UnitTest]
    public void RelayedChangeNotOfferedToSession_RequiresStoreRead()
    {
        Attach(position: 50);
        _manager.Broadcast(FeatureStreamMessage.Data(Envelope(cursor: 51)));
        _manager.MarkRelayed(51);
        // Joined after that broadcast, from a cursor before it.
        using var late = _manager.CreateSession("SSE", null);

        _manager.StorePollSkipCursor(late.SessionId, replayCursor: 50).Should().BeNull();
        _manager.StorePollSkipCursor(late.SessionId, replayCursor: 51).Should().NotBeNull();
    }

    [UnitTest]
    public void LaterLiveChange_DoesNotCoverRelayStartupGap()
    {
        using var session = _manager.CreateSession("SSE", null);
        Attach(position: 80);
        _manager.Broadcast(FeatureStreamMessage.Data(Envelope(cursor: 81)));
        _manager.MarkRelayed(81);
        session.Reader.TryRead(out _).Should().BeTrue();

        _manager.StorePollSkipCursor(session.SessionId, replayCursor: 50).Should().BeNull(
            "the live event cannot cover changes 51 to 80 skipped at relay startup");
        _manager.StorePollSkipCursor(session.SessionId, replayCursor: 81).Should().Be(81);
    }

    [UnitTest]
    public void LaterLiveChange_DoesNotCoverLateJoiningGap()
    {
        Attach(position: 50);
        _manager.Broadcast(FeatureStreamMessage.Data(Envelope(cursor: 51)));
        _manager.MarkRelayed(51);
        using var late = _manager.CreateSession("SSE", null);
        _manager.Broadcast(FeatureStreamMessage.Data(Envelope(cursor: 52)));
        _manager.MarkRelayed(52);
        late.Reader.TryRead(out _).Should().BeTrue();

        _manager.StorePollSkipCursor(late.SessionId, replayCursor: 50).Should().BeNull(
            "the late session missed change 51 even though change 52 reached it live");
        _manager.StorePollSkipCursor(late.SessionId, replayCursor: 52).Should().Be(52);
    }

    [UnitTest]
    public void JoiningBeforeRelayAcknowledgesBroadcast_StillRequiresDurableCoverage()
    {
        Attach(position: 50);
        _manager.Broadcast(FeatureStreamMessage.Data(Envelope(cursor: 51)));
        using var late = _manager.CreateSession("SSE", null);

        _manager.StorePollSkipCursor(late.SessionId, replayCursor: 50).Should().BeNull(
            "the session missed the broadcast even before MarkRelayed acknowledges it");
        _manager.MarkRelayed(51);
        _manager.Broadcast(FeatureStreamMessage.Data(Envelope(cursor: 52)));
        _manager.MarkRelayed(52);
        late.Reader.TryRead(out _).Should().BeTrue();

        _manager.StorePollSkipCursor(late.SessionId, replayCursor: 50).Should().BeNull();
        _manager.StorePollSkipCursor(late.SessionId, replayCursor: 51).Should().Be(52);
    }

    [UnitTest]
    public void LaterLiveChange_DoesNotCoverReattachmentGap()
    {
        using var session = _manager.CreateSession("SSE", null);
        Attach(position: 50);
        _manager.StorePollSkipCursor(session.SessionId, replayCursor: 50).Should().Be(50);
        _manager.DetachRelay();
        Attach(position: 80);
        _manager.Broadcast(FeatureStreamMessage.Data(Envelope(cursor: 81)));
        _manager.MarkRelayed(81);
        session.Reader.TryRead(out _).Should().BeTrue();

        _manager.StorePollSkipCursor(session.SessionId, replayCursor: 50).Should().BeNull(
            "the replacement attachment needs its own durable coverage");
        _manager.StorePollSkipCursor(session.SessionId, replayCursor: 81).Should().Be(81);
    }

    [UnitTest]
    public void UnrelayedChange_RequiresStoreRead_UntilTheSessionReadsPastIt()
    {
        using var session = _manager.CreateSession("SSE", null);
        Attach(position: 50);
        _manager.StorePollSkipCursor(session.SessionId, replayCursor: 50).Should().Be(50);
        _manager.MarkUnrelayed(51);
        _manager.Broadcast(FeatureStreamMessage.Data(Envelope(cursor: 52)));
        _manager.MarkRelayed(52);

        _manager.StorePollSkipCursor(session.SessionId, replayCursor: 50).Should().BeNull(
            "change 51 never reached the session live, so only the store has it");
        _manager.StorePollSkipCursor(session.SessionId, replayCursor: 51).Should().NotBeNull();
    }

    [UnitTest]
    public void DetachedRelay_SessionReadsTheStoreAgain()
    {
        using var session = _manager.CreateSession("SSE", null);
        Attach(position: 50);
        _manager.DetachRelay();

        _manager.StorePollSkipCursor(session.SessionId, replayCursor: 50).Should().BeNull();
    }

    [UnitTest]
    public void ReattachedRelay_UsesItsNewStart()
    {
        using var session = _manager.CreateSession("SSE", null);
        Attach(position: 50);
        _manager.StorePollSkipCursor(session.SessionId, replayCursor: 50).Should().NotBeNull();
        _manager.DetachRelay();
        Attach(position: 80);

        _manager.StorePollSkipCursor(session.SessionId, replayCursor: 50).Should().BeNull(
            "changes 51 to 80 were committed while no relay was attached");
    }

    [UnitTest]
    public void UnrelayedChangeDuringPoll_RequiresStoreReadBeforeAdvancing()
    {
        using var session = _manager.CreateSession("SSE", null);
        _manager.AttachRelay(() =>
        {
            _manager.MarkUnrelayed(51);
            _manager.Broadcast(FeatureStreamMessage.Data(Envelope(cursor: 52)));
            _manager.MarkRelayed(52);
            while (session.Reader.TryRead(out _))
            {
                // Complete delivery before the poll captures its proposed cursor.
            }
            return 50;
        });

        _manager.StorePollSkipCursor(session.SessionId, replayCursor: 50).Should().BeNull(
            "generation 52 cannot cover generation 51 that was never offered");
    }

    [UnitTest]
    public void SessionDropDuringPoll_RequiresStoreReadBeforeAdvancing()
    {
        using var session = _manager.CreateSession("SSE", null);
        _manager.AttachRelay(() =>
        {
            for (var cursor = 51; cursor <= 51 + Buffer; cursor++)
            {
                _manager.Broadcast(FeatureStreamMessage.Data(Envelope(cursor)));
                _manager.MarkRelayed(cursor);
            }
            while (session.Reader.TryRead(out _))
            {
                // Complete delivery before the poll captures its proposed cursor.
            }
            _manager.Broadcast(FeatureStreamMessage.Data(Envelope(cursor: 60)));
            _manager.MarkRelayed(60);
            while (session.Reader.TryRead(out _))
            {
                // Complete delivery before the poll captures its proposed cursor.
            }
            return 50;
        });

        _manager.StorePollSkipCursor(session.SessionId, replayCursor: 50).Should().BeNull(
            "a later offered generation cannot cover the change dropped during this poll");
    }

    [UnitTest]
    public void ReattachedRelayDuringPoll_DoesNotInstallTheDetachedStart()
    {
        using var session = _manager.CreateSession("SSE", null);
        _manager.AttachRelay(() =>
        {
            _manager.DetachRelay();
            _manager.AttachRelay(() => 80);
            return 50;
        });

        _manager.StorePollSkipCursor(session.SessionId, replayCursor: 50).Should().BeNull();
        _manager.StorePollSkipCursor(session.SessionId, replayCursor: 50).Should().BeNull(
            "the replacement relay began at 80, so changes 51 to 80 must be read from the store");
        _manager.StorePollSkipCursor(session.SessionId, replayCursor: 80).Should().Be(80);
    }

    [UnitTest]
    public void SkippedRead_MovesTheCursorOnlyOnceOfferedFramesAreWritten()
    {
        using var session = _manager.CreateSession("SSE", null);
        Attach(position: 50);
        _manager.StorePollSkipCursor(session.SessionId, replayCursor: 50).Should().Be(50);
        _manager.Broadcast(FeatureStreamMessage.Data(Envelope(cursor: 51)));
        _manager.MarkRelayed(51);

        _manager.StorePollSkipCursor(session.SessionId, replayCursor: 50).Should().Be(50,
            "change 51 is still queued, so the cursor must not pass it yet");
        session.Reader.TryRead(out _).Should().BeTrue();
        _manager.StorePollSkipCursor(session.SessionId, replayCursor: 50).Should().Be(51);
    }

    [UnitTest]
    public void ChangeDroppedFromAnOpenSession_RequiresStoreRead_EvenAfterLaterLiveChanges()
    {
        using var session = _manager.CreateSession("SSE", null);
        Attach(position: 50);
        _manager.StorePollSkipCursor(session.SessionId, replayCursor: 50).Should().Be(50);
        // Before drain starts a full channel drops silently and the session stays open.
        for (var cursor = 51; cursor <= 51 + Buffer; cursor++)
        {
            _manager.Broadcast(FeatureStreamMessage.Data(Envelope(cursor)));
            _manager.MarkRelayed(cursor);
        }
        while (session.Reader.TryRead(out _))
        {
            // Write out what was queued.
        }
        _manager.Broadcast(FeatureStreamMessage.Data(Envelope(cursor: 60)));
        _manager.MarkRelayed(60);
        session.Reader.TryRead(out _).Should().BeTrue();

        _manager.StorePollSkipCursor(session.SessionId, replayCursor: 50).Should().BeNull(
            $"change {51 + Buffer} was dropped, so only the store has it");
        _manager.StorePollSkipCursor(session.SessionId, replayCursor: 51 + Buffer).Should().Be(60);
    }

    [UnitTest]
    public void UnknownSession_ReadsTheStore()
    {
        Attach(position: 50);

        _manager.StorePollSkipCursor(Guid.NewGuid(), replayCursor: 50).Should().BeNull();
    }

    private void Attach(long position)
    {
        Volatile.Write(ref _relayPosition, position);
        _manager.AttachRelay(() => Volatile.Read(ref _relayPosition));
    }

    private static FeatureStreamEnvelope Envelope(long cursor) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        Cursor = cursor,
        Timestamp = DateTimeOffset.UtcNow,
        ServiceId = "test-svc",
        LayerId = 0,
        ObjectId = 1,
        Operation = "create",
        Protocol = "rest",
        RequestId = "req-1"
    };
}
