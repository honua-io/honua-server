// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Runtime.Versioning;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.TestKit.Attributes;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit.Abstractions;

namespace Honua.Worker.Gdal.Tests;

/// <summary>
/// Release-lane certification of the durable queue handoff through the exact
/// production GDAL worker image. The image is built and loaded by
/// <c>worker-gdal-image.yml</c>; this test deliberately never substitutes the
/// worker host, executor, native command runner, Redis transport, or filesystem.
/// </summary>
public sealed class ProductionWorkerContainerHandoffTests(ITestOutputHelper output)
{
    private const string ImageEnvironmentVariable = "HONUA_WORKER_IMAGE";
    private const string RedisAlias = "worker-redis";
    private const string ContainerOutputRoot = "/var/lib/honua/gp-outputs";

    [RequiredEnvironmentFact(
        ImageEnvironmentVariable,
        skipReason: "The production worker image must be built and named by the GDAL image lane.")]
    [SupportedOSPlatform("linux")]
    public async Task ProductionImage_ExecutesStagesRejectsMalformedPayloadAndReconcilesKilledWorker()
    {
        var image = Environment.GetEnvironmentVariable(ImageEnvironmentVariable)!;
        var hostOutputRoot = Path.Join(Path.GetTempPath(), $"honua-worker-container-{Guid.NewGuid():N}");
        Directory.CreateDirectory(hostOutputRoot);
        File.SetUnixFileMode(hostOutputRoot,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);

        await using var network = new NetworkBuilder().Build();
        await network.CreateAsync();
        await using var redis = new ContainerBuilder()
            .WithImage("redis:7.2-alpine")
            .WithNetwork(network)
            .WithNetworkAliases(RedisAlias)
            .WithPortBinding(6379, true)
            .WithCommand(
                "redis-server",
                "--appendonly",
                "yes",
                "--appendfsync",
                "always",
                "--save",
                "",
                "--maxmemory-policy",
                "noeviction")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("redis-cli", "ping"))
            .Build();

        try
        {
            await redis.StartAsync();
            var redisConnection = $"127.0.0.1:{redis.GetMappedPublicPort(6379)}";
            await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redisConnection);
            var store = new RedisExecutionJobStore(
                multiplexer, NullLogger<RedisExecutionJobStore>.Instance);
            var queue = new RedisJobQueue(
                multiplexer, store, NullLogger<RedisJobQueue>.Instance);
            var logStore = new RedisExecutionLogStore(
                multiplexer, NullLogger<RedisExecutionLogStore>.Instance);

            await using var worker = BuildWorker(image, network, hostOutputRoot);
            await worker.StartAsync();
            await RecordNativeVersionsAsync(worker);

            var completed = CreateRasterConversionJob("complete");
            (await store.TryCreateAsync(completed)).Should().BeTrue();
            await queue.EnqueueAsync(completed.OperationId);

            var terminal = await WaitForJobAsync(
                store, completed.OperationId,
                job => job.Status is ExecutionJobStatus.Succeeded or ExecutionJobStatus.Failed,
                TimeSpan.FromSeconds(45));
            terminal.Status.Should().Be(ExecutionJobStatus.Succeeded, terminal.ErrorMessage);
            terminal.ArtifactReferences.Should().ContainSingle();

            var descriptor = RasterOutputJson.Deserialize(terminal.ArtifactReferences.Single());
            var staged = descriptor.Should().BeOfType<StagedObjectRasterOutputDescriptor>().Subject;
            staged.Provider.Should().Be(CloudStorageProvider.Local);
            staged.StoreReference.Should().Be("container-certification");
            var stagedPath = Path.Join(hostOutputRoot, staged.ObjectKey.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(stagedPath).Should().BeTrue("the serving host must see the worker's staged artifact");
            new FileInfo(stagedPath).Length.Should().BeGreaterThan(0);

            var unrelated = CreateRasterConversionJob("unrelated");
            (await store.TryCreateAsync(unrelated)).Should().BeTrue();
            var malformed = $"{{not-a-valid-operation-envelope:{Guid.NewGuid():N}";
            await multiplexer.GetDatabase().SortedSetAddAsync(
                "controlplane:jobqueue:pending", malformed, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await WaitForQueueMemberRemovalAsync(multiplexer.GetDatabase(), malformed, TimeSpan.FromSeconds(10));
            var untouched = await store.GetAsync(unrelated.OperationId);
            untouched.Should().BeEquivalentTo(unrelated, options => options.Excluding(job => job.Version));
            (await logStore.GetLogsAsync(malformed)).Should().BeEmpty(
                "a rejected transport member must never reach a GDAL execution context");
            (await logStore.GetLogsAsync(unrelated.OperationId)).Should().BeEmpty(
                "the malformed member must not invoke GDAL against an unrelated job");

            var crash = CreateSlowVectorJob();
            (await store.TryCreateAsync(crash)).Should().BeTrue();
            await queue.EnqueueAsync(crash.OperationId);
            var running = await WaitForJobAsync(
                store, crash.OperationId,
                job => job.Status == ExecutionJobStatus.Running,
                TimeSpan.FromSeconds(20));
            // SIGKILL PID 1 so the worker cannot run its graceful-shutdown
            // cleanup, which force-requeues an in-flight job. This preserves
            // the Running record that crash reconciliation must recover.
            await worker.ExecAsync(["/bin/kill", "-9", "1"]);

            var stale = DateTimeOffset.UtcNow.AddMinutes(-5);
            await store.SetAsync(running with
            {
                Status = ExecutionJobStatus.Running,
                UpdatedAt = stale,
                LastHeartbeatAt = stale,
                ClaimedAt = stale,
                AttemptCount = 1,
                HeartbeatPolicy = new JobHeartbeatPolicy
                {
                    Interval = TimeSpan.FromMilliseconds(50),
                    Timeout = TimeSpan.FromMilliseconds(50),
                },
                RetryPolicy = new JobRetryPolicy
                {
                    MaxAttempts = 1,
                    Strategy = BackoffStrategy.Fixed,
                    BaseDelay = TimeSpan.Zero,
                    MaxDelay = TimeSpan.Zero,
                },
            });

            var reconciler = new JobReconciliationService(
                store, queue, queue, new ExecutionJobCancellationTokens(), [], logStore,
                NullLogger<JobReconciliationService>.Instance);
            await reconciler.SweepActiveJobsAsync(CancellationToken.None);

            var failed = await store.GetAsync(crash.OperationId);
            failed.Should().NotBeNull();
            failed!.Status.Should().Be(ExecutionJobStatus.Failed);
            failed.CurrentPhase.Should().Be("Failed (heartbeat expired)");
            failed.ErrorMessage.Should().Contain("Worker heartbeat expired");
        }
        finally
        {
            try
            {
                Directory.Delete(hostOutputRoot, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Testcontainers can briefly retain a bind-mount handle during teardown.
            }
        }
    }

    private static IContainer BuildWorker(string image, INetwork network, string hostOutputRoot)
        => new ContainerBuilder()
            .WithImage(image)
            .WithNetwork(network)
            .WithEnvironment("ConnectionStrings__redis", $"{RedisAlias}:6379")
            .WithEnvironment("Geoprocessing__OutputStaging__Enabled", "true")
            .WithEnvironment("Geoprocessing__OutputStaging__Provider", "local")
            .WithEnvironment("Geoprocessing__OutputStaging__StoreReference", "container-certification")
            .WithEnvironment("Geoprocessing__OutputStaging__LocalRootPath", ContainerOutputRoot)
            .WithEnvironment("Geoprocessing__OutputStaging__MaxInlineArtifactBytes", "1024")
            .WithBindMount(hostOutputRoot, ContainerOutputRoot)
            .Build();

    private async Task RecordNativeVersionsAsync(IContainer worker)
    {
        var gdal = await worker.ExecAsync(["gdalinfo", "--version"]);
        var pdal = await worker.ExecAsync(["pdal", "--version"]);
        gdal.ExitCode.Should().Be(0);
        pdal.ExitCode.Should().Be(0);
        output.WriteLine(gdal.Stdout.Trim());
        output.WriteLine(pdal.Stdout.Trim());
    }

    private static ExecutionJobRecord CreateRasterConversionJob(string suffix)
    {
        var fixture = FindRepositoryFile(
            "tests", "dotnet", "Honua.Core.Tests", "Raster", "CogParser", "Fixtures", "none_uint8.tif");
        var parameters = BaseParameters("conversion.raster-format");
        parameters[GdalWorkerParameterKeys.StepInputPrefix + "source"] = Convert.ToBase64String(File.ReadAllBytes(fixture));
        parameters[GdalWorkerParameterKeys.StepInputPrefix + "targetFormat"] = "GTiff";
        parameters[GdalWorkerParameterKeys.OutputNamePrefix + "0"] = "converted";
        parameters[GdalWorkerParameterKeys.OutputRegistrationPrefix + "converted"] = "certification";
        return CreateQueuedJob($"prod-container-{suffix}-{Guid.NewGuid():N}", parameters);
    }

    private static ExecutionJobRecord CreateSlowVectorJob()
    {
        var features = string.Join(',', Enumerable.Range(0, 150_000).Select(index =>
            $"{{\"type\":\"Feature\",\"geometry\":{{\"type\":\"Point\",\"coordinates\":[{index % 180},{index % 90}]}},\"properties\":{{\"id\":{index}}}}}"));
        var geoJson = $"{{\"type\":\"FeatureCollection\",\"features\":[{features}]}}";
        var parameters = BaseParameters("gdal.ogr2ogr");
        parameters[GdalWorkerParameterKeys.StepInputPrefix + "source"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(geoJson));
        parameters[GdalWorkerParameterKeys.StepInputPrefix + "sourceFormat"] = "GeoJSON";
        parameters[GdalWorkerParameterKeys.StepInputPrefix + "targetFormat"] = "GPKG";
        return CreateQueuedJob($"prod-container-crash-{Guid.NewGuid():N}", parameters);
    }

    private static Dictionary<string, string> BaseParameters(string processId)
        => new(StringComparer.Ordinal)
        {
            [GdalWorkerParameterKeys.ProcessDefinitions] = processId,
        };

    private static ExecutionJobRecord CreateQueuedJob(
        string operationId,
        Dictionary<string, string> parameters)
    {
        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = operationId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            Audit = new OperationAuditInfo { RequestedBy = "container-certification" },
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.AwsBatch,
                Backend = "production-container-certification",
                WorkloadName = parameters[GdalWorkerParameterKeys.ProcessDefinitions],
                RuntimeProfile = RuntimeProfiles.Native,
                Parameters = parameters,
            },
        };
    }

    private static async Task<ExecutionJobRecord> WaitForJobAsync(
        RedisExecutionJobStore store,
        string operationId,
        Func<ExecutionJobRecord, bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        do
        {
            var job = await store.GetAsync(operationId);
            if (job is not null && predicate(job))
            {
                return job;
            }

            await Task.Delay(20);
        }
        while (DateTimeOffset.UtcNow < deadline);

        throw new TimeoutException($"Job '{operationId}' did not reach the required state within {timeout}.");
    }

    private static async Task WaitForQueueMemberRemovalAsync(IDatabase database, string member, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (await database.SortedSetScoreAsync("controlplane:jobqueue:pending", member) is not null)
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("The production worker did not reject the malformed Redis payload.");
            }

            await Task.Delay(20);
        }
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Join([directory.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Repository fixture not found: {Path.Join(segments)}");
    }
}
