// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Import;
using Honua.Server.Features.Infrastructure.Progress;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Honua.Server.Tests.Import;

[Collection("Redis")]
[Protocol(Protocols.Admin)]
[Operation(Operations.TestInfrastructure)]
public sealed class UniversalProgressStoreIntegrationTests
{
    private readonly RedisFixture _redis;

    public UniversalProgressStoreIntegrationTests(RedisFixture redis)
    {
        _redis = redis;
    }

    [IntegrationTest]
    public async Task GetActiveOperationIdsAsync_WithRedisIndex_ReturnsTrackedOperationIds()
    {
        using var provider = BuildRedisServices();
        using var multiplexer = ConnectionMultiplexer.Connect(_redis.ConnectionString);
        var cache = provider.GetRequiredService<IDistributedCache>();
        var store = new UniversalProgressStore(cache, NullLogger<UniversalProgressStore>.Instance, multiplexer);

        await store.SetProgressAsync(
            "export-1",
            ExportProgress.CreateInitial("export-1", "csv", "svc", 1, 10),
            TimeSpan.FromMinutes(5));

        var activeIds = await store.GetActiveOperationIdsAsync(OperationType.Export);

        activeIds.Should().Contain("export-1");
    }

    private ServiceProvider BuildRedisServices()
    {
        var services = new ServiceCollection();
        services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = _redis.ConnectionString;
            options.InstanceName = string.Empty;
        });

        return services.BuildServiceProvider();
    }
}
