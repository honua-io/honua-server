// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Edit.Receipts;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Core.Tests.Features.Edit;

public sealed class FeatureEditReceiptCacheTests
{
    [UnitTest]
    public async Task EnterAsync_LocalCommitWinsAndBuffersAreOwned()
    {
        var durable = Substitute.For<IFeatureEditIdempotencyReceipts>();
        using var cache = CreateCache(durable);
        byte[] original = [1, 2];
        using (var entry = await cache.EnterAsync("key", CancellationToken.None))
        {
            entry.Complete(original, original.Length);
        }

        original[0] = 9;
        durable.ClearReceivedCalls();
        durable.TryReadAsync("key", Arg.Any<CancellationToken>()).Returns(new ReadOnlyMemory<byte>([3]));
        using (var replay = await cache.EnterAsync("key", CancellationToken.None))
        {
            replay.Recorded.Payload.ToArray().Should().Equal(1, 2);
            replay.Recorded.State.Should().Be(FeatureEditReceiptCache.ReceiptState.LocalRecorded);
            // A caller can access a ReadOnlyMemory backing array; it must not be the cache's buffer.
            System.Runtime.InteropServices.MemoryMarshal.TryGetArray(replay.Recorded.Payload, out var segment).Should().BeTrue();
            segment.Array![0] = 8;
        }

        using var next = await cache.EnterAsync("key", CancellationToken.None);
        next.Recorded.Payload.ToArray().Should().Equal(1, 2);
        await durable.DidNotReceive().TryReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task EnterAsync_DurableReceiptSupersedesLocalUnknownAndUncommittedFailure()
    {
        var durable = Substitute.For<IFeatureEditIdempotencyReceipts>();
        using var cache = CreateCache(durable);
        using (var unknown = await cache.EnterAsync("unknown", CancellationToken.None))
        {
            unknown.RetainAmbiguous();
        }
        using (var failure = await cache.EnterAsync("failure", CancellationToken.None))
        {
            failure.CompleteUncommitted(new byte[] { 1 }, 1);
        }

        durable.TryReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ReadOnlyMemory<byte>([7]));
        foreach (var key in new[] { "unknown", "failure" })
        {
            using var replay = await cache.EnterAsync(key, CancellationToken.None);
            replay.Recorded.State.Should().Be(FeatureEditReceiptCache.ReceiptState.Recorded);
            replay.Recorded.Payload.ToArray().Should().Equal(7);
        }
    }

    [UnitTest]
    public async Task EnterAsync_NoDurableReceiptRetainsLocalUnknownAndUncommittedFailure()
    {
        using var cache = CreateCache(null);
        using (var unknown = await cache.EnterAsync("unknown", CancellationToken.None))
        {
            unknown.RetainAmbiguous();
        }
        using (var failure = await cache.EnterAsync("failure", CancellationToken.None))
        {
            failure.CompleteUncommitted(new byte[] { 1 }, 1);
        }

        using var unknownReplay = await cache.EnterAsync("unknown", CancellationToken.None);
        using var failureReplay = await cache.EnterAsync("failure", CancellationToken.None);
        unknownReplay.Recorded.State.Should().Be(FeatureEditReceiptCache.ReceiptState.Unknown);
        failureReplay.Recorded.Payload.ToArray().Should().Equal(1);
    }

    [UnitTest]
    public async Task EnterAsync_CancelledWaiterCannotSplitGateOrLeakItsReference()
    {
        using var cache = CreateCache(null);
        var holder = await cache.EnterAsync("key", CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var cancelledWaiter = cache.EnterAsync("key", cancellation.Token);
        var remainingWaiter = cache.EnterAsync("key", CancellationToken.None);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWaiter);
        cache.GateCount.Should().Be(1);
        remainingWaiter.IsCompleted.Should().BeFalse();
        holder.Complete(new byte[] { 4 }, 1);
        holder.Dispose();
        using (var replay = await remainingWaiter)
        {
            replay.Recorded.Payload.ToArray().Should().Equal(4);
        }
        cache.GateCount.Should().Be(0);
    }

    private static FeatureEditReceiptCache CreateCache(IFeatureEditIdempotencyReceipts? durable)
        => new(durable, new FeatureEditReceiptCache.Limits(TimeSpan.FromHours(24), 1024 * 1024));
}
