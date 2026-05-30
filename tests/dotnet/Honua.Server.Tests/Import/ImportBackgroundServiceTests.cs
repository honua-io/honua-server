// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using System.Collections.Concurrent;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Import;
using Honua.Migration;
using Honua.Import.FileImport;
using Honua.Import.RasterImport;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Import;

[Collection("Unit")]
public sealed class ImportBackgroundServiceTests
{
    [UnitTest]
    public async Task GeoservicesBackgroundService_PersistsResultFields_WhenImportCompletes()
    {
        using var provider = CreateGeoservicesProvider(new SuccessfulGeoservicesImportService());
        var universalProgressStore = new UniversalProgressStore(null, NullLogger<UniversalProgressStore>.Instance);
        using var jobManager = new RedisImportJobManager(
            universalProgressStore,
            null,
            NullLogger<RedisImportJobManager>.Instance,
            new TestHostEnvironment());
        var service = new GeoservicesImportBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            jobManager,
            NullLogger<GeoservicesImportBackgroundService>.Instance);

        const string jobId = "geoservices-result";
        var request = new GeoservicesImportRequest
        {
            JobId = jobId,
            ServiceUrl = "https://8.8.8.8/arcgis/rest/services/Test/FeatureServer",
            LayerId = 2,
            TableName = "geoservices_result_test",
            AutoPublish = true,
            ServiceName = "imported-service"
        };
        var progress = GeoservicesImportProgress.CreateInitial(jobId, request.ServiceUrl, request.LayerId, request.TableName) with
        {
            ServiceName = request.ServiceName
        };

        await jobManager.RequestStore.SetProgressAsync(jobId, request, TimeSpan.FromMinutes(10));
        await jobManager.ProgressStore.SetProgressAsync(jobId, progress, TimeSpan.FromMinutes(10));
        await jobManager.JobQueue.EnqueueAsync(jobId);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(
                async () => (await jobManager.ProgressStore.GetProgressAsync(jobId).ConfigureAwait(false))?.Status == GeoservicesImportStatus.Completed,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            var completed = await jobManager.ProgressStore.GetProgressAsync(jobId).ConfigureAwait(false);
            completed.Should().NotBeNull();
            completed!.CurrentPhase.Should().Be("Import completed and layer published");
            completed.ServiceName.Should().Be("imported-service");
            completed.PublishedLayerId.Should().Be(42);
            completed.LayerId.Should().Be(42);
            completed.SourceKind.Should().Be("arcgis-geoservices-rest");
            completed.SourceUrl.Should().Be(request.ServiceUrl);
            completed.SourceLayerName.Should().Be("Roads");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [UnitTest]
    public async Task GeoservicesBackgroundService_DoesNotReprocessTerminalFailedJob_WhenDuplicateDeliveryOccurs()
    {
        using var provider = CreateGeoservicesProvider(new ThrowingGeoservicesImportService(ThrowingBehavior.FailImmediately));
        var universalProgressStore = new UniversalProgressStore(null, NullLogger<UniversalProgressStore>.Instance);
        using var jobManager = new RedisImportJobManager(
            universalProgressStore,
            null,
            NullLogger<RedisImportJobManager>.Instance,
            new TestHostEnvironment());
        var service = new GeoservicesImportBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            jobManager,
            NullLogger<GeoservicesImportBackgroundService>.Instance);

        const string jobId = "geoservices-fail";
        var request = new GeoservicesImportRequest
        {
            JobId = jobId,
            ServiceUrl = "https://8.8.8.8/arcgis/rest/services/Test/FeatureServer",
            LayerId = 0,
            TableName = "geoservices_fail_test",
            AutoPublish = false
        };
        var progress = GeoservicesImportProgress.CreateInitial(jobId, request.ServiceUrl, request.LayerId, request.TableName);

        await jobManager.RequestStore.SetProgressAsync(jobId, request, TimeSpan.FromMinutes(10));
        (await jobManager.RequestStore.GetProgressAsync(jobId).ConfigureAwait(false)).Should().NotBeNull();
        await jobManager.ProgressStore.SetProgressAsync(jobId, progress, TimeSpan.FromMinutes(10));
        await jobManager.JobQueue.EnqueueAsync(jobId);
        await jobManager.JobQueue.EnqueueAsync(jobId);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(
                async () => (await jobManager.ProgressStore.GetProgressAsync(jobId).ConfigureAwait(false))?.Status == GeoservicesImportStatus.Failed,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

            provider.GetRequiredService<ThrowingGeoservicesImportService>().CallCount.Should().Be(1);
            (await jobManager.ProgressStore.GetProgressAsync(jobId).ConfigureAwait(false))!.Status.Should().Be(GeoservicesImportStatus.Failed);
            (await jobManager.RequestStore.GetProgressAsync(jobId).ConfigureAwait(false)).Should().BeNull();
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [UnitTest]
    public async Task GeoservicesBackgroundService_PreservesCancelledState_WhenCancellationSurfacesAsGenericFailure()
    {
        using var provider = CreateGeoservicesProvider(new ThrowingGeoservicesImportService(ThrowingBehavior.ThrowAfterCancellation));
        var universalProgressStore = new UniversalProgressStore(null, NullLogger<UniversalProgressStore>.Instance);
        using var jobManager = new RedisImportJobManager(
            universalProgressStore,
            null,
            NullLogger<RedisImportJobManager>.Instance,
            new TestHostEnvironment());
        var service = new GeoservicesImportBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            jobManager,
            NullLogger<GeoservicesImportBackgroundService>.Instance);

        const string jobId = "geoservices-cancel";
        var request = new GeoservicesImportRequest
        {
            JobId = jobId,
            ServiceUrl = "https://8.8.8.8/arcgis/rest/services/Test/FeatureServer",
            LayerId = 0,
            TableName = "geoservices_cancel_test",
            AutoPublish = false
        };
        var progress = GeoservicesImportProgress.CreateInitial(jobId, request.ServiceUrl, request.LayerId, request.TableName);

        await jobManager.RequestStore.SetProgressAsync(jobId, request, TimeSpan.FromMinutes(10));
        await jobManager.ProgressStore.SetProgressAsync(jobId, progress, TimeSpan.FromMinutes(10));
        await jobManager.JobQueue.EnqueueAsync(jobId);

        await service.StartAsync(CancellationToken.None);
        try
        {
            var importService = provider.GetRequiredService<ThrowingGeoservicesImportService>();
            await importService.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            await jobManager.ProgressStore.SetProgressAsync(
                jobId,
                progress with
                {
                    Status = GeoservicesImportStatus.Cancelled,
                    CompletedAt = DateTimeOffset.UtcNow,
                    CurrentPhase = "Cancellation requested"
                },
                TimeSpan.FromMinutes(10)).ConfigureAwait(false);
            await jobManager.RequestStore.DeleteProgressAsync(jobId).ConfigureAwait(false);

            await WaitForAsync(
                async () => (await jobManager.ProgressStore.GetProgressAsync(jobId).ConfigureAwait(false))?.Status == GeoservicesImportStatus.Cancelled,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            (await jobManager.ProgressStore.GetProgressAsync(jobId).ConfigureAwait(false))!.Status.Should().Be(GeoservicesImportStatus.Cancelled);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [UnitTest]
    public async Task GeoservicesBackgroundService_WhenLeadershipIsLost_RequeuesWorkInsteadOfCancellingAndAcknowledging()
    {
        using var provider = CreateGeoservicesProvider(new ThrowingGeoservicesImportService(ThrowingBehavior.ThrowAfterCancellation));
        var universalProgressStore = new UniversalProgressStore(null, NullLogger<UniversalProgressStore>.Instance);
        var (redis, database) = CreateLeaseLossRedis("geoservices:import:queue", "geoservices:import:leader", "geoservices-lease-loss");
        using var jobManager = new RedisImportJobManager(
            universalProgressStore,
            null,
            NullLogger<RedisImportJobManager>.Instance,
            new TestHostEnvironment(),
            redis);
        var service = new GeoservicesImportBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            jobManager,
            NullLogger<GeoservicesImportBackgroundService>.Instance);

        const string jobId = "geoservices-lease-loss";
        var request = new GeoservicesImportRequest
        {
            JobId = jobId,
            ServiceUrl = "https://8.8.8.8/arcgis/rest/services/Test/FeatureServer",
            LayerId = 0,
            TableName = "geoservices_lease_loss_test",
            AutoPublish = false
        };
        var progress = GeoservicesImportProgress.CreateInitial(jobId, request.ServiceUrl, request.LayerId, request.TableName);

        await jobManager.RequestStore.SetProgressAsync(jobId, request, TimeSpan.FromMinutes(10));
        await jobManager.ProgressStore.SetProgressAsync(jobId, progress, TimeSpan.FromMinutes(10));
        await jobManager.JobQueue.EnqueueAsync(jobId);

        await service.StartAsync(CancellationToken.None);
        try
        {
            var importService = provider.GetRequiredService<ThrowingGeoservicesImportService>();
            await importService.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            GeoservicesImportProgress? lastObservedProgress = null;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                lastObservedProgress = await jobManager.ProgressStore.GetProgressAsync(jobId).ConfigureAwait(false);
                if (lastObservedProgress?.Status == GeoservicesImportStatus.Queued &&
                    string.Equals(lastObservedProgress.CurrentPhase, "Queued for recovery after leadership loss", StringComparison.Ordinal))
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
            }

            lastObservedProgress.Should().NotBeNull();
            lastObservedProgress!.Status.Should().Be(
                GeoservicesImportStatus.Queued,
                $"expected the strict-mode import to requeue after leadership loss, but observed phase '{lastObservedProgress.CurrentPhase}'");
            lastObservedProgress.CurrentPhase.Should().Be("Queued for recovery after leadership loss");

            (await jobManager.RequestStore.GetProgressAsync(jobId).ConfigureAwait(false)).Should().NotBeNull();
            _ = database.DidNotReceive().ListRemoveAsync(
                "geoservices:import:queue:processing",
                (RedisValue)jobId,
                1,
                Arg.Any<CommandFlags>());
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [UnitTest]
    public async Task GeoservicesBackgroundService_InStrictMode_WhenRedisFailsDuringHeartbeat_RequeuesWork()
    {
        using var provider = CreateGeoservicesProvider(new ThrowingGeoservicesImportService(ThrowingBehavior.ThrowAfterCancellation));
        var distributedCache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var universalProgressStore = new UniversalProgressStore(
            distributedCache,
            NullLogger<UniversalProgressStore>.Instance,
            redis: null);
        var (redis, database) = CreateHeartbeatFailureRedis("geoservices:import:queue", "geoservices:import:leader", "geoservices-strict-heartbeat");
        using var jobManager = new RedisImportJobManager(
            universalProgressStore,
            distributedCache,
            NullLogger<RedisImportJobManager>.Instance,
            new ProductionHostEnvironment(),
            redis);
        var service = new GeoservicesImportBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            jobManager,
            NullLogger<GeoservicesImportBackgroundService>.Instance);

        const string jobId = "geoservices-strict-heartbeat";
        var request = new GeoservicesImportRequest
        {
            JobId = jobId,
            ServiceUrl = "https://8.8.8.8/arcgis/rest/services/Test/FeatureServer",
            LayerId = 0,
            TableName = "geoservices_strict_heartbeat_test",
            AutoPublish = false
        };
        var progress = GeoservicesImportProgress.CreateInitial(jobId, request.ServiceUrl, request.LayerId, request.TableName);

        await jobManager.RequestStore.SetProgressAsync(jobId, request, TimeSpan.FromMinutes(10));
        await jobManager.ProgressStore.SetProgressAsync(jobId, progress, TimeSpan.FromMinutes(10));
        await jobManager.JobQueue.EnqueueAsync(jobId);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(
                async () =>
                {
                    var current = await jobManager.ProgressStore.GetProgressAsync(jobId).ConfigureAwait(false);
                    return current?.Status == GeoservicesImportStatus.Queued &&
                           string.Equals(current.CurrentPhase, "Queued for recovery after leadership loss", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            (await jobManager.RequestStore.GetProgressAsync(jobId).ConfigureAwait(false)).Should().NotBeNull();
            _ = database.DidNotReceive().ListRemoveAsync(
                "geoservices:import:queue:processing",
                (RedisValue)jobId,
                1,
                Arg.Any<CommandFlags>());
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [UnitTest]
    public async Task GeoServerBackgroundService_DoesNotReprocessTerminalFailedJob_WhenDuplicateDeliveryOccurs()
    {
        using var provider = CreateGeoServerProvider(new ThrowingGeoServerImportService(ThrowingBehavior.FailImmediately));
        var universalProgressStore = new UniversalProgressStore(null, NullLogger<UniversalProgressStore>.Instance);
        using var jobManager = new GeoServerImportJobManager(
            universalProgressStore,
            null,
            NullLogger<GeoServerImportJobManager>.Instance,
            new TestHostEnvironment());
        var service = new GeoServerImportBackgroundService(
            new ConfigurationBuilder().Build(),
            new TestHostEnvironment(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            jobManager,
            NullLogger<GeoServerImportBackgroundService>.Instance);

        const string jobId = "geoserver-fail";
        var request = new GeoServerImportRequest
        {
            JobId = jobId,
            GeoServerRestUrl = "https://8.8.8.8/geoserver/rest",
            TargetHonuaUrl = "https://honua.example.com",
            DryRun = true
        };
        var progress = GeoServerImportProgress.CreateInitial(jobId, request.GeoServerRestUrl, request.TargetHonuaUrl);

        await jobManager.RequestStore.SetProgressAsync(jobId, request, TimeSpan.FromMinutes(10));
        await jobManager.ProgressStore.SetProgressAsync(jobId, progress, TimeSpan.FromMinutes(10));
        await jobManager.JobQueue.EnqueueAsync(jobId);
        await jobManager.JobQueue.EnqueueAsync(jobId);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(
                async () => (await jobManager.ProgressStore.GetProgressAsync(jobId).ConfigureAwait(false))?.Status == GeoServerImportStatus.Failed,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

            provider.GetRequiredService<ThrowingGeoServerImportService>().CallCount.Should().Be(1);
            (await jobManager.ProgressStore.GetProgressAsync(jobId).ConfigureAwait(false))!.Status.Should().Be(GeoServerImportStatus.Failed);
            (await jobManager.RequestStore.GetProgressAsync(jobId).ConfigureAwait(false)).Should().BeNull();
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [UnitTest]
    public async Task GeoServerBackgroundService_PreservesCancelledState_WhenCancellationSurfacesAsGenericFailure()
    {
        using var provider = CreateGeoServerProvider(new ThrowingGeoServerImportService(ThrowingBehavior.ThrowAfterCancellation));
        var universalProgressStore = new UniversalProgressStore(null, NullLogger<UniversalProgressStore>.Instance);
        using var jobManager = new GeoServerImportJobManager(
            universalProgressStore,
            null,
            NullLogger<GeoServerImportJobManager>.Instance,
            new TestHostEnvironment());
        var service = new GeoServerImportBackgroundService(
            new ConfigurationBuilder().Build(),
            new TestHostEnvironment(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            jobManager,
            NullLogger<GeoServerImportBackgroundService>.Instance);

        const string jobId = "geoserver-cancel";
        var request = new GeoServerImportRequest
        {
            JobId = jobId,
            GeoServerRestUrl = "https://8.8.8.8/geoserver/rest",
            TargetHonuaUrl = "https://honua.example.com",
            DryRun = true
        };
        var progress = GeoServerImportProgress.CreateInitial(jobId, request.GeoServerRestUrl, request.TargetHonuaUrl);

        await jobManager.RequestStore.SetProgressAsync(jobId, request, TimeSpan.FromMinutes(10));
        await jobManager.ProgressStore.SetProgressAsync(jobId, progress, TimeSpan.FromMinutes(10));
        await jobManager.JobQueue.EnqueueAsync(jobId);

        await service.StartAsync(CancellationToken.None);
        try
        {
            var importService = provider.GetRequiredService<ThrowingGeoServerImportService>();
            await importService.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            await jobManager.ProgressStore.SetProgressAsync(
                jobId,
                progress with
                {
                    Status = GeoServerImportStatus.Cancelled,
                    CompletedAt = DateTimeOffset.UtcNow,
                    CurrentPhase = "Cancellation requested"
                },
                TimeSpan.FromMinutes(10)).ConfigureAwait(false);
            await jobManager.RequestStore.DeleteProgressAsync(jobId).ConfigureAwait(false);

            await WaitForAsync(
                async () => (await jobManager.ProgressStore.GetProgressAsync(jobId).ConfigureAwait(false))?.Status == GeoServerImportStatus.Cancelled,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            (await jobManager.ProgressStore.GetProgressAsync(jobId).ConfigureAwait(false))!.Status.Should().Be(GeoServerImportStatus.Cancelled);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [UnitTest]
    public async Task GeoServerBackgroundService_WhenLeadershipIsLost_RequeuesWorkInsteadOfCancellingAndAcknowledging()
    {
        using var provider = CreateGeoServerProvider(new ThrowingGeoServerImportService(ThrowingBehavior.ThrowAfterCancellation));
        var universalProgressStore = new UniversalProgressStore(null, NullLogger<UniversalProgressStore>.Instance);
        var (redis, database) = CreateLeaseLossRedis("geoserver:import:queue", "geoserver:import:leader", "geoserver-lease-loss");
        using var jobManager = new GeoServerImportJobManager(
            universalProgressStore,
            null,
            NullLogger<GeoServerImportJobManager>.Instance,
            new TestHostEnvironment(),
            redis);
        var service = new GeoServerImportBackgroundService(
            new ConfigurationBuilder().Build(),
            new TestHostEnvironment(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            jobManager,
            NullLogger<GeoServerImportBackgroundService>.Instance);

        const string jobId = "geoserver-lease-loss";
        var request = new GeoServerImportRequest
        {
            JobId = jobId,
            GeoServerRestUrl = "https://8.8.8.8/geoserver/rest",
            TargetHonuaUrl = "https://honua.example.com",
            DryRun = true
        };
        var progress = GeoServerImportProgress.CreateInitial(jobId, request.GeoServerRestUrl, request.TargetHonuaUrl);

        await jobManager.RequestStore.SetProgressAsync(jobId, request, TimeSpan.FromMinutes(10));
        await jobManager.ProgressStore.SetProgressAsync(jobId, progress, TimeSpan.FromMinutes(10));
        await jobManager.JobQueue.EnqueueAsync(jobId);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await provider.GetRequiredService<ThrowingGeoServerImportService>().Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await WaitForAsync(
                async () =>
                {
                    var current = await jobManager.ProgressStore.GetProgressAsync(jobId).ConfigureAwait(false);
                    return current?.Status == GeoServerImportStatus.Queued &&
                           string.Equals(current.CurrentPhase, "Queued for recovery after leadership loss", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            (await jobManager.RequestStore.GetProgressAsync(jobId).ConfigureAwait(false)).Should().NotBeNull();
            _ = database.DidNotReceive().ListRemoveAsync(
                "geoserver:import:queue:processing",
                (RedisValue)jobId,
                1,
                Arg.Any<CommandFlags>());
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static ServiceProvider CreateGeoservicesProvider(IGeoservicesImportService service)
    {
        var services = new ServiceCollection();
        services.AddSingleton(service);
        if (service is ThrowingGeoservicesImportService throwingService)
        {
            services.AddSingleton(throwingService);
        }

        services.AddSingleton<IGeoservicesImportService>(sp => service);
        return services.BuildServiceProvider();
    }

    private static ServiceProvider CreateGeoServerProvider(ThrowingGeoServerImportService service)
    {
        var services = new ServiceCollection();
        services.AddSingleton(service);
        services.AddSingleton<IGeoServerImportService>(sp => sp.GetRequiredService<ThrowingGeoServerImportService>());
        return services.BuildServiceProvider();
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition().ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
        }

        throw new TimeoutException($"Condition was not met within {timeout.TotalSeconds} seconds.");
    }

    private static (IConnectionMultiplexer Redis, IDatabase Database) CreateLeaseLossRedis(
        string queueKey,
        string leaderKey,
        string jobId)
    {
        var database = Substitute.For<IDatabase>();
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);

        database.ListLeftPushAsync(queueKey, Arg.Any<RedisValue>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(1L));
        database.ListRangeAsync($"{queueKey}:processing", 0, 99, Arg.Any<CommandFlags>())
            .Returns(
                Task.FromResult(Array.Empty<RedisValue>()),
                Task.FromResult(Array.Empty<RedisValue>()));
        database.ListRightPopLeftPushAsync($"{queueKey}:processing", queueKey, Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(RedisValue.Null));
        database.ListRightPopLeftPushAsync(queueKey, $"{queueKey}:processing", Arg.Any<CommandFlags>())
            .Returns(
                Task.FromResult((RedisValue)jobId),
                Task.FromResult(RedisValue.Null));
        database.LockTakeAsync(leaderKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(
                Task.FromResult(true),
                Task.FromResult(false));
        database.LockExtendAsync(leaderKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(
                Task.FromResult(true),
                Task.FromResult(false));
        database.ListRemoveAsync($"{queueKey}:processing", Arg.Any<RedisValue>(), 1, Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(1L));

        return (redis, database);
    }

    private static (IConnectionMultiplexer Redis, IDatabase Database) CreateHeartbeatFailureRedis(
        string queueKey,
        string leaderKey,
        string jobId)
    {
        var redisStrings = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var durableRequestStrings = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var redisSets = new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>(StringComparer.Ordinal);
        var database = Substitute.For<IDatabase>();
        var transaction = Substitute.For<ITransaction>();
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);

        database.ListLeftPushAsync(queueKey, Arg.Any<RedisValue>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(1L));
        database.ListRangeAsync($"{queueKey}:processing", 0, 99, Arg.Any<CommandFlags>())
            .Returns(
                Task.FromResult(Array.Empty<RedisValue>()),
                Task.FromResult(Array.Empty<RedisValue>()));
        database.ListRightPopLeftPushAsync($"{queueKey}:processing", queueKey, Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(RedisValue.Null));
        database.ListRightPopLeftPushAsync(queueKey, $"{queueKey}:processing", Arg.Any<CommandFlags>())
            .Returns(
                Task.FromResult((RedisValue)jobId),
                Task.FromResult(RedisValue.Null));
        database.LockTakeAsync(leaderKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(true));
        database.LockExtendAsync(leaderKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(
                Task.FromResult(true),
                Task.FromResult(true),
                Task.FromException<bool>(new RedisConnectionException(ConnectionFailureType.SocketFailure, "simulated Redis outage")));
        database.ListRemoveAsync($"{queueKey}:processing", Arg.Any<RedisValue>(), 1, Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(1L));
        database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(call =>
            {
                var key = call.ArgAt<RedisKey>(0).ToString();
                if (redisStrings.TryGetValue(key, out var value))
                {
                    return Task.FromResult((RedisValue)value);
                }

                return Task.FromResult(durableRequestStrings.TryGetValue(key, out var durableValue) ? (RedisValue)durableValue : RedisValue.Null);
            });
        database.SetMembersAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(call =>
            {
                var key = call.ArgAt<RedisKey>(0).ToString();
                if (!redisSets.TryGetValue(key, out var members))
                {
                    return Task.FromResult(Array.Empty<RedisValue>());
                }

                return Task.FromResult(members.Keys.Select(member => (RedisValue)member).ToArray());
            });
        database.CreateTransaction().Returns(transaction);

        transaction.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
            .Returns(call =>
            {
                var key = call.ArgAt<RedisKey>(0).ToString();
                var value = call.ArgAt<RedisValue>(1).ToString();
                redisStrings[key] = value;
                if (key.StartsWith("geoservices:import:request:", StringComparison.Ordinal))
                {
                    durableRequestStrings[key] = value;
                }

                return Task.FromResult(true);
            });
        transaction.SetAddAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns(call =>
            {
                var key = call.ArgAt<RedisKey>(0).ToString();
                var set = redisSets.GetOrAdd(key, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
                set[call.ArgAt<RedisValue>(1).ToString()] = 1;
                return Task.FromResult(true);
            });
        transaction.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(call =>
            {
                var key = call.ArgAt<RedisKey>(0).ToString();
                redisStrings.TryRemove(key, out _);
                durableRequestStrings.TryRemove(key, out _);
                return Task.FromResult(true);
            });
        transaction.SetRemoveAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns(call =>
            {
                var key = call.ArgAt<RedisKey>(0).ToString();
                if (redisSets.TryGetValue(key, out var set))
                {
                    set.TryRemove(call.ArgAt<RedisValue>(1).ToString(), out _);
                }

                return Task.FromResult(true);
            });
        transaction.ExecuteAsync(Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(true));

        return (redis, database);
    }

    private enum ThrowingBehavior
    {
        FailImmediately,
        ThrowAfterCancellation
    }

    private sealed class ThrowingGeoservicesImportService(ThrowingBehavior behavior) : IGeoservicesImportService
    {
        public int CallCount => _callCount;

        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _callCount;

        public Task<GeoservicesServiceInfo> DiscoverServiceAsync(
            GeoservicesDiscoveryRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<MigrationSourceInventoryArtifact> ScanSourceAsync(
            GeoservicesDiscoveryRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GeoservicesImportResult> ImportLayerAsync(
            GeoservicesImportRequest request,
            CancellationToken cancellationToken = default)
            => ImportLayerAsync(request, progress: null, cancellationToken);

        public async Task<GeoservicesImportResult> ImportLayerAsync(
            GeoservicesImportRequest request,
            IProgress<GeoservicesImportProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            Started.TrySetResult(true);

            if (behavior == ThrowingBehavior.FailImmediately)
            {
                throw new InvalidOperationException("boom");
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException("late failure after cancellation");
            }

            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class SuccessfulGeoservicesImportService : IGeoservicesImportService
    {
        public Task<GeoservicesServiceInfo> DiscoverServiceAsync(
            GeoservicesDiscoveryRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<MigrationSourceInventoryArtifact> ScanSourceAsync(
            GeoservicesDiscoveryRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GeoservicesImportResult> ImportLayerAsync(
            GeoservicesImportRequest request,
            CancellationToken cancellationToken = default)
            => ImportLayerAsync(request, progress: null, cancellationToken);

        public Task<GeoservicesImportResult> ImportLayerAsync(
            GeoservicesImportRequest request,
            IProgress<GeoservicesImportProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            progress?.Report(new GeoservicesImportProgress
            {
                JobId = request.JobId!,
                Status = GeoservicesImportStatus.Publishing,
                FeaturesProcessed = 10,
                EstimatedTotalFeatures = 10,
                SourceServiceUrl = request.ServiceUrl,
                SourceLayerId = request.LayerId,
                SourceLayerName = "Roads",
                TableName = request.TableName,
                ServiceName = request.ServiceName,
                CurrentPhase = "Publishing imported layer",
                StartedAt = DateTimeOffset.UtcNow
            });

            return Task.FromResult(GeoservicesImportResult.CreateSuccess(
                request.TableName,
                request.ServiceUrl,
                request.LayerId,
                featureCount: 10,
                publishedLayerId: 42,
                serviceName: request.ServiceName,
                sourceLayerName: "Roads"));
        }
    }

    private sealed class ThrowingGeoServerImportService(ThrowingBehavior behavior) : IGeoServerImportService
    {
        public int CallCount => _callCount;

        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _callCount;

        public Task<GeoServerServiceInfo> DiscoverServiceAsync(
            GeoServerDiscoveryRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<MigrationSourceInventoryArtifact> ScanSourceAsync(
            GeoServerDiscoveryRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GeoServerImportResult> ImportConfigurationAsync(
            GeoServerImportRequest request,
            CancellationToken cancellationToken = default)
            => ImportConfigurationAsync(request, progress: null, cancellationToken);

        public async Task<GeoServerImportResult> ImportConfigurationAsync(
            GeoServerImportRequest request,
            IProgress<GeoServerImportProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            Started.TrySetResult(true);

            if (behavior == ThrowingBehavior.FailImmediately)
            {
                throw new InvalidOperationException("boom");
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException("late failure after cancellation");
            }

            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";

        public string ApplicationName { get; set; } = nameof(ImportBackgroundServiceTests);

        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class ProductionHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";

        public string ApplicationName { get; set; } = nameof(ImportBackgroundServiceTests);

        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
