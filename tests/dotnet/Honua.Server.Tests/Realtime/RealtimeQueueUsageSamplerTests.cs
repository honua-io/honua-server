// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Server.Features.Realtime;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Realtime;

[Collection("Unit")]
public sealed class RealtimeQueueUsageSamplerTests
{
    [UnitTest]
    public async Task TwoNodes_OnlyLeaseHolderQueriesAndRenewalLossStopsSampling()
    {
        var store = new MaintenanceLease();
        var firstQueries = 0;
        var secondQueries = 0;
        using var first = new RealtimeQueueUsageSampler(store, _ =>
        {
            firstQueries++;
            return Task.FromResult(0.25);
        }, NullLogger<RealtimeQueueUsageSampler>.Instance);
        using var second = new RealtimeQueueUsageSampler(store, _ =>
        {
            secondQueries++;
            return Task.FromResult(0.25);
        }, NullLogger<RealtimeQueueUsageSampler>.Instance);

        await first.SampleOnceAsync(CancellationToken.None);
        await second.SampleOnceAsync(CancellationToken.None);
        await first.SampleOnceAsync(CancellationToken.None);
        firstQueries.Should().Be(2);
        secondQueries.Should().Be(0);
        store.AllowRenewal = false;

        await first.SampleOnceAsync(CancellationToken.None);

        firstQueries.Should().Be(2, "a lost maintenance lease must stop database sampling");
    }

    [UnitTest]
    public async Task FailedProbe_NextProbeRetriesWithoutLosingTheHost()
    {
        var calls = 0;
        using var sampler = new RealtimeQueueUsageSampler(new MaintenanceLease(), _ =>
        {
            if (++calls == 1)
            {
                throw new InvalidOperationException("Probe unavailable");
            }
            return Task.FromResult(0d);
        }, NullLogger<RealtimeQueueUsageSampler>.Instance);

        await sampler.SampleOnceAsync(CancellationToken.None);
        await sampler.SampleOnceAsync(CancellationToken.None);

        calls.Should().Be(2);
    }

    private sealed class MaintenanceLease : ILeaseStore
    {
        private string? _owner;
        public bool AllowRenewal { get; set; } = true;

        public Task<bool> TryAcquireAsync(LeaseClaim claim, TimeSpan duration, CancellationToken cancellationToken = default)
        {
            if (_owner is not null)
            {
                return Task.FromResult(false);
            }
            _owner = claim.OwnerId;
            return Task.FromResult(true);
        }

        public Task<bool> RenewAsync(LeaseClaim claim, TimeSpan duration, CancellationToken cancellationToken = default)
            => Task.FromResult(AllowRenewal && _owner == claim.OwnerId);

        public Task ReleaseAsync(LeaseClaim claim, CancellationToken cancellationToken = default)
        {
            if (_owner == claim.OwnerId)
            {
                _owner = null;
            }
            return Task.CompletedTask;
        }
    }
}
