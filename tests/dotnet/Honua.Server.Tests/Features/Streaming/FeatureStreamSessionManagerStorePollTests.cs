// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Streaming;
using Honua.Server.Tests.Infrastructure.Telemetry;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Streaming;

/// <summary>
/// The periodic store read of an SSE session is skipped only while the task's relay has offered
/// that session everything it has broadcast; every other case still reads the store.
/// </summary>
public sealed class FeatureStreamSessionManagerStorePollTests : IDisposable
{
    private readonly FeatureStreamSessionManager _manager = new(
        Options.Create(new FeatureStreamOptions { MaxBufferPerConnection = 64, ReplayBatchSize = 100 }),
        NullLogger<FeatureStreamSessionManager>.Instance,
        TestTelemetry.CreateFeatureStreamMetrics());

    private long _relayPosition;

    public void Dispose() => _manager.Dispose();

    [UnitTest]
    public void WithoutRelay_SessionReadsTheStore()
    {
        using var session = _manager.CreateSession("SSE", null);

        _manager.NeedsStorePoll(session.SessionId, replayCursor: 100).Should().BeTrue();
    }

    [UnitTest]
    public void RelayNotYetPositioned_SessionReadsTheStore()
    {
        using var session = _manager.CreateSession("SSE", null);
        _manager.AttachRelay(() => Volatile.Read(ref _relayPosition));

        _manager.NeedsStorePoll(session.SessionId, replayCursor: 100).Should().BeTrue();
    }

    [UnitTest]
    public void SessionAtOrPastRelayStart_SkipsTheStore_SessionBeforeItReadsOnce()
    {
        using var current = _manager.CreateSession("SSE", null);
        using var earlier = _manager.CreateSession("SSE", null);
        Attach(position: 50);

        _manager.NeedsStorePoll(current.SessionId, replayCursor: 50).Should().BeFalse();
        _manager.NeedsStorePoll(earlier.SessionId, replayCursor: 40).Should().BeTrue(
            "the session started before the relay and may have missed what came between");
    }

    [UnitTest]
    public void OfferedChange_IsCoveredWithoutAStoreRead()
    {
        using var session = _manager.CreateSession("SSE", null);
        Attach(position: 50);
        _manager.Broadcast(FeatureStreamMessage.Data(Envelope(cursor: 51)));
        _manager.MarkRelayed(51);

        _manager.NeedsStorePoll(session.SessionId, replayCursor: 50).Should().BeFalse();
    }

    [UnitTest]
    public void RelayedChangeNotOfferedToSession_RequiresStoreRead()
    {
        Attach(position: 50);
        _manager.Broadcast(FeatureStreamMessage.Data(Envelope(cursor: 51)));
        _manager.MarkRelayed(51);
        // Joined after that broadcast, from a cursor before it.
        using var late = _manager.CreateSession("SSE", null);

        _manager.NeedsStorePoll(late.SessionId, replayCursor: 50).Should().BeTrue();
        _manager.NeedsStorePoll(late.SessionId, replayCursor: 51).Should().BeFalse();
    }

    [UnitTest]
    public void UnrelayedChange_RequiresStoreRead_UntilTheSessionReadsPastIt()
    {
        using var session = _manager.CreateSession("SSE", null);
        Attach(position: 50);
        _manager.MarkUnrelayed(51);
        _manager.Broadcast(FeatureStreamMessage.Data(Envelope(cursor: 52)));
        _manager.MarkRelayed(52);

        _manager.NeedsStorePoll(session.SessionId, replayCursor: 50).Should().BeTrue(
            "change 51 never reached the session live, so only the store has it");
        _manager.NeedsStorePoll(session.SessionId, replayCursor: 51).Should().BeFalse();
    }

    [UnitTest]
    public void DetachedRelay_SessionReadsTheStoreAgain()
    {
        using var session = _manager.CreateSession("SSE", null);
        Attach(position: 50);
        _manager.DetachRelay();

        _manager.NeedsStorePoll(session.SessionId, replayCursor: 50).Should().BeTrue();
    }

    [UnitTest]
    public void ReattachedRelay_UsesItsNewStart()
    {
        using var session = _manager.CreateSession("SSE", null);
        Attach(position: 50);
        _manager.NeedsStorePoll(session.SessionId, replayCursor: 50).Should().BeFalse();
        _manager.DetachRelay();
        Attach(position: 80);

        _manager.NeedsStorePoll(session.SessionId, replayCursor: 50).Should().BeTrue(
            "changes 51 to 80 were committed while no relay was attached");
    }

    [UnitTest]
    public void UnknownSession_ReadsTheStore()
    {
        Attach(position: 50);

        _manager.NeedsStorePoll(Guid.NewGuid(), replayCursor: 50).Should().BeTrue();
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
