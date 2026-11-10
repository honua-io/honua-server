// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Configurations;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Honua.Worker.Gdal.Tests;

// Release-lane certification of the durable queue handoff through the exact
// production GDAL worker image. The image is built and loaded by
// worker-gdal-image.yml; this test deliberately never substitutes the
// worker host, executor, native command runner, job queue, or filesystem.
[Collection(WebAppFixtureJobSubstrateExtensions.JobSubstrateCollection)]
public sealed class ProductionWorkerContainerHandoffTests(ITestOutputHelper output)
{
    private const string ImageEnvironmentVariable = "HONUA_WORKER_IMAGE";
    private const string DockerHostAlias = "host.docker.internal";
    private const string AssignedJobBackend = "production-container-certification";
    private const string ContainerOutputRoot = "/var/lib/honua/gp-outputs";

    [RequiredEnvironmentFact(
        ImageEnvironmentVariable,
        skipReason: "The production worker image must be built and named by the GDAL image lane.")]
    [Trait("Tier", "Slow")]
    [SupportedOSPlatform("linux")]
    public async Task ProductionImage_ExecutesStagesAndReconcilesKilledWorker()
    {
        var image = Environment.GetEnvironmentVariable(ImageEnvironmentVariable)!;
        var hostOutputRoot = Path.Join(Path.GetTempPath(), $"honua-worker-container-{Guid.NewGuid():N}");
        Directory.CreateDirectory(hostOutputRoot);
        GeoprocessingOutputStoreTestHelper.Attest(new GeoprocessingOutputStagingOptions
        {
            Enabled = true,
            LocalRootPath = hostOutputRoot,
            StoreReference = "container-certification",
            MaxInlineArtifactBytes = 1024,
        });
        File.SetUnixFileMode(hostOutputRoot,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);

        var postgres = new PostgresFixture();
        await postgres.InitializeAsync();
        try
        {
            var (store, queue, logStore) = await JobSubstrate.CreateEmptyAsync(postgres);

            await using var worker = BuildWorker(image, postgres.ConnectionString, hostOutputRoot);
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

            using var reconciler = new JobReconciliationService(
                store, queue, queue, new ExecutionJobCancellationTokens(), [], logStore,
                NullLogger<JobReconciliationService>.Instance, store, store);
            await reconciler.SweepActiveJobsAsync(CancellationToken.None);

            var failed = await store.GetAsync(crash.OperationId);
            failed.Should().NotBeNull();
            failed!.Status.Should().Be(ExecutionJobStatus.Failed);
            failed.CurrentPhase.Should().Be("Failed (heartbeat expired)");
            failed.ErrorMessage.Should().Contain("Worker heartbeat expired");
        }
        finally
        {
            await postgres.DisposeAsync();
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

    [RequiredEnvironmentFact(
        ImageEnvironmentVariable,
        skipReason: "The production worker image must be built and named by the GDAL image lane.")]
    [Trait("Tier", "Integration")]
    public async Task ProductionImage_AssignedNativeOperation_ConvertsAndExitsWithoutDrainingQueue()
    {
        var image = Environment.GetEnvironmentVariable(ImageEnvironmentVariable)!;
        var postgres = new PostgresFixture();
        await postgres.InitializeAsync();
        try
        {
            var (store, queue, _) = await JobSubstrate.CreateEmptyAsync(postgres);
            var parameters = BaseParameters("gdal.ogr2ogr");
            const string csv = "WKT,name,value\n\"POINT Z (12.25 -4.5 7.75)\",alpha,17\n";
            parameters[GdalWorkerParameterKeys.StepInputPrefix + "source"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(csv));
            parameters[GdalWorkerParameterKeys.StepInputPrefix + "sourceFormat"] = "CSV";
            parameters[GdalWorkerParameterKeys.StepInputPrefix + "targetFormat"] = "GeoJSON";
            var assigned = CreateQueuedJob($"native-assigned-{Guid.NewGuid():N}", parameters, AssignedJobBackend) with
            {
                AttemptCount = 1,
                ProviderOperationId = "container-certification-attempt-1",
            };
            (await store.TryCreateAsync(assigned)).Should().BeTrue();
            var unrelated = CreateQueuedJob($"native-unrelated-{Guid.NewGuid():N}", parameters);
            (await store.TryCreateAsync(unrelated)).Should().BeTrue();
            await queue.EnqueueAsync(unrelated.OperationId);
            var queuedUnrelated = (await store.GetAsync(unrelated.OperationId))!;
            (await queue.GetQueueDepthAsync()).Should().Be(1);

            // Reuse exactly the launch variables stamped by Kubernetes/Batch backends.
            var launch = ExecutionWorkerLaunchEnvironment.Build(assigned, 1)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            await using var worker = new ContainerBuilder()
                .WithImage(image)
                .WithExtraHost(DockerHostAlias, "host-gateway")
                .WithEnvironment(
                    "ConnectionStrings__DefaultConnection",
                    ConnectionStringReachableFromContainer(postgres.ConnectionString))
                .WithEnvironment("Licensing__Mode", "Disabled")
                .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
                .WithEnvironment(launch)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged(
                    "Application is shutting down", options => options.WithMode(WaitStrategyMode.OneShot)))
                .Build();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await worker.StartAsync(timeout.Token);
            (await worker.GetExitCodeAsync(timeout.Token)).Should().Be(0);
            timeout.IsCancellationRequested.Should().BeFalse();
            var logs = await worker.GetLogsAsync();
            output.WriteLine(logs.Stdout);
            output.WriteLine(logs.Stderr);

            var terminal = await store.GetAsync(assigned.OperationId);
            terminal.Should().NotBeNull();
            terminal!.Status.Should().Be(ExecutionJobStatus.Succeeded, terminal.ErrorMessage);
            terminal.ArtifactReferences.Should().ContainSingle();
            var artifact = terminal.ArtifactReferences.Single();
            artifact.Should().StartWith("data:application/geo+json;base64,");
            using var document = JsonDocument.Parse(Convert.FromBase64String(artifact[(artifact.IndexOf(',') + 1)..]));
            var features = document.RootElement.GetProperty("features");
            features.GetArrayLength().Should().Be(1);
            var feature = features[0];
            feature.GetProperty("geometry").GetProperty("type").GetString().Should().Be("Point");
            feature.GetProperty("geometry").GetProperty("coordinates").EnumerateArray()
                .Select(value => value.GetDouble()).Should().Equal(12.25, -4.5, 7.75);
            feature.GetProperty("properties").GetProperty("name").GetString().Should().Be("alpha");
            feature.GetProperty("properties").GetProperty("value").GetString().Should().Be("17");
            (await store.GetAsync(unrelated.OperationId)).Should().BeEquivalentTo(queuedUnrelated);
            (await queue.GetQueueDepthAsync()).Should().Be(1);
        }
        finally
        {
            await postgres.DisposeAsync();
        }
    }

    private static IContainer BuildWorker(string image, string hostConnectionString, string hostOutputRoot)
        => new ContainerBuilder()
            .WithImage(image)
            .WithExtraHost(DockerHostAlias, "host-gateway")
            .WithEnvironment(
                "ConnectionStrings__DefaultConnection",
                ConnectionStringReachableFromContainer(hostConnectionString))
            .WithEnvironment("Geoprocessing__OutputStaging__Enabled", "true")
            .WithEnvironment("Geoprocessing__OutputStaging__Provider", "local")
            .WithEnvironment("Geoprocessing__OutputStaging__StoreReference", "container-certification")
            .WithEnvironment("Geoprocessing__OutputStaging__LocalRootPath", ContainerOutputRoot)
            .WithEnvironment("Geoprocessing__OutputStaging__MaxInlineArtifactBytes", "1024")
            .WithEnvironment("Geoprocessing__OutputStaging__PersistenceClass", "shared-persistent")
            .WithEnvironment("Geoprocessing__OutputStaging__BackupIdentity", "qualification-backup")
            .WithEnvironment("Geoprocessing__OutputStaging__BackupStoreReferences__0", "container-certification")
            .WithEnvironment("Geoprocessing__OutputStaging__ConfigurationDigest",
                GeoprocessingOutputStoreAttestation.Create(new GeoprocessingOutputStagingOptions
                {
                    StoreReference = "container-certification",
                    MaxInlineArtifactBytes = 1024,
                    PersistenceClass = "shared-persistent",
                    BackupIdentity = "qualification-backup",
                    BackupStoreReferences = ["container-certification"],
                }).ConfigurationDigest)
            .WithBindMount(hostOutputRoot, ContainerOutputRoot)
            .Build();

    // The test database is published on the Docker host, which a container reaches through the host gateway.
    private static string ConnectionStringReachableFromContainer(string hostConnectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(hostConnectionString);
        if (builder.Host is "localhost" or "127.0.0.1" or "::1")
        {
            builder.Host = DockerHostAlias;
        }

        return builder.ConnectionString;
    }

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
        Dictionary<string, string> parameters,
        string backend = LocalBatchComputeBackend.BackendId)
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
                Backend = backend,
                WorkloadName = parameters[GdalWorkerParameterKeys.ProcessDefinitions],
                RuntimeProfile = RuntimeProfiles.Native,
                Parameters = parameters,
            },
        };
    }

    private static async Task<ExecutionJobRecord> WaitForJobAsync(
        PostgresExecutionJobStore store,
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

    private sealed record JobSubstrate(
        PostgresExecutionJobStore Store,
        PostgresJobQueue Queue,
        PostgresExecutionLogStore LogStore)
    {
        public static async Task<JobSubstrate> CreateEmptyAsync(PostgresFixture postgres)
        {
            await postgres.DataSource.ClearJobSubstrateAsync();
            var database = new JobStoreDatabase(postgres.DataSource);
            var options = Options.Create(new JobStoreOptions());
            return new JobSubstrate(
                new PostgresExecutionJobStore(database, options, NullLogger<PostgresExecutionJobStore>.Instance),
                new PostgresJobQueue(database, NullLogger<PostgresJobQueue>.Instance),
                new PostgresExecutionLogStore(database, options, NullLogger<PostgresExecutionLogStore>.Instance));
        }
    }
}
