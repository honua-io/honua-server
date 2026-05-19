// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.Import.Services;

/// <summary>
/// Thread-safe collector for raw migration run metrics emitted by scan/manifest/apply/import code paths.
/// </summary>
/// <remarks>
/// <para>
/// The recorder buffers in-memory measurements (counters and sampled snapshots) and produces a
/// <see cref="MigrationRunMetricsArtifact"/> via <see cref="Build"/>. Builders scrub URLs and
/// credentials before publishing the artifact; the recorder itself never accepts URL or credential
/// inputs.
/// </para>
/// <para>
/// Phase scoping: callers wrap each migration phase with <see cref="BeginPhase"/> and dispose the
/// returned scope when the phase ends. Counters submitted inside the scope are attributed to that
/// phase; counters submitted outside any scope are attributed to the run totals only.
/// </para>
/// </remarks>
public sealed class MigrationRunMetricsRecorder
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PhaseAccumulator> _phases = new(StringComparer.Ordinal);
    private readonly List<MigrationRunMetricsResourceSample> _samples = [];
    private readonly HashSet<string> _resumeMarkers = new(StringComparer.Ordinal);
    private readonly DateTimeOffset _startedAt;
    private DateTimeOffset _completedAt;
    private string? _activePhase;
    private long _retryCount;
    private long _resumeCount;
    private long _sourceRequestCount;
    private long _bytesRead;
    private long _bytesWritten;
    private long _resourceCount;
    private long _featureCount;
    private long _coverageCount;
    private long _manualReviewCount;
    private long _candidateItemCount;
    private long _databaseGrowthBytes;
    private long _databaseGrowthRows;
    private long _artifactBytes;
    private long _cpuMilliseconds;
    private long _peakMemoryBytes;

    /// <summary>
    /// Initializes a new <see cref="MigrationRunMetricsRecorder"/>.
    /// </summary>
    /// <param name="startedAt">Wall-clock instant when the recorder was opened. Defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public MigrationRunMetricsRecorder(DateTimeOffset? startedAt = null)
    {
        _startedAt = startedAt ?? DateTimeOffset.UtcNow;
        _completedAt = _startedAt;
    }

    /// <summary>Wall-clock instant the recorder opened.</summary>
    public DateTimeOffset StartedAt => _startedAt;

    /// <summary>
    /// Starts a measurement scope for the given migration phase. Dispose the returned scope when
    /// the phase ends.
    /// </summary>
    /// <param name="phase">Phase identifier (see <see cref="MigrationCostPerformancePhases"/>).</param>
    public PhaseScope BeginPhase(string phase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);

        lock (_gate)
        {
            var accumulator = GetOrCreatePhase(phase, DateTimeOffset.UtcNow);
            _activePhase = phase;
            return new PhaseScope(this, phase, accumulator);
        }
    }

    /// <summary>Records a source request (typically HTTP) issued during the run.</summary>
    public void RecordSourceRequest(long count = 1)
    {
        if (count <= 0) return;
        lock (_gate)
        {
            _sourceRequestCount += count;
            CurrentPhase()?.AddSourceRequest(count);
        }
    }

    /// <summary>Records bytes read from a source.</summary>
    public void RecordBytesRead(long bytes)
    {
        if (bytes <= 0) return;
        lock (_gate)
        {
            _bytesRead += bytes;
            CurrentPhase()?.AddBytesRead(bytes);
        }
    }

    /// <summary>Records bytes written to a Honua-owned store or artifact.</summary>
    public void RecordBytesWritten(long bytes)
    {
        if (bytes <= 0) return;
        lock (_gate)
        {
            _bytesWritten += bytes;
            CurrentPhase()?.AddBytesWritten(bytes);
        }
    }

    /// <summary>Records a retry attempt.</summary>
    public void RecordRetry(long count = 1)
    {
        if (count <= 0) return;
        lock (_gate)
        {
            _retryCount += count;
            CurrentPhase()?.AddRetry(count);
        }
    }

    /// <summary>Records a resume attempt with an optional safe marker label (no URLs or secrets).</summary>
    public void RecordResume(string? safeMarker = null, long count = 1)
    {
        if (count <= 0) return;
        lock (_gate)
        {
            _resumeCount += count;
            CurrentPhase()?.AddResume(count);
            if (!string.IsNullOrWhiteSpace(safeMarker))
            {
                _resumeMarkers.Add(SafeMarker(safeMarker));
            }
        }
    }

    /// <summary>Records resource (workspace/layer/service) processing count.</summary>
    public void RecordResourceCount(long count)
    {
        if (count <= 0) return;
        lock (_gate)
        {
            _resourceCount += count;
            CurrentPhase()?.AddResourceCount(count);
        }
    }

    /// <summary>Records feature processing count.</summary>
    public void RecordFeatureCount(long count)
    {
        if (count <= 0) return;
        lock (_gate)
        {
            _featureCount += count;
            CurrentPhase()?.AddFeatureCount(count);
        }
    }

    /// <summary>Records coverage/raster processing count.</summary>
    public void RecordCoverageCount(long count)
    {
        if (count <= 0) return;
        lock (_gate)
        {
            _coverageCount += count;
            CurrentPhase()?.AddCoverageCount(count);
        }
    }

    /// <summary>Records the manual review ratio's numerator and denominator.</summary>
    public void RecordManualReview(long manualReview, long candidateItems)
    {
        if (manualReview <= 0 && candidateItems <= 0) return;
        lock (_gate)
        {
            if (manualReview > 0)
            {
                _manualReviewCount += manualReview;
                CurrentPhase()?.AddManualReview(manualReview);
            }

            if (candidateItems > 0)
            {
                _candidateItemCount += candidateItems;
                CurrentPhase()?.AddCandidateItem(candidateItems);
            }
        }
    }

    /// <summary>Records database growth attributed to the active phase.</summary>
    public void RecordDatabaseGrowth(long rows, long bytes)
    {
        if (rows <= 0 && bytes <= 0) return;
        lock (_gate)
        {
            _databaseGrowthRows += Math.Max(0, rows);
            _databaseGrowthBytes += Math.Max(0, bytes);
            CurrentPhase()?.AddDatabaseGrowth(rows, bytes);
        }
    }

    /// <summary>Records the size in bytes of an emitted evidence artifact.</summary>
    public void RecordArtifactBytes(long bytes)
    {
        if (bytes <= 0) return;
        lock (_gate)
        {
            _artifactBytes += bytes;
            CurrentPhase()?.AddArtifactBytes(bytes);
        }
    }

    /// <summary>
    /// Samples CPU and memory snapshots from the current process. Safe to call at intervals
    /// from a background timer.
    /// </summary>
    /// <param name="now">Optional wall-clock instant. Defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public MigrationRunMetricsResourceSample SampleResources(DateTimeOffset? now = null)
    {
        var sampledAt = now ?? DateTimeOffset.UtcNow;
        long? cpuMilliseconds = null;
        long? workingSetBytes = null;
        long? gcHeapBytes = null;
        string? activePhase;

        try
        {
            using var process = Process.GetCurrentProcess();
            cpuMilliseconds = (long)process.TotalProcessorTime.TotalMilliseconds;
            workingSetBytes = process.WorkingSet64;
        }
        catch (PlatformNotSupportedException)
        {
            // Process metrics unavailable on this platform.
        }
        catch (InvalidOperationException)
        {
            // Process has exited.
        }

        try
        {
            gcHeapBytes = GC.GetTotalMemory(forceFullCollection: false);
        }
        catch (OutOfMemoryException)
        {
            // Best-effort sampling.
        }

        lock (_gate)
        {
            activePhase = _activePhase;
            if (cpuMilliseconds is { } cpu)
            {
                _cpuMilliseconds = Math.Max(_cpuMilliseconds, cpu);
            }

            if (workingSetBytes is { } memory)
            {
                _peakMemoryBytes = Math.Max(_peakMemoryBytes, memory);
            }

            var sample = new MigrationRunMetricsResourceSample
            {
                SampledAt = sampledAt,
                Phase = activePhase,
                CpuMilliseconds = cpuMilliseconds,
                WorkingSetBytes = workingSetBytes,
                GcHeapBytes = gcHeapBytes
            };
            _samples.Add(sample);
            return sample;
        }
    }

    /// <summary>
    /// Builds a deterministic <see cref="MigrationRunMetricsArtifact"/> capturing the recorded
    /// measurements. Source URLs and credentials are scrubbed from the supplied source identity.
    /// </summary>
    /// <param name="sourceKind">Stable source kind identifier (e.g. <c>geoserver-rest</c>).</param>
    /// <param name="sourceFamily">Source family classification.</param>
    /// <param name="source">Raw source identity. URLs and credentials are stripped before emission.</param>
    /// <param name="measurementScope">Human-readable measurement scope.</param>
    /// <param name="runId">Optional deterministic run identifier.</param>
    /// <param name="completedAt">Optional wall-clock instant; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public MigrationRunMetricsArtifact Build(
        string sourceKind,
        string sourceFamily,
        MigrationSourceIdentity source,
        string measurementScope,
        string? runId = null,
        DateTimeOffset? completedAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFamily);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(measurementScope);

        lock (_gate)
        {
            _completedAt = completedAt ?? DateTimeOffset.UtcNow;
            if (_completedAt < _startedAt)
            {
                _completedAt = _startedAt;
            }

            return MigrationRunMetricsBuilder.Build(
                this,
                sourceKind,
                sourceFamily,
                source,
                measurementScope,
                runId);
        }
    }

    internal IReadOnlyDictionary<string, PhaseAccumulator> SnapshotPhases() => _phases;

    internal IReadOnlyList<MigrationRunMetricsResourceSample> SnapshotSamples() => _samples;

    internal IReadOnlyCollection<string> SnapshotResumeMarkers() => _resumeMarkers;

    internal DateTimeOffset CompletedAt => _completedAt;

    internal MigrationRunMetricsValues SnapshotTotals()
    {
        var duration = (long)Math.Max(0, (_completedAt - _startedAt).TotalMilliseconds);
        return new MigrationRunMetricsValues
        {
            DurationMilliseconds = duration,
            SourceRequestCount = _sourceRequestCount > 0 ? _sourceRequestCount : null,
            BytesRead = _bytesRead > 0 ? _bytesRead : null,
            BytesWritten = _bytesWritten > 0 ? _bytesWritten : null,
            RetryCount = _retryCount > 0 ? (int?)_retryCount : null,
            ResumeCount = _resumeCount > 0 ? (int?)_resumeCount : null,
            CpuMilliseconds = _cpuMilliseconds > 0 ? _cpuMilliseconds : null,
            PeakMemoryBytes = _peakMemoryBytes > 0 ? _peakMemoryBytes : null,
            DatabaseGrowthBytes = _databaseGrowthBytes > 0 ? _databaseGrowthBytes : null,
            DatabaseGrowthRows = _databaseGrowthRows > 0 ? _databaseGrowthRows : null,
            ArtifactBytes = _artifactBytes > 0 ? _artifactBytes : null,
            ResourceCount = _resourceCount > 0 ? _resourceCount : null,
            FeatureCount = _featureCount > 0 ? _featureCount : null,
            CoverageCount = _coverageCount > 0 ? _coverageCount : null,
            ResourceThroughputPerSecond = ComputeThroughput(_resourceCount, duration),
            FeatureThroughputPerSecond = ComputeThroughput(_featureCount, duration),
            CoverageThroughputPerSecond = ComputeThroughput(_coverageCount, duration),
            ManualReviewCount = _manualReviewCount > 0 ? (int?)_manualReviewCount : null,
            CandidateItemCount = _candidateItemCount > 0 ? (int?)_candidateItemCount : null,
            ManualReviewRatio = ComputeRatio(_manualReviewCount, _candidateItemCount)
        };
    }

    internal static double? ComputeThroughput(long count, long durationMilliseconds)
    {
        if (count <= 0 || durationMilliseconds <= 0) return null;
        return count / (durationMilliseconds / 1000d);
    }

    internal static double? ComputeRatio(long numerator, long denominator)
    {
        if (denominator <= 0) return null;
        return Math.Clamp((double)numerator / denominator, 0d, 1d);
    }

    private PhaseAccumulator? CurrentPhase()
        => _activePhase != null && _phases.TryGetValue(_activePhase, out var accumulator)
            ? accumulator
            : null;

    private PhaseAccumulator GetOrCreatePhase(string phase, DateTimeOffset startedAt)
    {
        if (!_phases.TryGetValue(phase, out var accumulator))
        {
            accumulator = new PhaseAccumulator(phase, startedAt);
            _phases.Add(phase, accumulator);
        }

        return accumulator;
    }

    private void EndPhase(string phase)
    {
        lock (_gate)
        {
            if (_phases.TryGetValue(phase, out var accumulator))
            {
                accumulator.Close(DateTimeOffset.UtcNow);
            }

            if (string.Equals(_activePhase, phase, StringComparison.Ordinal))
            {
                _activePhase = null;
            }
        }
    }

    private static string SafeMarker(string marker)
    {
        // Strip any accidental URL-like content; markers should be opaque identifiers.
        var trimmed = marker.Trim();
        if (trimmed.Contains("://", StringComparison.Ordinal))
        {
            return "[redacted-marker]";
        }

        return trimmed.Length > 128 ? trimmed[..128] : trimmed;
    }

    /// <summary>
    /// Disposable scope returned by <see cref="BeginPhase"/>.
    /// </summary>
    public sealed class PhaseScope : IDisposable
    {
        private readonly MigrationRunMetricsRecorder _recorder;
        private readonly string _phase;
        private bool _disposed;

        internal PhaseScope(MigrationRunMetricsRecorder recorder, string phase, PhaseAccumulator accumulator)
        {
            _recorder = recorder;
            _phase = phase;
            Accumulator = accumulator;
        }

        internal PhaseAccumulator Accumulator { get; }

        /// <summary>Phase identifier this scope applies to.</summary>
        public string Phase => _phase;

        /// <summary>Disposes the scope and closes the phase window.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _recorder.EndPhase(_phase);
        }
    }

    internal sealed class PhaseAccumulator
    {
        public PhaseAccumulator(string phase, DateTimeOffset startedAt)
        {
            Phase = phase;
            StartedAt = startedAt;
            CompletedAt = startedAt;
        }

        public string Phase { get; }
        public DateTimeOffset StartedAt { get; }
        public DateTimeOffset CompletedAt { get; private set; }
        public long SourceRequestCount { get; private set; }
        public long BytesRead { get; private set; }
        public long BytesWritten { get; private set; }
        public long RetryCount { get; private set; }
        public long ResumeCount { get; private set; }
        public long ResourceCount { get; private set; }
        public long FeatureCount { get; private set; }
        public long CoverageCount { get; private set; }
        public long ManualReviewCount { get; private set; }
        public long CandidateItemCount { get; private set; }
        public long DatabaseGrowthBytes { get; private set; }
        public long DatabaseGrowthRows { get; private set; }
        public long ArtifactBytes { get; private set; }

        public void AddSourceRequest(long count) => SourceRequestCount += count;
        public void AddBytesRead(long bytes) => BytesRead += bytes;
        public void AddBytesWritten(long bytes) => BytesWritten += bytes;
        public void AddRetry(long count) => RetryCount += count;
        public void AddResume(long count) => ResumeCount += count;
        public void AddResourceCount(long count) => ResourceCount += count;
        public void AddFeatureCount(long count) => FeatureCount += count;
        public void AddCoverageCount(long count) => CoverageCount += count;
        public void AddManualReview(long count) => ManualReviewCount += count;
        public void AddCandidateItem(long count) => CandidateItemCount += count;
        public void AddArtifactBytes(long bytes) => ArtifactBytes += bytes;

        public void AddDatabaseGrowth(long rows, long bytes)
        {
            if (rows > 0) DatabaseGrowthRows += rows;
            if (bytes > 0) DatabaseGrowthBytes += bytes;
        }

        public void Close(DateTimeOffset completedAt)
        {
            if (completedAt >= CompletedAt)
            {
                CompletedAt = completedAt;
            }
        }

        public MigrationRunMetricsValues ToValues()
        {
            var duration = (long)Math.Max(0, (CompletedAt - StartedAt).TotalMilliseconds);
            return new MigrationRunMetricsValues
            {
                DurationMilliseconds = duration,
                SourceRequestCount = SourceRequestCount > 0 ? SourceRequestCount : null,
                BytesRead = BytesRead > 0 ? BytesRead : null,
                BytesWritten = BytesWritten > 0 ? BytesWritten : null,
                RetryCount = RetryCount > 0 ? (int?)RetryCount : null,
                ResumeCount = ResumeCount > 0 ? (int?)ResumeCount : null,
                ResourceCount = ResourceCount > 0 ? ResourceCount : null,
                FeatureCount = FeatureCount > 0 ? FeatureCount : null,
                CoverageCount = CoverageCount > 0 ? CoverageCount : null,
                ResourceThroughputPerSecond = ComputeThroughput(ResourceCount, duration),
                FeatureThroughputPerSecond = ComputeThroughput(FeatureCount, duration),
                CoverageThroughputPerSecond = ComputeThroughput(CoverageCount, duration),
                ManualReviewCount = ManualReviewCount > 0 ? (int?)ManualReviewCount : null,
                CandidateItemCount = CandidateItemCount > 0 ? (int?)CandidateItemCount : null,
                ManualReviewRatio = ComputeRatio(ManualReviewCount, CandidateItemCount),
                DatabaseGrowthBytes = DatabaseGrowthBytes > 0 ? DatabaseGrowthBytes : null,
                DatabaseGrowthRows = DatabaseGrowthRows > 0 ? DatabaseGrowthRows : null,
                ArtifactBytes = ArtifactBytes > 0 ? ArtifactBytes : null
            };
        }
    }
}
