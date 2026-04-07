// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Server.Features.Import;
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

    private static ServiceProvider CreateGeoservicesProvider(ThrowingGeoservicesImportService service)
    {
        var services = new ServiceCollection();
        services.AddSingleton(service);
        services.AddSingleton<IGeoservicesImportService>(sp => sp.GetRequiredService<ThrowingGeoservicesImportService>());
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
        var database = Substitute.For<IDatabase>();
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);

        database.ListLeftPushAsync(queueKey, Arg.Any<RedisValue>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(1L));
        database.ListRangeAsync($"{queueKey}:processing", 0, 99, Arg.Any<CommandFlags>())
            .Returns(
                Task.FromResult(Array.Empty<RedisValue>()),
                Task.FromResult(Array.Empty<RedisValue>()));
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

    private sealed class ThrowingGeoServerImportService(ThrowingBehavior behavior) : IGeoServerImportService
    {
        public int CallCount => _callCount;

        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _callCount;

        public Task<GeoServerServiceInfo> DiscoverServiceAsync(
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
