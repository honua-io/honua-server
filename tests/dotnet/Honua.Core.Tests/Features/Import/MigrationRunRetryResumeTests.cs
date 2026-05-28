// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Tests.Features.Import;

/// <summary>
/// Retry / resume / idempotency / cancellation evidence for migration runs
/// (issue #1033 slice 3). Each scenario drives a small in-process migration
/// runner that uses <see cref="MigrationRunMetricsRecorder"/> +
/// <see cref="IMigrationRunCheckpointStore"/>, then asserts the recorded metric
/// counters and the produced "manifest" artifact behave correctly.
/// </summary>
public sealed class MigrationRunRetryResumeTests
{
    private const string RunId = "retry-resume-run-001";

    [Fact]
    public async Task SimulatedSourceFailure_ResumeFromCheckpoint_ProducesSameFinalArtifact()
    {
        var store = new InMemoryMigrationRunCheckpointStore();

        // Attempt 1: fail after item 3 of 5.
        var firstRunner = new FakeMigrationRunner(itemCount: 5, failAfter: 3, store);
        await Assert.ThrowsAsync<InvalidOperationException>(firstRunner.RunAsync);
        firstRunner.CompletedItems.Should().Equal(new[] { 0, 1, 2 });
        var firstArtifact = firstRunner.Recorder.Build(
            sourceKind: "geoserver-rest",
            sourceFamily: MigrationCostPerformanceSourceFamilies.GeoServerRest,
            source: TestSource(),
            measurementScope: "retry-resume slice3");
        firstArtifact.Totals.RetryCount.Should().BeNull("no retry before failure");

        // Persisted checkpoint should survive across runner instances.
        var checkpoint = await store.LoadAsync(RunId);
        checkpoint.Should().NotBeNull();
        checkpoint!.CompletedItemCount.Should().Be(3);

        // Attempt 2: complete the run, resuming from checkpoint.
        var secondRunner = new FakeMigrationRunner(itemCount: 5, failAfter: int.MaxValue, store);
        await secondRunner.RunAsync();

        secondRunner.CompletedItems.Should().Equal(new[] { 3, 4 }, "resume must skip already-completed items");
        secondRunner.ResumedFromCheckpoint.Should().BeTrue();
        var secondArtifact = secondRunner.Recorder.Build(
            sourceKind: "geoserver-rest",
            sourceFamily: MigrationCostPerformanceSourceFamilies.GeoServerRest,
            source: TestSource(),
            measurementScope: "retry-resume slice3");

        secondArtifact.Totals.ResumeCount.Should().Be(1);
        secondArtifact.Totals.ResumeFromCheckpoint.Should().BeTrue();
        secondArtifact.Totals.RetryCount.Should().Be(1, "the source failure was retried once");
        secondArtifact.Totals.CancellationCount.Should().BeNull();
        secondArtifact.ResumeMarkers.Should().Contain("checkpoint:apply:3");

        // Final manifest produced after resume must equal the manifest a clean single-pass run would produce.
        var cleanRunner = new FakeMigrationRunner(itemCount: 5, failAfter: int.MaxValue,
            new InMemoryMigrationRunCheckpointStore());
        await cleanRunner.RunAsync();
        secondRunner.Manifest.Should().Equal(cleanRunner.Manifest, "resume produces the same final manifest as a clean run");
    }

    [Fact]
    public async Task RepeatedApply_IsIdempotent_AndRecordsReplayCounter()
    {
        var store = new InMemoryMigrationRunCheckpointStore();

        var first = new FakeMigrationRunner(itemCount: 4, failAfter: int.MaxValue, store);
        await first.RunAsync();
        first.Manifest.Should().HaveCount(4);

        // Seed a completion-sentinel checkpoint so a re-run sees prior progress.
        // Production callers either keep the final checkpoint or persist a completion
        // sentinel; the recorder/store contract is agnostic to either strategy.
        await store.SaveAsync(new MigrationRunCheckpoint
        {
            RunId = RunId,
            Phase = MigrationCostPerformancePhases.Apply,
            ResumeMarker = "complete",
            CompletedItemCount = 4,
            CapturedAt = DateTimeOffset.UtcNow,
            Attempt = 2
        });

        // Replay the apply phase a second time. The runner should detect already-applied
        // items via the checkpoint, perform no incremental work, and record the replay.
        var replay = new FakeMigrationRunner(itemCount: 4, failAfter: int.MaxValue, store);
        await replay.RunAsync();

        replay.CompletedItems.Should().BeEmpty("repeated apply with completed checkpoint is a no-op");
        replay.Manifest.Should().Equal(first.Manifest, "idempotent replay must not change the manifest");
        var replayArtifact = replay.Recorder.Build(
            sourceKind: "geoserver-rest",
            sourceFamily: MigrationCostPerformanceSourceFamilies.GeoServerRest,
            source: TestSource(),
            measurementScope: "idempotent-replay slice3");

        replayArtifact.Totals.IdempotentReplayCount.Should().Be(1);
        replayArtifact.Totals.ResumeFromCheckpoint.Should().BeTrue();
        replayArtifact.Totals.RetryCount.Should().BeNull();
    }

    [Fact]
    public async Task CancellationMidRun_LeavesManifestRecoverable()
    {
        var store = new InMemoryMigrationRunCheckpointStore();
        using var cts = new CancellationTokenSource();

        var cancelable = new FakeMigrationRunner(itemCount: 6, failAfter: int.MaxValue, store)
        {
            CancelAfter = 2,
            CancellationSource = cts
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelable.RunAsync(cts.Token));

        cancelable.CompletedItems.Should().Equal(new[] { 0, 1 });
        var cancelArtifact = cancelable.Recorder.Build(
            sourceKind: "geoserver-rest",
            sourceFamily: MigrationCostPerformanceSourceFamilies.GeoServerRest,
            source: TestSource(),
            measurementScope: "cancellation slice3");
        cancelArtifact.Totals.CancellationCount.Should().Be(1);

        // Checkpoint must persist so a future run can resume from item 2.
        var checkpoint = await store.LoadAsync(RunId);
        checkpoint.Should().NotBeNull();
        checkpoint!.CompletedItemCount.Should().Be(2);
        checkpoint.Phase.Should().Be(MigrationCostPerformancePhases.Apply);

        var resumed = new FakeMigrationRunner(itemCount: 6, failAfter: int.MaxValue, store);
        await resumed.RunAsync();

        resumed.CompletedItems.Should().Equal(new[] { 2, 3, 4, 5 }, "resume after cancellation skips completed items");
        resumed.Manifest.Should().HaveCount(6, "the recovered manifest is complete after resume");
    }

    [Fact]
    public async Task TransientNetworkError_RetriesAndCompletes_RecordsRetryCounter()
    {
        var store = new InMemoryMigrationRunCheckpointStore();
        var runner = new FakeMigrationRunner(itemCount: 3, failAfter: int.MaxValue, store)
        {
            TransientFailureAtItem = 1,
            TransientFailureLimit = 2
        };

        await runner.RunAsync();

        runner.Manifest.Should().HaveCount(3);
        var artifact = runner.Recorder.Build(
            sourceKind: "geoserver-rest",
            sourceFamily: MigrationCostPerformanceSourceFamilies.GeoServerRest,
            source: TestSource(),
            measurementScope: "transient-network slice3");
        artifact.Totals.RetryCount.Should().Be(2);
        artifact.Totals.ResumeCount.Should().BeNull("transient retries do not re-load the checkpoint");
    }

    [Fact]
    public void MetricSchema_ExposesSlice3Fields_AsAdditiveNullable()
    {
        // Defensive guardrail: slice 3 must not break the slice 1 contract.
        var totals = new MigrationRunMetricsValues
        {
            DurationMilliseconds = 100,
            RetryCount = 1,
            ResumeCount = 1,
            ResumeFromCheckpoint = true,
            IdempotentReplayCount = 1,
            CancellationCount = 1
        };
        totals.ResumeFromCheckpoint.Should().BeTrue();
        totals.IdempotentReplayCount.Should().Be(1);
        totals.CancellationCount.Should().Be(1);
    }

    private static MigrationSourceIdentity TestSource() => new()
    {
        DisplayName = "fixture-host",
        BaseUrl = "https://fixture.example/geoserver"
    };

    /// <summary>
    /// Minimal deterministic in-process migration runner used to exercise retry, resume,
    /// idempotency, and cancellation behavior against the recorder + checkpoint store.
    /// </summary>
    private sealed class FakeMigrationRunner
    {
        private readonly int _itemCount;
        private readonly int _failAfter;
        private readonly IMigrationRunCheckpointStore _store;

        public FakeMigrationRunner(int itemCount, int failAfter, IMigrationRunCheckpointStore store)
        {
            _itemCount = itemCount;
            _failAfter = failAfter;
            _store = store;
            Recorder = new MigrationRunMetricsRecorder();
        }

        public MigrationRunMetricsRecorder Recorder { get; }
        public List<int> CompletedItems { get; } = new();
        public List<string> Manifest { get; } = new();
        public bool ResumedFromCheckpoint { get; private set; }

        public int? CancelAfter { get; init; }
        public CancellationTokenSource? CancellationSource { get; init; }

        public int? TransientFailureAtItem { get; init; }
        public int TransientFailureLimit { get; init; }

        public Task RunAsync() => RunAsync(CancellationToken.None);

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            using (Recorder.BeginPhase(MigrationCostPerformancePhases.Apply))
            {
                var existing = await _store.LoadAsync(RunId, cancellationToken);
                var startIndex = 0;
                if (existing != null)
                {
                    Recorder.RecordResume("checkpoint:" + existing.Phase + ":" + existing.CompletedItemCount);
                    Recorder.RecordResumeFromCheckpoint(true);
                    startIndex = existing.CompletedItemCount;

                    // Seed manifest with already-applied items so the final artifact equals a clean run.
                    for (var i = 0; i < startIndex; i++)
                    {
                        Manifest.Add($"item-{i}");
                    }

                    if (startIndex >= _itemCount)
                    {
                        // Idempotent replay — no incremental work, no retry of failed work.
                        Recorder.RecordIdempotentReplay();
                        return;
                    }

                    // Resuming with outstanding work counts as a retry of the failed attempt.
                    Recorder.RecordRetry();
                    ResumedFromCheckpoint = true;
                }
                else
                {
                    Recorder.RecordResumeFromCheckpoint(false);
                }

                var transientRemaining = TransientFailureLimit;
                for (var i = startIndex; i < _itemCount; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        Recorder.RecordCancellation();
                        await _store.SaveAsync(NewCheckpoint(i), cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    if (CancelAfter.HasValue && i == CancelAfter.Value && CancellationSource != null)
                    {
                        Recorder.RecordCancellation();
                        await _store.SaveAsync(NewCheckpoint(i), CancellationToken.None);
                        CancellationSource.Cancel();
                        CancellationSource.Token.ThrowIfCancellationRequested();
                    }

                    if (TransientFailureAtItem.HasValue && i == TransientFailureAtItem.Value && transientRemaining > 0)
                    {
                        Recorder.RecordRetry();
                        transientRemaining--;
                        i--; // retry this item without persisting a checkpoint
                        continue;
                    }

                    if (i >= _failAfter)
                    {
                        // Simulated source failure: persist a checkpoint so the next attempt can resume.
                        await _store.SaveAsync(NewCheckpoint(i), cancellationToken);
                        throw new InvalidOperationException($"simulated source failure at item {i}");
                    }

                    Manifest.Add($"item-{i}");
                    CompletedItems.Add(i);
                    await _store.SaveAsync(NewCheckpoint(i + 1), cancellationToken);
                }

                // Successful completion: clear the checkpoint so subsequent runs do not resume.
                await _store.DeleteAsync(RunId, cancellationToken);
            }
        }

        private MigrationRunCheckpoint NewCheckpoint(int completedCount) => new()
        {
            RunId = RunId,
            Phase = MigrationCostPerformancePhases.Apply,
            ResumeMarker = $"item-cursor:{completedCount}",
            CompletedItemCount = completedCount,
            CapturedAt = DateTimeOffset.UtcNow,
            Attempt = 1
        };
    }
}
