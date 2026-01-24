// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Import;
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
    }

}
