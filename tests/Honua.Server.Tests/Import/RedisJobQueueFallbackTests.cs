// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Import;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Import;

[Collection("Unit")]
public sealed class RedisJobQueueFallbackTests
{
    [UnitTest]
    public async Task EnqueueAsync_WhenRedisUnavailable_UsesInMemoryFallback()
    {
        var cache = new ThrowingDistributedCache();
        var queueKey = $"test:queue:{Guid.NewGuid():N}";
        var queue = new RedisJobQueue(cache, redis: null, NullLogger.Instance, queueKey);

        await queue.EnqueueAsync("job-1");
        var length = await queue.GetQueueLengthAsync();
        length.Should().Be(1);

        var job = await queue.DequeueAsync(TimeSpan.FromMilliseconds(200));
        job.Should().Be("job-1");
    }

    private sealed class ThrowingDistributedCache : IDistributedCache
    {
        public byte[]? Get(string key) => throw new InvalidOperationException("Distributed cache should not be used.");

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
            => throw new InvalidOperationException("Distributed cache should not be used.");

        public void Refresh(string key) => throw new InvalidOperationException("Distributed cache should not be used.");

        public Task RefreshAsync(string key, CancellationToken token = default)
            => throw new InvalidOperationException("Distributed cache should not be used.");

        public void Remove(string key) => throw new InvalidOperationException("Distributed cache should not be used.");

        public Task RemoveAsync(string key, CancellationToken token = default)
            => throw new InvalidOperationException("Distributed cache should not be used.");

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
            => throw new InvalidOperationException("Distributed cache should not be used.");

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
            => throw new InvalidOperationException("Distributed cache should not be used.");
    }
}
