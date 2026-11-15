// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Events;
using Honua.Server.Features.Streaming;
using Honua.TestKit;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Streaming;

// One Pro host for the default streaming cases. Per-test hosts were the measured
// cost (about 6s of boot and schema seed each). Cases that need a different
// license, metadata graph or configuration still build their own host.
public sealed class FeatureStreamProHost : IAsyncLifetime
{
    private readonly TestLicenseEntitlementService _license = new(HonuaEdition.Pro);

    public WebAppFixture Fixture { get; }

    public FeatureStreamProHost()
    {
        // A fresh host starts its heartbeat clock with the case. This host lives for the
        // class, so the default 30s tick can land in the next frame a case reads as status.
        // Cases that assert a heartbeat call BroadcastHeartbeat themselves. Cases that need
        // the short scheduled interval build their own host.
        Fixture = new WebAppFixture()
            .ReplaceService<ILicenseEntitlementService>(_license)
            .ConfigureWebHost(builder => builder.UseSetting(
                "FeatureStreaming:HeartbeatInterval",
                "01:00:00"));
    }

    public Task InitializeAsync() => Fixture.InitializeAsync();

    public Task DisposeAsync() => Fixture.DisposeAsync();

    // Sessions and the in-memory event log are singletons on this host. Clear them
    // so each case still observes an empty stream, the same as a fresh host.
    public void ResetBetweenTests()
    {
        var sessions = Fixture.GetService<FeatureStreamSessionManager>();
        foreach (var session in sessions.GetSessions())
        {
            sessions.RemoveSession(session.SessionId, FeatureStreamDisconnectReason.ClientClosed);
        }

        if (Fixture.GetService<IFeatureChangeEventStore>() is not InMemoryFeatureChangeEventStore store)
        {
            throw new InvalidOperationException(
                "The shared streaming host requires the in-memory feature-change event store.");
        }

        store.ResetForTests();
    }
}
