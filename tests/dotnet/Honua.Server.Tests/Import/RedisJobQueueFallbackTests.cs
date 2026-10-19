// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Import;
using Honua.Migration;
using Honua.Import.FileImport;
using Honua.Import.RasterImport;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Import;

[Collection("Unit")]
public sealed class RedisJobQueueFallbackTests
{
    [UnitTest]
    public async Task EnqueueAsync_WhenRedisUnavailable_UsesInMemoryFallback()
    {
        var queueKey = $"test:queue:{Guid.NewGuid():N}";
        var queue = new RedisJobQueue(null, NullLogger.Instance, queueKey);

        await queue.EnqueueAsync("job-1");
        var length = await queue.GetQueueLengthAsync();
        length.Should().Be(1);

        var job = await queue.DequeueAsync(TimeSpan.FromMilliseconds(200));
        job.Should().Be("job-1");

        await queue.RecoverInFlightAsync();
        var recovered = await queue.DequeueAsync(TimeSpan.FromMilliseconds(200));
        recovered.Should().Be("job-1");

        await queue.CompleteAsync(recovered!);
        var lengthAfterComplete = await queue.GetQueueLengthAsync();
        lengthAfterComplete.Should().Be(0);
    }

    [UnitTest]
    public async Task EnqueueAsync_WhenFallbackDisabled_ThrowsInsteadOfAcceptingNodeLocalWork()
    {
        var queueKey = $"test:queue:{Guid.NewGuid():N}";
        var queue = new RedisJobQueue(null, NullLogger.Instance, queueKey, allowFallback: false);

        var act = () => queue.EnqueueAsync("job-1");

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*Distributed import queue is unavailable*");
    }

    [UnitTest]
    public async Task CompleteAsync_AfterRedisReturns_AcknowledgesInsteadOfLeavingJobForRedelivery()
    {
        var redis = new OutageRedis();
        var queue = new RedisJobQueue(redis.Multiplexer, NullLogger.Instance, "test:queue", allowFallback: false);
        await queue.EnqueueAsync("job-1");
        (await queue.DequeueAsync(TimeSpan.FromMilliseconds(200))).Should().Be("job-1");

        // Another queue call observes a Redis blip while the job is still running.
        redis.Down = true;
        (await queue.GetQueueLengthAsync()).Should().Be(0);
        queue.IsUsingFallback.Should().BeTrue();
        redis.Down = false;
        typeof(RedisJobQueue)
            .GetField("_lastRedisFailure", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(queue, DateTime.UtcNow);

        await queue.CompleteAsync("job-1");

        redis.ListContents("test:queue:processing").Should().BeEmpty("the finished job must be acknowledged once Redis is back");
        await queue.RecoverInFlightAsync();
        (await queue.DequeueAsync(TimeSpan.FromMilliseconds(50))).Should().BeNull("an acknowledged job is never redelivered");
    }
}
