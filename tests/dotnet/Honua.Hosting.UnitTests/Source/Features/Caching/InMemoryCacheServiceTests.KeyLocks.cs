// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Caching;

public sealed partial class InMemoryCacheServiceTests
{
    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetOrSetAsync_CancelledWaiter_ReleasesItsKeyLockReference()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = _cacheService.GetOrSetAsync<MetadataV2Field>("item:cancel", async cancellationToken =>
        {
            await release.Task.WaitAsync(cancellationToken);
            return new MetadataV2Field { Name = "Held", Type = MetadataV2FieldType.String, Length = 10 };
        });
        using var cancellation = new CancellationTokenSource();
        var waiter = _cacheService.GetOrSetAsync<MetadataV2Field>(
            "item:cancel",
            _ => Task.FromResult<MetadataV2Field?>(null),
            cancellation.Token);

        await cancellation.CancelAsync();
        await FluentActions.Awaiting(() => waiter).Should().ThrowAsync<OperationCanceledException>();
        release.SetResult();
        await holder;

        KeyLockCount(_cacheService).Should().Be(0);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task Dispose_WhileAFactoryRuns_LetsTheCallerFinish()
    {
        var cache = new InMemoryCacheService(
            Options.Create(new CacheOptions { Enabled = true, KeyPrefix = "dispose:" }),
            NullLogger<InMemoryCacheService>.Instance,
            _performanceMonitor);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var caller = cache.GetOrSetAsync<MetadataV2Field>("item:dispose", async cancellationToken =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new MetadataV2Field { Name = "Late", Type = MetadataV2FieldType.String, Length = 10 };
        });
        await entered.Task;

        cache.Dispose();
        release.SetResult();

        await FluentActions.Awaiting(() => caller).Should().NotThrowAsync();
    }

    private static int KeyLockCount(InMemoryCacheService cacheService)
    {
        var keyLocksField = typeof(InMemoryCacheService).GetField("_keyLocks", BindingFlags.NonPublic | BindingFlags.Instance);
        keyLocksField.Should().NotBeNull();
        return ((System.Collections.ICollection)keyLocksField!.GetValue(cacheService)!).Count;
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetOrSetAsync_ConcurrentMisses_CallsFactoryOnce()
    {
        var factoryCalls = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<MetadataV2Field?> Factory(CancellationToken _)
        {
            Interlocked.Increment(ref factoryCalls);
            await release.Task;
            return new MetadataV2Field { Name = "computed", Type = MetadataV2FieldType.String, Length = 10 };
        }

        var callers = Enumerable.Range(0, 8)
            .Select(_ => _cacheService.GetOrSetAsync<MetadataV2Field>("item:stampede", Factory))
            .ToArray();
        release.SetResult();
        var results = await Task.WhenAll(callers);

        factoryCalls.Should().Be(1);
        results.Should().OnlyContain(result => result != null && result.Name == "computed");
        KeyLockCount(_cacheService).Should().Be(0, "a key lock leaves once its last holder or waiter leaves");
    }
}
