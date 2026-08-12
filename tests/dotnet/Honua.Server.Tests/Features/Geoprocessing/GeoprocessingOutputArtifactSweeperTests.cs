// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.FileStorage;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Orphan reconciliation for staged geoprocessing outputs (#3089): the sweeper
/// reclaims losing-attempt staging, terminal-failure staging, and expired orphans,
/// while never touching the current attempt of a live job, the published winning
/// set of a succeeded job, an actively read (leased) object, an object still inside
/// the sweep grace, or keys outside the canonical scheme. Runs against the real
/// filesystem store implementation.
/// </summary>
public sealed class GeoprocessingOutputArtifactSweeperTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("honua-gp-sweeper-tests-").FullName;
    private readonly FileSystemGeoprocessingOutputObjectStore _store;
    private readonly IExecutionJobStore _jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
    private readonly GeoprocessingOutputStagingOptions _options;

    public GeoprocessingOutputArtifactSweeperTests()
    {
        _options = new GeoprocessingOutputStagingOptions
        {
            Enabled = true,
            LocalRootPath = _root,
            SweepGrace = TimeSpan.Zero,
            OrphanRetention = TimeSpan.Zero,
        };
        _store = new FileSystemGeoprocessingOutputObjectStore(Options.Create(_options));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort scratch cleanup.
        }
    }

    [UnitTest]
    public async Task Sweep_ExpiredJobRecord_DeletesOrphan()
    {
        var key = await StageObjectAsync("job-gone", attempt: 1);
        _jobStore.GetAsync("job-gone", Arg.Any<CancellationToken>()).Returns((ExecutionJobRecord?)null);

        var result = await CreateSweeper().SweepOnceAsync(CancellationToken.None);

        result.Deleted.Should().Be(1);
        (await _store.GetInfoAsync(key)).Should().BeNull();
    }

    [UnitTest]
    public async Task Sweep_ExpiredJobRecordWithinOrphanRetention_IsKept()
    {
        var options = _options;
        options.OrphanRetention = TimeSpan.FromDays(7);
        var key = await StageObjectAsync("job-gone", attempt: 1);
        _jobStore.GetAsync("job-gone", Arg.Any<CancellationToken>()).Returns((ExecutionJobRecord?)null);

        var result = await CreateSweeper().SweepOnceAsync(CancellationToken.None);

        result.Deleted.Should().Be(0);
        (await _store.GetInfoAsync(key)).Should().NotBeNull();
    }

    [UnitTest]
    public async Task Sweep_StaleAttemptOfRunningJob_IsDeleted_CurrentAttemptKept()
    {
        var staleKey = await StageObjectAsync("job-live", attempt: 1);
        var currentKey = await StageObjectAsync("job-live", attempt: 2);
        _jobStore.GetAsync("job-live", Arg.Any<CancellationToken>())
            .Returns(CreateJob("job-live", ExecutionJobStatus.Running, attemptCount: 2));

        var result = await CreateSweeper().SweepOnceAsync(CancellationToken.None);

        result.Deleted.Should().Be(1);
        (await _store.GetInfoAsync(staleKey)).Should().BeNull();
        (await _store.GetInfoAsync(currentKey)).Should().NotBeNull();
    }

    [UnitTest]
    public async Task Sweep_SucceededJob_KeepsPublishedSet_DeletesUnreferenced()
    {
        var winningKey = await StageObjectAsync("job-done", attempt: 2);
        var losingKey = await StageObjectAsync("job-done", attempt: 1);
        var job = CreateJob("job-done", ExecutionJobStatus.Succeeded, attemptCount: 2) with
        {
            ArtifactReferences = [RasterOutputJson.Serialize(CreateDescriptor("job-done", 2, winningKey))]
        };
        _jobStore.GetAsync("job-done", Arg.Any<CancellationToken>()).Returns(job);

        var result = await CreateSweeper().SweepOnceAsync(CancellationToken.None);

        result.Deleted.Should().Be(1);
        (await _store.GetInfoAsync(winningKey)).Should().NotBeNull();
        (await _store.GetInfoAsync(losingKey)).Should().BeNull();
    }

    [UnitTest]
    public async Task Sweep_CancelledJob_DeletesStaging()
    {
        var key = await StageObjectAsync("job-cancelled", attempt: 1);
        _jobStore.GetAsync("job-cancelled", Arg.Any<CancellationToken>())
            .Returns(CreateJob("job-cancelled", ExecutionJobStatus.Cancelled, attemptCount: 1));

        var result = await CreateSweeper().SweepOnceAsync(CancellationToken.None);

        result.Deleted.Should().Be(1);
        (await _store.GetInfoAsync(key)).Should().BeNull();
    }

    [UnitTest]
    public async Task Sweep_ActivelyReadObject_IsNeverDeleted()
    {
        var key = await StageObjectAsync("job-reading", attempt: 1);
        _jobStore.GetAsync("job-reading", Arg.Any<CancellationToken>())
            .Returns(CreateJob("job-reading", ExecutionJobStatus.Cancelled, attemptCount: 1));
        (await _store.TryAcquireReadLeaseAsync(key, TimeSpan.FromMinutes(15))).Should().BeTrue();

        var result = await CreateSweeper().SweepOnceAsync(CancellationToken.None);

        result.Deleted.Should().Be(0);
        (await _store.GetInfoAsync(key)).Should().NotBeNull();
    }

    [UnitTest]
    public async Task Sweep_ObjectWithinGrace_IsNeverDeleted()
    {
        var options = _options;
        options.SweepGrace = TimeSpan.FromHours(1);
        var key = await StageObjectAsync("job-fresh", attempt: 1);
        _jobStore.GetAsync("job-fresh", Arg.Any<CancellationToken>()).Returns((ExecutionJobRecord?)null);

        var result = await CreateSweeper().SweepOnceAsync(CancellationToken.None);

        result.Deleted.Should().Be(0);
        (await _store.GetInfoAsync(key)).Should().NotBeNull();
    }

    [UnitTest]
    public async Task Sweep_ForeignKeyShape_IsNeverDeleted()
    {
        // A key under the prefix but outside the canonical {job}/a{attempt}/... scheme.
        await using (var content = new MemoryStream(new byte[] { 1, 2, 3 }))
        {
            await _store.WriteAsync("gp/outputs/manual-upload.tif", content, "image/tiff");
        }

        var result = await CreateSweeper().SweepOnceAsync(CancellationToken.None);

        result.Deleted.Should().Be(0);
        (await _store.GetInfoAsync("gp/outputs/manual-upload.tif")).Should().NotBeNull();
    }

    private GeoprocessingOutputArtifactSweeper CreateSweeper()
        => new(
            _store,
            _jobStore,
            new StaticOptionsMonitor<GeoprocessingOutputStagingOptions>(_options),
            NullLogger<GeoprocessingOutputArtifactSweeper>.Instance);

    private async Task<string> StageObjectAsync(string jobId, int attempt)
    {
        var key = GeoprocessingOutputObjectKeys.Build("gp/outputs", jobId, attempt, "output1", "result.tif");
        await using var content = new MemoryStream(new byte[] { 1, 2, 3, 4 });
        await _store.WriteAsync(key, content, "image/tiff");
        return key;
    }

    private static ExecutionJobRecord CreateJob(string jobId, ExecutionJobStatus status, int attemptCount)
    {
        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = jobId,
            Status = status,
            CreatedAt = now.AddHours(-1),
            UpdatedAt = now,
            AttemptCount = attemptCount,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "raster.resample"
            }
        };
    }

    private static StagedObjectRasterOutputDescriptor CreateDescriptor(string jobId, int attempt, string objectKey)
        => new()
        {
            JobId = jobId,
            AttemptNumber = attempt,
            OutputName = "output1",
            Content = new RasterContentIdentity
            {
                SizeBytes = 4,
                MediaType = "image/tiff",
                Checksum = new RasterChecksum("sha256", new string('a', 64)),
            },
            ProducingEngine = RasterOutputContract.GdalWorkerEngine,
            Provider = CloudStorageProvider.Local,
            StoreReference = "gp-outputs",
            ObjectKey = objectKey,
        };

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
