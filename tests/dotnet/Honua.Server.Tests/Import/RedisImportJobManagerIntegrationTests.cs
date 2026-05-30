// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Import;
using Honua.Migration;
using Honua.Import.FileImport;
using Honua.Import.RasterImport;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Redis integration tests for distributed import job coordination.
/// </summary>
[Collection("Redis")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
[Operation(Operations.TestInfrastructure)]
public sealed class RedisImportJobManagerIntegrationTests
{
    private readonly RedisFixture _redis;

    public RedisImportJobManagerIntegrationTests(RedisFixture redis)
    {
        _redis = redis;
    }

    [IntegrationTest]
    public async Task JobQueue_WithRedis_RoundTrips()
    {
        var queueKey = $"test:queue:{Guid.NewGuid():N}";
        using var multiplexer = ConnectionMultiplexer.Connect(_redis.ConnectionString);
        var queue = new RedisJobQueue(multiplexer, NullLogger.Instance, queueKey);

        await queue.EnqueueAsync("job-1");
        var length = await queue.GetQueueLengthAsync();
        length.Should().Be(1);

        var job = await queue.DequeueAsync(TimeSpan.FromSeconds(2));
        job.Should().Be("job-1");

        var finalLength = await queue.GetQueueLengthAsync();
        finalLength.Should().Be(0);
    }

    [IntegrationTest]
    public async Task LeaderElection_AllowsSingleLeader()
    {
        var lockKey = $"test:leader:{Guid.NewGuid():N}";
        using var multiplexer = ConnectionMultiplexer.Connect(_redis.ConnectionString);
        var electionA = new RedisLeaderElection(multiplexer, NullLogger.Instance, lockKey, "instance-a");
        var electionB = new RedisLeaderElection(multiplexer, NullLogger.Instance, lockKey, "instance-b");

        try
        {
            var acquiredA = await electionA.TryAcquireLeadershipAsync();
            var acquiredB = await electionB.TryAcquireLeadershipAsync();

            acquiredA.Should().BeTrue();
            acquiredB.Should().BeFalse();

            await electionA.ReleaseLeadershipAsync();

            var acquiredBAfter = await electionB.TryAcquireLeadershipAsync();
            acquiredBAfter.Should().BeTrue();
        }
        finally
        {
            electionA.Dispose();
            electionB.Dispose();
        }
    }

    [IntegrationTest]
    [FlakyTest("In-memory fallback Dequeue with 200ms timeout occasionally returns null under CI load when the Enqueue→Dequeue settle exceeds the wait window. Tracked in #812 quarantine sweep.")]
    public async Task JobQueue_WithoutRedis_UsesInMemoryFallback()
    {
        var queue = new RedisJobQueue(null, NullLogger.Instance, $"test:queue:{Guid.NewGuid():N}");

        await queue.EnqueueAsync("job-1");
        var length = await queue.GetQueueLengthAsync();
        length.Should().Be(1);

        var job = await queue.DequeueAsync(TimeSpan.FromMilliseconds(200));
        job.Should().Be("job-1");
    }

    [IntegrationTest]
    public async Task LeaderElection_WithoutRedis_UsesLocalFallbackLeadership()
    {
        var election = new RedisLeaderElection(null, NullLogger.Instance, $"test:leader:{Guid.NewGuid():N}", "instance-a");
        try
        {
            var acquired = await election.TryAcquireLeadershipAsync();

            acquired.Should().BeTrue();
            election.IsLeader.Should().BeTrue();
        }
        finally
        {
            election.Dispose();
        }
    }

    [IntegrationTest]
    public async Task LeaderElection_Dispose_ReleasesLeadershipForNextInstance()
    {
        var lockKey = $"test:leader:{Guid.NewGuid():N}";
        using var multiplexer = ConnectionMultiplexer.Connect(_redis.ConnectionString);
        var electionA = new RedisLeaderElection(multiplexer, NullLogger.Instance, lockKey, "instance-a");
        var electionB = new RedisLeaderElection(multiplexer, NullLogger.Instance, lockKey, "instance-b");
        var disposedA = false;

        try
        {
            (await electionA.TryAcquireLeadershipAsync()).Should().BeTrue();
            (await electionB.TryAcquireLeadershipAsync()).Should().BeFalse();

            electionA.Dispose();
            disposedA = true;

            (await electionB.TryAcquireLeadershipAsync()).Should().BeTrue(
                "disposing the current leader should release the Redis lock promptly");
        }
        finally
        {
            if (!disposedA)
            {
                electionA.Dispose();
            }

            electionB.Dispose();
        }
    }

    [IntegrationTest]
    public async Task RequestStore_WithRedis_PersistsAndRetrieves()
    {
        using var provider = BuildRedisServices();
        var cache = provider.GetRequiredService<IDistributedCache>();
        using var multiplexer = ConnectionMultiplexer.Connect(_redis.ConnectionString);
        var keyPrefix = $"test:request:{Guid.NewGuid():N}:";
        var store = new RedisProgressStore<GeoservicesImportRequest>(
            cache,
            NullLogger.Instance,
            keyPrefix,
            GeoservicesImportJsonContext.Default.GeoservicesImportRequest,
            multiplexer);

        var request = new GeoservicesImportRequest
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Test/FeatureServer",
            LayerId = 1,
            TableName = "test_table",
            AutoPublish = false
        };

        await store.SetProgressAsync("job-1", request, TimeSpan.FromMinutes(5));

        var loaded = await store.GetProgressAsync("job-1");

        loaded.Should().NotBeNull();
        loaded.Should().BeEquivalentTo(request);

        await store.DeleteProgressAsync("job-1");
        var deleted = await store.GetProgressAsync("job-1");
        deleted.Should().BeNull();
    }

    [IntegrationTest]
    public async Task RequestStore_WithRedis_TracksActiveJobIds()
    {
        using var provider = BuildRedisServices();
        var cache = provider.GetRequiredService<IDistributedCache>();
        using var multiplexer = ConnectionMultiplexer.Connect(_redis.ConnectionString);
        var keyPrefix = $"test:active:{Guid.NewGuid():N}:";
        var store = new RedisProgressStore<GeoservicesImportRequest>(
            cache,
            NullLogger.Instance,
            keyPrefix,
            GeoservicesImportJsonContext.Default.GeoservicesImportRequest,
            multiplexer);

        await store.SetProgressAsync("job-1", new GeoservicesImportRequest
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Test/FeatureServer",
            LayerId = 1,
            TableName = "test_table",
            AutoPublish = false
        }, TimeSpan.FromMinutes(5));

        var activeIds = await store.GetActiveJobIdsAsync();

        activeIds.Should().Contain("job-1");
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
