// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Server.Features.Streaming;
using Honua.TestKit;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Streaming;

// Default streaming cases share a Pro host. Cases that need a different license,
// metadata graph or configuration share a keyed variant host or build their own.
public sealed class FeatureStreamProHost : IAsyncLifetime
{
    private readonly TestLicenseEntitlementService _license = new(HonuaEdition.Pro);
    private readonly Dictionary<string, Task<WebAppFixture>> _variants = new(StringComparer.Ordinal);
    private readonly List<WebAppFixture> _variantFixtures = [];

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

    public async Task DisposeAsync()
    {
        foreach (var variant in _variantFixtures)
        {
            await variant.DisposeAsync();
        }

        await Fixture.DisposeAsync();
    }

    // Cases that need the same non-default host share one host per variant key. A variant
    // starts on first use, so a shard whose filter skips those cases never boots it. Each
    // acquisition drops sessions an earlier case left on the variant.
    public async Task<WebAppFixture> GetVariantAsync(string key, Func<WebAppFixture> create)
    {
        Task<WebAppFixture> started;
        lock (_variants)
        {
            if (!_variants.TryGetValue(key, out started!))
            {
                var fixture = create();
                _variantFixtures.Add(fixture);
                started = StartAsync(fixture);
                _variants[key] = started;
            }
        }

        var variant = await started;
        DropSessions(variant);
        return variant;
    }

    private static async Task<WebAppFixture> StartAsync(WebAppFixture fixture)
    {
        await fixture.InitializeAsync();
        return fixture;
    }

    // Sessions belong to this application and must not leak between sequential cases.
    // The production PostgreSQL event log survives a host restart too: keep its rows
    // and monotonic generations, including the realtime relay's durable position.
    public void ResetBetweenTests() => DropSessions(Fixture);

    // A shared variant keeps running after the case, so drop the sessions the case opened.
    public static void DropSessions(WebAppFixture fixture)
    {
        var sessions = fixture.GetService<FeatureStreamSessionManager>();
        foreach (var session in sessions.GetSessions())
        {
            sessions.RemoveSession(session.SessionId, FeatureStreamDisconnectReason.ClientClosed);
        }
    }
}
