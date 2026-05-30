// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Rendering;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.MapServer.Rendering;

public sealed class RasterRenderCapacityLimiterTests
{
    [Fact]
    public async Task TryAcquireAsync_WaitsForReleasedSlotWithinTimeout()
    {
        using var limiter = new RasterRenderCapacityLimiter(
            maxConcurrentRenders: 1,
            maxReservedBytes: 1024,
            acquireTimeout: TimeSpan.FromSeconds(2));

        await using var firstLease = await limiter.TryAcquireAsync(1, 1, CancellationToken.None);
        firstLease.Should().NotBeNull();

        var pendingAcquire = limiter.TryAcquireAsync(1, 1, CancellationToken.None).AsTask();

        await firstLease!.DisposeAsync();
        await pendingAcquire.WaitAsync(TimeSpan.FromSeconds(1));

        await using var secondLease = await pendingAcquire;
        secondLease.Should().NotBeNull();
    }

    [Fact]
    public async Task TryAcquireAsync_WhenTimeoutExpires_ReturnsNull()
    {
        using var limiter = new RasterRenderCapacityLimiter(
            maxConcurrentRenders: 1,
            maxReservedBytes: 1024,
            acquireTimeout: TimeSpan.FromMilliseconds(10));

        await using var firstLease = await limiter.TryAcquireAsync(1, 1, CancellationToken.None);
        firstLease.Should().NotBeNull();

        await Task.Delay(25);

        var secondLease = await limiter.TryAcquireAsync(1, 1, CancellationToken.None);
        secondLease.Should().BeNull();
    }
}
