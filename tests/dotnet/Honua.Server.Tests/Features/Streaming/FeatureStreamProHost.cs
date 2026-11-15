// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Events;
using Honua.Server.Features.Streaming;
using Honua.TestKit;
using Honua.TestKit.Helpers;

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
        Fixture = new WebAppFixture()
            .ReplaceService<ILicenseEntitlementService>(_license);
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
