// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Events;
using Honua.Server.Features.Streaming;
using Honua.TestKit;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Streaming;

// Default streaming cases share a Pro host. Cases that need a different
// license, metadata graph or configuration build their own host.
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
            .ConfigureServices(services =>
            {
                services.RemoveAll<IFeatureChangeEventStore>();
                services.AddSingleton<IFeatureChangeEventStore>(provider =>
                    new ResettableFeatureChangeEventStore(
                        provider.GetRequiredService<IOptions<FeatureChangeEventOptions>>()));
            })
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

        if (Fixture.GetService<IFeatureChangeEventStore>() is not ResettableFeatureChangeEventStore store)
        {
            throw new InvalidOperationException(
                "The shared streaming host requires the in-memory feature-change event store.");
        }

        store.Reset();
    }

    private sealed class ResettableFeatureChangeEventStore(IOptions<FeatureChangeEventOptions> options)
        : IFeatureChangeEventStore, IFeatureChangeEventStoreHealth, IDisposable
    {
        private InMemoryFeatureChangeEventStore _store = new(options);

        private InMemoryFeatureChangeEventStore Current => Volatile.Read(ref _store);

        public bool CanPersistEvents => Current.CanPersistEvents;

        public bool IsUsingInMemoryFallback => Current.IsUsingInMemoryFallback;

        public void Reset() => Interlocked.Exchange(
            ref _store, new InMemoryFeatureChangeEventStore(options)).Dispose();

        public Task<FeatureChangeEvent> AppendAsync(
            FeatureChangeEventRequest request, CancellationToken cancellationToken = default)
            => Current.AppendAsync(request, cancellationToken);

        public Task<long> GetCurrentCursorAsync(CancellationToken cancellationToken = default)
            => Current.GetCurrentCursorAsync(cancellationToken);

        public Task<long> GetOldestRetainedCursorAsync(CancellationToken cancellationToken = default)
            => Current.GetOldestRetainedCursorAsync(cancellationToken);

        public Task<FeatureChangeRetentionWindow> GetRetentionWindowAsync(
            CancellationToken cancellationToken = default)
            => Current.GetRetentionWindowAsync(cancellationToken);

        public Task<IReadOnlyList<FeatureChangeEvent>> QueryAsync(
            long? cursor, DateTimeOffset? from, DateTimeOffset? to, int limit,
            CancellationToken cancellationToken = default)
            => Current.QueryAsync(cursor, from, to, limit, cancellationToken);

        public void Dispose() => Current.Dispose();
    }
}
