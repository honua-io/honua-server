// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

}
