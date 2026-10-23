// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Caching;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Caching;

[Protocol(TestProtocols.TestQuality)]
public sealed class CacheRefreshQueueCapacityTests
{
    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Cache)]
    public async Task TryEnqueueRefresh_FullQueue_RejectsOverflowWithoutRetainingClaims(bool distributedCoordinator)
    {
        var options = Options.Create(new CacheOptions { BackgroundRefreshEnabled = true });
        var monitor = Substitute.For<IPerformanceMonitor>();
#pragma warning disable CS0618 // Cover the retained local coordinator as well as the production implementation.
        using BackgroundService service = distributedCoordinator
            ? new DistributedCacheRefreshCoordinator(options, monitor, NullLogger<DistributedCacheRefreshCoordinator>.Instance)
            : new CacheRefreshCoordinator(options, monitor, NullLogger<CacheRefreshCoordinator>.Instance);
#pragma warning restore CS0618
        var coordinator = (ICacheRefreshCoordinator)service;
        var firstDequeued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Leave the worker stopped so saturation is deterministic and independent of host load.
        for (var index = 0; index < 1000; index++)
        {
            Assert.True(coordinator.TryEnqueueRefresh($"queued-{index}", _ =>
            {
                firstDequeued.TrySetResult();
                return Task.CompletedTask;
            }));
        }

        var acceptedOverflow = 0;
        for (var index = 0; index < 10000; index++)
        {
            if (coordinator.TryEnqueueRefresh($"overflow-{index}", static _ => Task.CompletedTask))
            {
                acceptedOverflow++;
            }
        }

        Assert.Equal(1000, coordinator.QueueDepth);
        Assert.Equal(0, acceptedOverflow);
        Assert.False(coordinator.TryClaimWriteBack("overflow-0"));

        await service.StartAsync(CancellationToken.None);
        try
        {
            await firstDequeued.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(coordinator.TryEnqueueRefresh("overflow-0", static _ => Task.CompletedTask));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}
