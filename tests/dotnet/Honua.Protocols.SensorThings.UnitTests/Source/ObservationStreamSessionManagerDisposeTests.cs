// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Realtime;
using Honua.Protocols.SensorThings.Streaming;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

public sealed class ObservationStreamSessionManagerDisposeTests
{
    [UnitTest]
    public async Task SubscribeFailure_ReleasesAdmissionSlot()
    {
        var provider = Substitute.For<IRealtimeFanoutProvider>();
        provider.Subscribe(Arg.Any<RealtimeSubscriptionRequest>())
            .Returns(_ => throw new InvalidOperationException("unavailable"));
        await using var manager = new ObservationStreamSessionManager(
            NullLogger<ObservationStreamSessionManager>.Instance, provider);
        Assert.Throws<InvalidOperationException>(() => manager.TryCreateSession(
            new("SSE", null, new("tenant-a", null), "alice", null), out _));
        Assert.Equal(0, manager.SessionCount);
    }
}
