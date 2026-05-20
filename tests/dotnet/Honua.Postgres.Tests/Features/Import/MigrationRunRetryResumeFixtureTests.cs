// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Services;

namespace Honua.Postgres.Tests.Features.Import;

/// <summary>
/// Integration coverage for issue #1033 slice 3: drive a small deterministic GeoServer
/// fixture through a failure-injection wrapper, prove the resulting run metrics carry
/// the slice-3 retry/resume/idempotency counters, and confirm the slice-2 baseline
/// evaluator still classifies the recovered run.
/// </summary>
/// <remarks>
/// Deliberately does not require a Postgres container: the wrapper is a deterministic
/// in-process pipeline driven by a JSON fixture so the test stays fast and can run on
/// the PR gate alongside the other Postgres test-project fixtures.
/// </remarks>
public sealed class MigrationRunRetryResumeFixtureTests
{
    private const string RunId = "fixture-geoserver-small-001";

    [Fact]
    public async Task RecoveredRun_EmitsRetryResumeCounters_AndPassesBaselineEvaluator()
    {
        var fixture = LoadFixture();
        var store = new InMemoryMigrationRunCheckpointStore();

        // Attempt 1: simulate a source failure halfway through.
        var firstAttempt = RunPipeline(fixture, store, failAfterIndex: fixture.Resources.Length / 2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => firstAttempt.RunAsync());

        var firstArtifact = firstAttempt.BuildArtifact();
        firstArtifact.Totals.ResumeFromCheckpoint.Should().BeFalse();
        firstArtifact.Totals.ResumeCount.Should().BeNull();

        // Attempt 2: resume from the checkpoint, complete the run.
        var secondAttempt = RunPipeline(fixture, store, failAfterIndex: int.MaxValue);
        await secondAttempt.RunAsync();

        var secondArtifact = secondAttempt.BuildArtifact();
        secondArtifact.Totals.ResumeCount.Should().Be(1, "the second attempt loaded a checkpoint");
        secondArtifact.Totals.ResumeFromCheckpoint.Should().BeTrue();
        secondArtifact.Totals.RetryCount.Should().Be(1);
        secondArtifact.Totals.CancellationCount.Should().BeNull();
        secondArtifact.Totals.FeatureCount.Should().Be(secondAttempt.TotalFeatures);
        secondArtifact.Totals.ResourceCount.Should().Be(fixture.Resources.Length);

        // Slice-2 baseline evaluator must still classify the recovered run.
        var evaluation = MigrationRunMetricsBaselineEvaluator.TryEvaluate(
            secondArtifact,
            MigrationCostPerformanceFixtureSizes.Small);

        evaluation.Should().NotBeNull("the GeoServer small baseline is seeded by slice 2");
        evaluation!.SourceFamily.Should().Be(MigrationCostPerformanceSourceFamilies.GeoServerRest);
        evaluation.Signals.Should().Contain(s => s.Metric == "retryCount");
        evaluation.Signals.Should().Contain(s => s.Metric == "resumeCount");

        // The recovered run intentionally exercises the warn band for retries/resumes;
        // it must remain in Warn or Pass — never Fail — because the seeded thresholds
        // tolerate up to 10 retries and 5 resumes before failing.
        evaluation.Status.Should().BeOneOf(
            MigrationMetricBaselineStatuses.Pass,
            MigrationMetricBaselineStatuses.Warn);
    }

    [Fact]
    public async Task IdempotentReplay_OnAlreadyCompletedRun_LeavesArtifactUnchangedAndRecordsReplay()
    {
        var fixture = LoadFixture();
        var store = new InMemoryMigrationRunCheckpointStore();

        var first = RunPipeline(fixture, store, failAfterIndex: int.MaxValue);
        await first.RunAsync();
        var firstArtifact = first.BuildArtifact();
        firstArtifact.Totals.IdempotentReplayCount.Should().BeNull();

        // Re-seed the checkpoint to the final state to simulate a re-run hitting an already-completed apply.
        await store.SaveAsync(new MigrationRunCheckpoint
        {
            RunId = RunId,
            Phase = MigrationCostPerformancePhases.Apply,
            ResumeMarker = "complete",
            CompletedItemCount = fixture.Resources.Length,
            CapturedAt = DateTimeOffset.UtcNow,
            Attempt = 2
        });

        var replay = RunPipeline(fixture, store, failAfterIndex: int.MaxValue);
        await replay.RunAsync();
        var replayArtifact = replay.BuildArtifact();

        replayArtifact.Totals.IdempotentReplayCount.Should().Be(1);
        replayArtifact.Totals.ResumeFromCheckpoint.Should().BeTrue();
        replay.AppliedItems.Should().BeEmpty("repeated apply must not produce new manifest items");
    }

    private static FixtureFile LoadFixture()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Features",
            "Import",
            "Fixtures",
            "RetryResume",
            "GeoServerSmallFixture.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var resources = new List<FixtureResource>();
        foreach (var resource in root.GetProperty("resources").EnumerateArray())
        {
            resources.Add(new FixtureResource(
                Id: resource.GetProperty("id").GetString() ?? string.Empty,
                FeatureCount: resource.GetProperty("featureCount").GetInt64()));
        }

        return new FixtureFile(
            FixtureProfile: root.GetProperty("fixtureProfile").GetString() ?? string.Empty,
            SourceFamily: root.GetProperty("sourceFamily").GetString() ?? string.Empty,
            Size: root.GetProperty("size").GetString() ?? string.Empty,
            DisplayName: root.GetProperty("displayName").GetString() ?? string.Empty,
            Resources: resources.ToArray());
    }

    private static FixtureRunner RunPipeline(
        FixtureFile fixture,
        IMigrationRunCheckpointStore store,
        int failAfterIndex) => new(fixture, store, failAfterIndex);

    private sealed record FixtureFile(
        string FixtureProfile,
        string SourceFamily,
        string Size,
        string DisplayName,
        FixtureResource[] Resources);

    private sealed record FixtureResource(string Id, long FeatureCount);

    /// <summary>
    /// Failure-injection wrapper that drives the fixture through a recorder + checkpoint
    /// store. Mirrors the shape of the Core retry/resume runner but operates against
    /// realistic fixture data so the integration test exercises the same metric surface
    /// the production migration pipeline uses.
    /// </summary>
    private sealed class FixtureRunner
    {
        private readonly FixtureFile _fixture;
        private readonly IMigrationRunCheckpointStore _store;
        private readonly int _failAfterIndex;
        private readonly MigrationRunMetricsRecorder _recorder = new();
        private readonly List<string> _applied = new();
        private long _totalFeatures;

        public FixtureRunner(FixtureFile fixture, IMigrationRunCheckpointStore store, int failAfterIndex)
        {
            _fixture = fixture;
            _store = store;
            _failAfterIndex = failAfterIndex;
        }

        public IReadOnlyList<string> AppliedItems => _applied;
        public long TotalFeatures => _totalFeatures;

        public async Task RunAsync()
        {
            using (_recorder.BeginPhase(MigrationCostPerformancePhases.Scan))
            {
                _recorder.RecordSourceRequest(1);
                _recorder.RecordBytesRead(2_048);
                _recorder.RecordResourceCount(_fixture.Resources.Length);
            }

            using (_recorder.BeginPhase(MigrationCostPerformancePhases.Apply))
            {
                var existing = await _store.LoadAsync(RunId);
                var startIndex = 0;
                if (existing != null)
                {
                    _recorder.RecordResume($"fixture:{existing.Phase}:{existing.CompletedItemCount}");
                    _recorder.RecordResumeFromCheckpoint(true);
                    startIndex = existing.CompletedItemCount;

                    // Seed already-applied features into totals so the recovered artifact
                    // matches a clean run.
                    for (var j = 0; j < startIndex; j++)
                    {
                        _totalFeatures += _fixture.Resources[j].FeatureCount;
                        _recorder.RecordFeatureCount(_fixture.Resources[j].FeatureCount);
                        _recorder.RecordSourceRequest(1);
                    }

                    if (startIndex >= _fixture.Resources.Length)
                    {
                        _recorder.RecordIdempotentReplay();
                        return;
                    }

                    _recorder.RecordRetry();
                }
                else
                {
                    _recorder.RecordResumeFromCheckpoint(false);
                }

                for (var i = startIndex; i < _fixture.Resources.Length; i++)
                {
                    if (i >= _failAfterIndex)
                    {
                        await _store.SaveAsync(NewCheckpoint(i));
                        throw new InvalidOperationException($"simulated source failure at item {i}");
                    }

                    var resource = _fixture.Resources[i];
                    _applied.Add(resource.Id);
                    _totalFeatures += resource.FeatureCount;
                    _recorder.RecordFeatureCount(resource.FeatureCount);
                    _recorder.RecordSourceRequest(1);
                    _recorder.RecordBytesRead(resource.FeatureCount * 128);
                    _recorder.RecordBytesWritten(resource.FeatureCount * 96);
                    await _store.SaveAsync(NewCheckpoint(i + 1));
                }

                await _store.DeleteAsync(RunId);
            }
        }

        public MigrationRunMetricsArtifact BuildArtifact() => _recorder.Build(
            sourceKind: "geoserver-rest",
            sourceFamily: MigrationCostPerformanceSourceFamilies.GeoServerRest,
            source: new MigrationSourceIdentity
            {
                DisplayName = _fixture.DisplayName,
                BaseUrl = "https://fixture.example/geoserver"
            },
            measurementScope: "slice3 fixture retry/resume",
            runId: RunId);

        private MigrationRunCheckpoint NewCheckpoint(int completedCount) => new()
        {
            RunId = RunId,
            Phase = MigrationCostPerformancePhases.Apply,
            ResumeMarker = $"resource-cursor:{completedCount}",
            CompletedItemCount = completedCount,
            CapturedAt = DateTimeOffset.UtcNow,
            Attempt = 1
        };
    }
}
