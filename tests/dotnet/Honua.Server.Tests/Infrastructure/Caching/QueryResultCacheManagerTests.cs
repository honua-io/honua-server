// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Caching;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Infrastructure.Caching;

public sealed class QueryResultCacheManagerTests
{
    [Fact]
    public async Task GetOrExecuteAsync_ByDefault_DoesNotCacheResults()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        using var manager = CreateManager(memoryCache, new QueryResultCacheOptions());
        var executions = 0;

        var first = await manager.GetOrExecuteAsync("query:spatial", () => Task.FromResult(++executions));
        var second = await manager.GetOrExecuteAsync("query:spatial", () => Task.FromResult(++executions));

        first.Should().Be(1);
        second.Should().Be(2);
        executions.Should().Be(2);
    }

    [Fact]
    public async Task GetOrExecuteAsync_WhenEnabled_CachesResults()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        using var manager = CreateManager(
            memoryCache,
            new QueryResultCacheOptions
            {
                Enabled = true
            });
        var executions = 0;

        var first = await manager.GetOrExecuteAsync("query:nonspatial", () => Task.FromResult(++executions));
        var second = await manager.GetOrExecuteAsync("query:nonspatial", () => Task.FromResult(++executions));

        first.Should().Be(1);
        second.Should().Be(1);
        executions.Should().Be(1);
    }

    private static QueryResultCacheManager CreateManager(IMemoryCache memoryCache, QueryResultCacheOptions options)
        => new(
            memoryCache,
            NullLogger<QueryResultCacheManager>.Instance,
            Options.Create(options));
}
