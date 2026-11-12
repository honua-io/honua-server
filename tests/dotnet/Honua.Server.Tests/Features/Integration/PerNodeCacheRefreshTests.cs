// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Integration;

[Protocol(TestProtocols.TestQuality)]
[Collection("Performance")]
public sealed class PerNodeCacheRefreshTests
{
    [UnitTest]
    [Operation(Operations.Cache, Operations.Infrastructure)]
    public async Task CacheCoordination_MultipleNodes_EachNodeRefreshesForItself()
    {
        var refreshCount = 0;
        var options = Options.Create(new CacheOptions
        {
            BackgroundRefreshEnabled = true,
            MaxConcurrentRefreshes = 2,
            RefreshTimeoutSeconds = 5
        });
        using var coordinator1 = CreateCoordinator(options);
        using var coordinator2 = CreateCoordinator(options);
        using var cts = new CancellationTokenSource();
        _ = coordinator1.StartAsync(cts.Token);
        _ = coordinator2.StartAsync(cts.Token);

        var enqueued1 = coordinator1.TryEnqueueRefresh("layer:1", _ =>
        {
            Interlocked.Increment(ref refreshCount);
            return Task.CompletedTask;
        });
        var enqueued2 = coordinator2.TryEnqueueRefresh("layer:1", _ =>
        {
            Interlocked.Increment(ref refreshCount);
            return Task.CompletedTask;
        });

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && Volatile.Read(ref refreshCount) < 2)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        enqueued1.Should().BeTrue();
        enqueued2.Should().BeTrue();
        refreshCount.Should().Be(2, "each node deduplicates only its own refreshes");
        await cts.CancelAsync();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void CacheInvalidation_OnAnotherNode_LeavesThisNodesPendingRefreshUntouched()
    {
        var options = Options.Create(new CacheOptions { BackgroundRefreshEnabled = true });
        using var coordinator1 = CreateCoordinator(options);
        using var coordinator2 = CreateCoordinator(options);
        coordinator1.TryEnqueueRefresh("layer:1", _ => Task.CompletedTask);

        coordinator2.NotifyInvalidation("layer:1");

        coordinator1.WasInvalidated("layer:1").Should().BeFalse("invalidation state is per node");
        coordinator2.WasInvalidated("layer:1").Should().BeFalse("coordinator2 never had a pending refresh for this key");
    }

    private static CacheRefreshCoordinator CreateCoordinator(IOptions<CacheOptions> options)
        => new(options, Substitute.For<IPerformanceMonitor>(), NullLogger<CacheRefreshCoordinator>.Instance);
}
