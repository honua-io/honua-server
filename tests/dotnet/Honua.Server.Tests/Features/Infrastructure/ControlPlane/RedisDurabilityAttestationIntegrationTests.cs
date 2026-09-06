// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Executes the Redis durability attestation and durable-job registration seams against real
/// Redis containers. The recovery case intentionally kills the container process abruptly so
/// graceful shutdown cannot stand in for AOF recovery evidence.
/// </summary>
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.TestInfrastructure)]
public sealed class RedisDurabilityAttestationIntegrationTests
{
    [IntegrationTest]
    public async Task NonPersistentRedis_IsRejectedBeforeDurableJobRegistration()
    {
        await using var redis = await StartRedisAsync(appendOnly: false, evictionPolicy: "noeviction");
        var inspection = await RedisDurabilityAttestor.InspectAsync(redis.Multiplexer);

        inspection.Accepted.Should().BeFalse();
        inspection.FailureCause.Should().Be(DurableJobSubstrateCause.RedisPersistenceDisabled, inspection.FailureDetail);

        var provider = ComposeJobServices(redis.Multiplexer, inspection.Attestation);
        provider.GetService<IExecutionJobStore>().Should().BeNull();
        provider.GetService<IJobQueue>().Should().BeNull();

        new DurableJobSubstrateOptions
        {
            RedisConfigured = true,
            RedisEntitled = true,
            RedisDurabilityFailure = inspection.FailureCause
        }.Classify(false, false).Should().Be(DurableJobSubstrateCause.RedisPersistenceDisabled);
    }

    [IntegrationTest]
    public async Task EvictingRedis_IsRejectedBeforeDurableJobRegistration()
    {
        await using var redis = await StartRedisAsync(appendOnly: true, evictionPolicy: "allkeys-lru");
        var inspection = await RedisDurabilityAttestor.InspectAsync(redis.Multiplexer);

        inspection.Accepted.Should().BeFalse();
        inspection.FailureCause.Should().Be(DurableJobSubstrateCause.RedisEvictionPolicyUnsafe, inspection.FailureDetail);

        var provider = ComposeJobServices(redis.Multiplexer, inspection.Attestation);
        provider.GetService<IExecutionJobStore>().Should().BeNull();
        provider.GetService<IJobQueue>().Should().BeNull();

        new DurableJobSubstrateOptions
        {
            RedisConfigured = true,
            RedisEntitled = true,
            RedisDurabilityFailure = inspection.FailureCause
        }.Classify(false, false).Should().Be(DurableJobSubstrateCause.RedisEvictionPolicyUnsafe);
    }

    [IntegrationTest]
    public async Task UnsafeFsyncRedis_IsRejectedBeforeDurableJobRegistration()
    {
        await using var redis = await StartRedisAsync(
            appendOnly: true,
            evictionPolicy: "noeviction",
            appendFsync: "no");
        var inspection = await RedisDurabilityAttestor.InspectAsync(redis.Multiplexer);

        inspection.Accepted.Should().BeFalse();
        inspection.FailureCause.Should().Be(DurableJobSubstrateCause.RedisWritePolicyUnsafe, inspection.FailureDetail);

        var provider = ComposeJobServices(redis.Multiplexer, inspection.Attestation);
        provider.GetService<IExecutionJobStore>().Should().BeNull();
        provider.GetService<IJobQueue>().Should().BeNull();
    }

    [IntegrationTest]
    public async Task RedisWithoutPolicyReadPermission_IsRejectedBeforeDurableJobRegistration()
    {
        await using var redis = await StartRedisAsync(
            appendOnly: true,
            evictionPolicy: "noeviction",
            denyPolicyInspection: true);
        var inspection = await RedisDurabilityAttestor.InspectAsync(redis.Multiplexer);

        inspection.Accepted.Should().BeFalse();
        inspection.FailureCause.Should().Be(DurableJobSubstrateCause.RedisAttestationUnavailable, inspection.FailureDetail);

        var provider = ComposeJobServices(redis.Multiplexer, inspection.Attestation);
        provider.GetService<IExecutionJobStore>().Should().BeNull();
        provider.GetService<IJobQueue>().Should().BeNull();
    }

    [IntegrationTest]
    public async Task AcceptedRedis_SurvivesAbruptKillAndRestart_WithAllDurableRecordsIntact()
    {
        await using var redis = await StartRedisAsync(appendOnly: true, evictionPolicy: "noeviction");
        var inspection = await RedisDurabilityAttestor.InspectAsync(redis.Multiplexer);
        inspection.Accepted.Should().BeTrue();
        inspection.Attestation!.PersistenceMode.Should().Contain("aof_enabled=1");
        inspection.Attestation.AcknowledgedWritePolicy.Should().Be("appendfsync=always");
        inspection.Attestation.EvictionPolicy.Should().Be("noeviction");
        inspection.Attestation.Endpoint.Should().NotContain("@");
        inspection.Attestation.Endpoint.ToLowerInvariant().Should().NotContain("password");
        inspection.Attestation.ObservedAt.Should().BeBefore(DateTimeOffset.UtcNow);

        var cacheServices = new ServiceCollection();
        cacheServices.AddStackExchangeRedisCache(options => options.Configuration = redis.ConnectionString);
        using var cacheProvider = cacheServices.BuildServiceProvider();
        var healthCheck = new Honua.Server.Features.HealthCheck.RedisHealthCheck(
            redis.Multiplexer,
            cacheProvider.GetRequiredService<IDistributedCache>(),
            NullLogger<Honua.Server.Features.HealthCheck.RedisHealthCheck>.Instance,
            Options.Create(new DurableJobSubstrateOptions
            {
                RedisConfigured = true,
                RedisEntitled = true,
                RedisDurabilityAttestation = inspection.Attestation
            }));
        var health = await healthCheck.CheckHealthAsync(new HealthCheckContext());
        health.Status.Should().Be(HealthStatus.Healthy);
        health.Data["durabilityEndpoint"].Should().Be(inspection.Attestation.Endpoint);
        health.Data["persistenceMode"].Should().Be(inspection.Attestation.PersistenceMode);
        health.Data["acknowledgedWritePolicy"].Should().Be(inspection.Attestation.AcknowledgedWritePolicy);
        health.Data["evictionPolicy"].Should().Be(inspection.Attestation.EvictionPolicy);
        health.Data["durabilityObservedAt"].Should().Be(inspection.Attestation.ObservedAt);

        new DurableJobSubstrateOptions
        {
            RedisConfigured = true,
            RedisEntitled = true,
            RedisDurabilityAttestation = inspection.Attestation
        }.Classify(true, true).Should().Be(DurableJobSubstrateCause.Available);

        using var provider = ComposeJobServices(redis.Multiplexer, inspection.Attestation);
        var jobStore = provider.GetRequiredService<IExecutionJobStore>();
        var queue = provider.GetRequiredService<IJobQueue>();
        var logStore = provider.GetRequiredService<IExecutionLogStore>();
        var resultStore = provider.GetRequiredService<IGeoprocessingResultPackageStore>();
        var database = redis.Multiplexer.GetDatabase();

        var queuedId = $"durability-queued-{Guid.NewGuid():N}";
        var claimedId = $"durability-claimed-{Guid.NewGuid():N}";
        var runningId = $"durability-running-{Guid.NewGuid():N}";
        var terminalId = $"durability-terminal-{Guid.NewGuid():N}";

        await jobStore.TryCreateAsync(CreateJob(claimedId));
        await queue.EnqueueAsync(claimedId);
        (await queue.TryClaimAsync("durability-worker")).Should().Be(claimedId);

        await jobStore.TryCreateAsync(CreateJob(runningId));
        await queue.EnqueueAsync(runningId);
        (await queue.TryClaimAsync("durability-worker")).Should().Be(runningId);
        var running = (await jobStore.GetAsync(runningId))!;
        await jobStore.SetAsync(running with
        {
            Status = ExecutionJobStatus.Running,
            CurrentPhase = "Running",
            LastHeartbeatAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        await jobStore.TryCreateAsync(CreateJob(queuedId));
        await queue.EnqueueAsync(queuedId);

        var terminal = CreateJob(terminalId) with
        {
            Status = ExecutionJobStatus.Succeeded,
            CurrentPhase = "Completed",
            CompletedAt = DateTimeOffset.UtcNow,
            ArtifactReferences = [$"artifact:{terminalId}"]
        };
        await jobStore.TryCreateAsync(terminal);
        await logStore.AppendAsync(terminalId, new ExecutionLogEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level = ExecutionLogLevel.Info,
            Message = "terminal record persisted",
            Phase = "Completed"
        });
        var package = new AnalysisResultPackage
        {
            ResultPackageId = $"package-{terminalId}",
            Status = GeoprocessingWorkflowStatus.Completed,
            Summary = new ResultSummary { Title = "durability recovery" },
            Artifacts =
            [
                new ArtifactRef
                {
                    ArtifactId = $"artifact:{terminalId}",
                    Kind = ArtifactKind.File,
                    Label = "durable result",
                    Uri = $"s3://results/{terminalId}.json",
                    ContentType = "application/json"
                }
            ],
            MapPackageId = $"map-package:{terminalId}",
            AppPackageId = $"app-package:{terminalId}",
            Provenance = new ProvenanceRecord
            {
                Sources = [],
                ProcessDefinitions = ["integration.durability"],
                ExecutedAt = DateTimeOffset.UtcNow
            }
        };
        await resultStore.SetAsync(terminalId, package);

        await database.PingAsync();
        await KillContainerAsync(redis.Container);
        await redis.Container.StartAsync();
        await WaitForRedisAsync(redis.Multiplexer);

        (await jobStore.GetAsync(queuedId))!.Status.Should().Be(ExecutionJobStatus.Queued);
        (await jobStore.GetAsync(claimedId))!.Status.Should().Be(ExecutionJobStatus.Provisioning);
        (await jobStore.GetAsync(runningId))!.Status.Should().Be(ExecutionJobStatus.Running);
        (await jobStore.GetAsync(terminalId))!.Status.Should().Be(ExecutionJobStatus.Succeeded);

        (await logStore.GetLogsAsync(terminalId)).Should().ContainSingle(entry =>
            entry.Message == "terminal record persisted");
        (await resultStore.GetAsync(terminalId)).Should().BeEquivalentTo(package);

        (await jobStore.GetAsync(terminalId))!.ArtifactReferences
            .Should().ContainSingle($"artifact:{terminalId}");

        var pending = await database.SortedSetRangeByRankAsync("controlplane:jobqueue:pending");
        pending.Should().Contain((RedisValue)queuedId);
        pending.Should().NotContain((RedisValue)claimedId);
        pending.Should().NotContain((RedisValue)runningId);

        var claimed = await database.SortedSetRangeByRankAsync("controlplane:jobqueue:claimed");
        claimed.Should().Contain((RedisValue)claimedId);
        claimed.Should().Contain((RedisValue)runningId);
        claimed.Should().NotContain((RedisValue)terminalId);

        var active = await database.SetMembersAsync("controlplane:job:active");
        active.Should().Contain((RedisValue)queuedId);
        active.Should().Contain((RedisValue)claimedId);
        active.Should().Contain((RedisValue)runningId);
        active.Should().NotContain((RedisValue)terminalId);

        var createdIndex = await database.SortedSetRangeByRankAsync("controlplane:job:index:created:all");
        createdIndex.Should().Contain((RedisValue)queuedId);
        createdIndex.Should().Contain((RedisValue)claimedId);
        createdIndex.Should().Contain((RedisValue)runningId);
        createdIndex.Should().Contain((RedisValue)terminalId);

        (await queue.GetQueueDepthAsync()).Should().Be(1);
    }

    private static ServiceProvider ComposeJobServices(
        IConnectionMultiplexer redis,
        RedisDurabilityAttestation? attestation)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(redis);
        if (attestation is not null)
        {
            services.AddSingleton(attestation);
        }

        services.AddGeoprocessing(new ConfigurationBuilder().Build());
        services.AddJobOrchestration();
        return services.BuildServiceProvider();
    }

    private static ExecutionJobRecord CreateJob(string operationId)
    {
        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = operationId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.LocalProcess,
                Backend = "integration",
                WorkloadName = "durability"
            }
        };
    }

    private static async Task<RedisTestContainer> StartRedisAsync(
        bool appendOnly,
        string evictionPolicy,
        string appendFsync = "always",
        bool denyPolicyInspection = false)
    {
        var hostPort = ReserveLoopbackPort();
        var command = new List<string>
        {
            "redis-server",
            "--appendonly",
            appendOnly ? "yes" : "no",
                "--appendfsync",
                appendFsync,
                "--save",
            "",
            "--maxmemory-policy",
            evictionPolicy
        };
        if (denyPolicyInspection)
        {
            command.AddRange(["--user", "default", "on", "nopass", "~*", "+@all", "-info", "-config"]);
        }

        var container = new RedisBuilder("redis:7.2-alpine")
            .WithPortBinding(hostPort, 6379)
            .WithCommand(command.ToArray())
            .Build();
        await container.StartAsync();

        var connectionString = container.GetConnectionString();
        var options = ConfigurationOptions.Parse(connectionString, ignoreUnknown: true);
        options.AllowAdmin = true;
        options.AbortOnConnectFail = false;
        options.ConnectRetry = 5;
        var multiplexer = await ConnectionMultiplexer.ConnectAsync(options);
        return new RedisTestContainer(container, connectionString, multiplexer);
    }

    private static async Task KillContainerAsync(RedisContainer container)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "docker",
            ArgumentList = { "kill", container.Id },
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Could not start docker kill.");
        await process.WaitForExitAsync();
        process.ExitCode.Should().Be(0, await process.StandardError.ReadToEndAsync());
    }

    private static async Task WaitForRedisAsync(ConnectionMultiplexer multiplexer)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (await multiplexer.GetDatabase().PingAsync() < TimeSpan.FromSeconds(5))
                {
                    return;
                }
            }
            catch (RedisConnectionException)
            {
                // The multiplexer is expected to observe the abrupt disconnect before reconnecting.
            }

            await Task.Delay(250);
        }

        throw new TimeoutException("Redis did not reconnect after the abrupt container restart.");
    }

    private static int ReserveLoopbackPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class RedisTestContainer(
        RedisContainer container,
        string connectionString,
        ConnectionMultiplexer multiplexer) : IAsyncDisposable
    {
        public RedisContainer Container { get; } = container;
        public string ConnectionString { get; } = connectionString;
        public ConnectionMultiplexer Multiplexer { get; } = multiplexer;

        public ValueTask DisposeAsync()
        {
            Multiplexer.Dispose();
            return Container.DisposeAsync();
        }
    }
}
