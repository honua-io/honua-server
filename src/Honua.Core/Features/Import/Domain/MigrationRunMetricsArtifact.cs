// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// Release-safe, threshold-free schema for raw migration cost and performance measurements
/// emitted by the migration pipeline (scan, manifest, apply, import).
/// </summary>
/// <remarks>
/// <para>
/// This artifact captures per-source-family raw metrics only. It deliberately does not include
/// pass/warn/fail thresholds, signals, or baselines; those are layered on top by the
/// <see cref="MigrationCostPerformanceEvidenceArtifact"/> in a later slice of issue #1033.
/// </para>
/// <para>
/// Privacy posture: the artifact never includes source URLs, credential values, query strings,
/// or source feature/coverage payloads. Source identity is reduced to safe display labels.
/// </para>
/// </remarks>
public sealed record MigrationRunMetricsArtifact
{
    /// <summary>Stable artifact kind identifier.</summary>
    public string ArtifactKind { get; init; } = "honua.migration.run-metrics";

    /// <summary>Artifact schema version.</summary>
    public string ArtifactVersion { get; init; } = "1.0";

    /// <summary>
    /// Source kind identifier such as <c>geoserver-rest</c>, <c>ogc-wfs</c>, or
    /// <c>arcgis-geoservices-rest</c>.
    /// </summary>
    public required string SourceKind { get; init; }

    /// <summary>
    /// Source family classification (matches <see cref="MigrationCostPerformanceSourceFamilies"/>).
    /// </summary>
    public required string SourceFamily { get; init; }

    /// <summary>Safe source summary with private URLs and credentials omitted.</summary>
    public required MigrationRunMetricsSourceSummary Source { get; init; }

    /// <summary>Optional deterministic run identifier supplied by the harness or job.</summary>
    public string? RunId { get; init; }

    /// <summary>Human-readable scope for the measured run.</summary>
    public required string MeasurementScope { get; init; }

    /// <summary>Wall-clock instant when the run started.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Wall-clock instant when the run completed.</summary>
    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>Aggregate totals across all measured phases.</summary>
    public required MigrationRunMetricsValues Totals { get; init; }

    /// <summary>Phase-level measurements in deterministic order.</summary>
    public MigrationRunMetricsPhase[] Phases { get; init; } = [];

    /// <summary>
    /// CPU and memory samples collected at intervals by the recorder. Empty when sampling
    /// was not enabled or no samples were captured.
    /// </summary>
    public MigrationRunMetricsResourceSample[] ResourceSamples { get; init; } = [];

    /// <summary>Resume markers (idempotency keys, resume tokens) observed during the run.</summary>
    public string[] ResumeMarkers { get; init; } = [];

    /// <summary>Privacy posture proving the artifact intentionally excludes secrets.</summary>
    public required MigrationRunMetricsPrivacySummary Privacy { get; init; }
}

/// <summary>
/// Secret-safe source identity used in migration run metrics.
/// </summary>
public sealed record MigrationRunMetricsSourceSummary
{
    /// <summary>Human-readable source label after URL and credential redaction.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Source product name when known.</summary>
    public string? Product { get; init; }

    /// <summary>Source product version when known.</summary>
    public string? Version { get; init; }

    /// <summary>Source service type or protocol subtype when known.</summary>
    public string? ServiceType { get; init; }
}

/// <summary>
/// Raw measurement values for a phase or aggregate scope.
/// </summary>
public sealed record MigrationRunMetricsValues
{
    /// <summary>Wall-clock duration in milliseconds.</summary>
    public long? DurationMilliseconds { get; init; }

    /// <summary>HTTP or other source requests issued in this scope.</summary>
    public long? SourceRequestCount { get; init; }

    /// <summary>Bytes read from source systems or staging artifacts.</summary>
    public long? BytesRead { get; init; }

    /// <summary>Bytes written to Honua-owned stores or output artifacts.</summary>
    public long? BytesWritten { get; init; }

    /// <summary>Retry attempts observed.</summary>
    public int? RetryCount { get; init; }

    /// <summary>Resume attempts observed.</summary>
    public int? ResumeCount { get; init; }

    /// <summary>
    /// Whether the run resumed from a previously persisted checkpoint
    /// (issue #1033 slice 3). Null when the run did not attempt to resume.
    /// </summary>
    public bool? ResumeFromCheckpoint { get; init; }

    /// <summary>
    /// Number of times an apply phase replayed previously-applied work without producing
    /// any incremental change (idempotency evidence; issue #1033 slice 3).
    /// </summary>
    public int? IdempotentReplayCount { get; init; }

    /// <summary>
    /// Number of times the run observed a cancellation request (issue #1033 slice 3).
    /// </summary>
    public int? CancellationCount { get; init; }

    /// <summary>CPU milliseconds when the recorder can measure them.</summary>
    public long? CpuMilliseconds { get; init; }

    /// <summary>Peak resident memory in bytes when the recorder can measure it.</summary>
    public long? PeakMemoryBytes { get; init; }

    /// <summary>Database growth in bytes attributed to this scope.</summary>
    public long? DatabaseGrowthBytes { get; init; }

    /// <summary>Database row growth attributed to this scope.</summary>
    public long? DatabaseGrowthRows { get; init; }

    /// <summary>Size in bytes of the evidence artifact emitted for this scope.</summary>
    public long? ArtifactBytes { get; init; }

    /// <summary>Source resources processed (workspaces, layers, services).</summary>
    public long? ResourceCount { get; init; }

    /// <summary>Features processed.</summary>
    public long? FeatureCount { get; init; }

    /// <summary>Coverages or raster assets processed.</summary>
    public long? CoverageCount { get; init; }

    /// <summary>Resources processed per second.</summary>
    public double? ResourceThroughputPerSecond { get; init; }

    /// <summary>Features processed per second.</summary>
    public double? FeatureThroughputPerSecond { get; init; }

    /// <summary>Coverages processed per second.</summary>
    public double? CoverageThroughputPerSecond { get; init; }

    /// <summary>Items requiring manual review.</summary>
    public int? ManualReviewCount { get; init; }

    /// <summary>Total candidate items used as the denominator for manual review ratio.</summary>
    public int? CandidateItemCount { get; init; }

    /// <summary>Manual review ratio (manual / candidate) when both counts are known.</summary>
    public double? ManualReviewRatio { get; init; }
}

/// <summary>
/// Phase-level raw measurement.
/// </summary>
public sealed record MigrationRunMetricsPhase
{
    /// <summary>Phase identifier (see <see cref="MigrationCostPerformancePhases"/>).</summary>
    public required string Phase { get; init; }

    /// <summary>Wall-clock instant when the phase started.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Wall-clock instant when the phase completed.</summary>
    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>Raw metrics for the phase.</summary>
    public required MigrationRunMetricsValues Metrics { get; init; }
}

/// <summary>
/// CPU and memory sample captured at a single instant during the run.
/// </summary>
public sealed record MigrationRunMetricsResourceSample
{
    /// <summary>Wall-clock instant the sample was taken.</summary>
    public required DateTimeOffset SampledAt { get; init; }

    /// <summary>Phase active when the sample was taken, when known.</summary>
    public string? Phase { get; init; }

    /// <summary>CPU milliseconds consumed by the process at sample time when available.</summary>
    public long? CpuMilliseconds { get; init; }

    /// <summary>Working-set memory bytes reported at sample time when available.</summary>
    public long? WorkingSetBytes { get; init; }

    /// <summary>Managed-heap GC bytes reported at sample time when available.</summary>
    public long? GcHeapBytes { get; init; }
}

/// <summary>
/// Privacy posture for migration run metrics.
/// </summary>
public sealed record MigrationRunMetricsPrivacySummary
{
    /// <summary>Whether source URLs are present in the artifact (always <c>false</c>).</summary>
    public bool SourceUrlsIncluded { get; init; }

    /// <summary>Whether credential values are present in the artifact (always <c>false</c>).</summary>
    public bool CredentialValuesIncluded { get; init; }

    /// <summary>Whether source feature/coverage payloads are present (always <c>false</c>).</summary>
    public bool SourceDataIncluded { get; init; }

    /// <summary>Fields intentionally omitted from the artifact.</summary>
    public string[] OmittedFields { get; init; } = [];
}
