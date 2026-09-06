// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Infrastructure.Caching;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Caching;

[Trait("Tier", "Fast")]
public sealed class MonitoredResponseCacheConcurrencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fill_ConcurrentInvalidationThroughDecorator_PreservesGeneration(bool useFactory)
    {
        var shared = new CacheServiceResponseCacheReplicaTests.SharedCache();
        var writer = new CacheServiceResponseCache(shared);
        var monitor = Substitute.For<IPerformanceMonitor>();
        var scope = Substitute.For<IOperationScope>();
        scope.WithTag(Arg.Any<string>(), Arg.Any<string>()).Returns(scope);
        monitor.StartOperation(Arg.Any<string>()).Returns(scope);
        var reader = new MonitoredResponseCacheDecorator(
            new CacheServiceResponseCache(shared), monitor, NullLogger<MonitoredResponseCacheDecorator>.Instance);
        const string key = "query:odata:layer:42:hash";

        async Task<string> QueryAsync()
        {
            await writer.RemoveByPatternAsync("query:odata:layer:42:*");
            Assert.Null(await reader.GetAsync<string>(key));
            return "before edit";
        }

        if (useFactory)
        {
            Assert.Equal("before edit", await reader.GetOrCreateAsync(key, QueryAsync, TimeSpan.FromMinutes(5)));
        }
        else
        {
            var fillKey = await reader.BindKeyAsync(key);
            Assert.Null(await reader.GetAsync<string>(fillKey));
            await reader.SetAsync(fillKey, await QueryAsync(), TimeSpan.FromMinutes(5));
        }

        Assert.Null(await writer.GetAsync<string>(key));
        Assert.Null(await reader.GetAsync<string>(key));
        Assert.Equal("after edit", await reader.GetOrCreateAsync(key, () => Task.FromResult("after edit"), TimeSpan.FromMinutes(5)));
        Assert.Equal("after edit", await writer.GetAsync<string>(key));
        monitor.Received().RecordCacheMetrics(Arg.Any<string>(), "miss");
    }
}
