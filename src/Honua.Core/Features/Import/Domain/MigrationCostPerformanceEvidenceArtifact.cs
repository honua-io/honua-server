// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// Stable classification values for migration cost and performance evidence.
/// </summary>
public static class MigrationCostPerformanceClassifications
{
    /// <summary>Measurements are within the configured baseline.</summary>
    public const string Pass = "pass";

    /// <summary>Measurements are usable but outside the warning baseline or incomplete.</summary>
    public const string Warn = "warn";

    /// <summary>Measurements exceed the failure baseline or contain invalid values.</summary>
    public const string Fail = "fail";
}

/// <summary>
/// Stable migration phase identifiers used by cost and performance evidence.
/// </summary>
public static class MigrationCostPerformancePhases
{
    /// <summary>Source inventory scan phase.</summary>
    public const string Scan = "scan";

    /// <summary>Migration manifest generation phase.</summary>
    public const string Manifest = "manifest";

    /// <summary>Apply or publish planning phase.</summary>
    public const string Apply = "apply";

    /// <summary>Feature, coverage, or metadata import phase.</summary>
    public const string Import = "import";
}

/// <summary>
/// Stable source-family identifiers used by migration fixture evidence.
/// </summary>
public static class MigrationCostPerformanceSourceFamilies
{
    /// <summary>ArcGIS GeoServices REST source family.</summary>
    public const string ArcGisGeoServicesRest = "arcgis-geoservices-rest";

    /// <summary>GeoServer REST source family.</summary>
    public const string GeoServerRest = "geoserver-rest";

    /// <summary>OGC Features or WFS source family.</summary>
    public const string OgcFeature = "ogc-feature";

    /// <summary>OGC map or tile metadata source family.</summary>
    public const string OgcMapTileMetadata = "ogc-map-tile-metadata";

    /// <summary>Coverage or raster source family.</summary>
    public const string Coverage = "coverage";
}

/// <summary>
/// Stable fixture-size identifiers used by migration cost and performance evidence.
/// </summary>
public static class MigrationCostPerformanceFixtureSizes
{
    /// <summary>Small deterministic migration fixture.</summary>
    public const string Small = "small";

    /// <summary>Medium deterministic migration fixture.</summary>
    public const string Medium = "medium";

    /// <summary>Large deterministic migration fixture.</summary>
    public const string Large = "large";
}

/// <summary>
/// Stable recovery scenario identifiers used by migration cost and performance evidence.
/// </summary>
public static class MigrationCostPerformanceRecoveryScenarios
{
    /// <summary>Recovery after a simulated source failure.</summary>
    public const string SourceFailure = "source-failure";

    /// <summary>Recovery after job cancellation.</summary>
    public const string JobCancellation = "job-cancellation";

    /// <summary>Recovery after a transient network failure.</summary>
    public const string TransientNetworkError = "transient-network-error";

    /// <summary>Idempotent replay of a repeated apply attempt.</summary>
    public const string RepeatedApplyAttempt = "repeated-apply-attempt";
}

/// <summary>
/// Website-linkable artifact containing release-safe migration cost and performance evidence.
/// </summary>
public sealed record MigrationCostPerformanceEvidenceArtifact
{
    /// <summary>
    /// Stable artifact kind identifier.
    /// </summary>
    public string ArtifactKind { get; init; } = "honua.migration.cost-performance-evidence";

    /// <summary>
    /// Artifact schema version.
    /// </summary>
    public string ArtifactVersion { get; init; } = "1.0";

    /// <summary>
    /// Source kind identifier such as <c>geoserver-rest</c>, <c>ogc-wfs</c>, or <c>arcgis-geoservices-rest</c>.
    /// </summary>
    public required string SourceKind { get; init; }

    /// <summary>
    /// Safe source summary with private URLs and credentials omitted.
    /// </summary>
    public required MigrationCostPerformanceSourceSummary Source { get; init; }

    /// <summary>
    /// Human-readable scope for the measured run.
    /// </summary>
    public required string MeasurementScope { get; init; }

    /// <summary>
    /// Optional deterministic run identifier supplied by the harness.
    /// </summary>
    public string? RunId { get; init; }

    /// <summary>
    /// Representative deterministic fixture profile used by the measured run.
    /// </summary>
    public required MigrationCostPerformanceFixtureProfile FixtureProfile { get; init; }

    /// <summary>
    /// Aggregate classification across all measured phases.
    /// </summary>
    public required string OverallClassification { get; init; }

    /// <summary>
    /// Reviewer-facing summary of the classification outcome.
    /// </summary>
    public required string Summary { get; init; }

    /// <summary>
    /// Threshold profile used to classify the measurements.
    /// </summary>
    public required MigrationCostPerformanceThresholds Thresholds { get; init; }

    /// <summary>
    /// Aggregate metrics across every phase.
    /// </summary>
    public required MigrationCostPerformanceMetrics Totals { get; init; }

    /// <summary>
    /// Phase-level measurements in deterministic phase order.
    /// </summary>
    public MigrationCostPerformancePhaseEvidence[] Phases { get; init; } = [];

    /// <summary>
    /// Recovery and idempotency evidence for simulated migration failure modes.
    /// </summary>
    public MigrationCostPerformanceRecoveryEvidence[] Recovery { get; init; } = [];

    /// <summary>
    /// Privacy posture proving the artifact intentionally excludes source URLs, credentials, and source data.
    /// </summary>
    public required MigrationCostPerformancePrivacySummary Privacy { get; init; }
}

/// <summary>
/// Secret-safe source identity used in migration cost and performance evidence.
/// </summary>
public sealed record MigrationCostPerformanceSourceSummary
{
    /// <summary>
    /// Human-readable source label after URL and credential redaction.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Source product name when known.
    /// </summary>
    public string? Product { get; init; }

    /// <summary>
    /// Source product version when known.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Source service type or protocol subtype when known.
    /// </summary>
    public string? ServiceType { get; init; }
}

/// <summary>
/// Phase-level cost and performance evidence.
/// </summary>
public sealed record MigrationCostPerformancePhaseEvidence
{
    /// <summary>
    /// Stable phase identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Migration phase, such as <c>scan</c>, <c>manifest</c>, <c>apply</c>, or <c>import</c>.
    /// </summary>
    public required string Phase { get; init; }

    /// <summary>
    /// Phase classification after applying threshold signals.
    /// </summary>
    public required string Classification { get; init; }

    /// <summary>
    /// Aggregated phase metrics.
    /// </summary>
    public required MigrationCostPerformanceMetrics Metrics { get; init; }

    /// <summary>
    /// Metric-level threshold signals in deterministic order.
    /// </summary>
    public MigrationCostPerformanceSignal[] Signals { get; init; } = [];
}

/// <summary>
/// Deterministic fixture profile for a measured migration run.
/// </summary>
public sealed record MigrationCostPerformanceFixtureProfile
{
    /// <summary>
    /// Source family represented by the fixture.
    /// </summary>
    public required string SourceFamily { get; init; }

    /// <summary>
    /// Fixture size classification: <c>small</c>, <c>medium</c>, or <c>large</c>.
    /// </summary>
    public required string Size { get; init; }

    /// <summary>
    /// Expected source resources represented by the fixture.
    /// </summary>
    public int? ExpectedResourceCount { get; init; }

    /// <summary>
    /// Expected features represented by the fixture.
    /// </summary>
    public long? ExpectedFeatureCount { get; init; }

    /// <summary>
    /// Expected coverages or raster assets represented by the fixture.
    /// </summary>
    public long? ExpectedCoverageCount { get; init; }

    /// <summary>
    /// Secret-safe fixture description.
    /// </summary>
    public string? Description { get; init; }
}

/// <summary>
/// Duration, volume, retry, resume, resource, and review metrics for one migration scope.
/// </summary>
public sealed record MigrationCostPerformanceMetrics
{
    /// <summary>
    /// Measured wall-clock duration in milliseconds.
    /// </summary>
    public long? DurationMilliseconds { get; init; }

    /// <summary>
    /// Source requests issued during the measured scope.
    /// </summary>
    public long? SourceRequestCount { get; init; }

    /// <summary>
    /// Bytes read from source systems or staging artifacts.
    /// </summary>
    public long? BytesRead { get; init; }

    /// <summary>
    /// Bytes written to Honua-owned stores or output artifacts.
    /// </summary>
    public long? BytesWritten { get; init; }

    /// <summary>
    /// Retry attempts observed during the measured scope.
    /// </summary>
    public int? RetryCount { get; init; }

    /// <summary>
    /// Resume attempts observed during the measured scope.
    /// </summary>
    public int? ResumeCount { get; init; }

    /// <summary>
    /// CPU time in milliseconds when the harness can measure it.
    /// </summary>
    public long? CpuMilliseconds { get; init; }

    /// <summary>
    /// Peak resident memory in bytes when the harness can measure it.
    /// </summary>
    public long? PeakMemoryBytes { get; init; }

    /// <summary>
    /// Database growth in bytes caused by the measured scope.
    /// </summary>
    public long? DatabaseGrowthBytes { get; init; }

    /// <summary>
    /// Evidence artifact size in bytes.
    /// </summary>
    public long? ArtifactBytes { get; init; }

    /// <summary>
    /// Source resources processed by the measured scope.
    /// </summary>
    public long? ResourceCount { get; init; }

    /// <summary>
    /// Features processed by the measured scope.
    /// </summary>
    public long? FeatureCount { get; init; }

    /// <summary>
    /// Coverages or raster assets processed by the measured scope.
    /// </summary>
    public long? CoverageCount { get; init; }

    /// <summary>
    /// Resources processed per second, computed from resource count and duration when omitted.
    /// </summary>
    public double? ResourceThroughputPerSecond { get; init; }

    /// <summary>
    /// Features processed per second, computed from feature count and duration when omitted.
    /// </summary>
    public double? FeatureThroughputPerSecond { get; init; }

    /// <summary>
    /// Coverages processed per second, computed from coverage count and duration when omitted.
    /// </summary>
    public double? CoverageThroughputPerSecond { get; init; }

    /// <summary>
    /// Items requiring manual review.
    /// </summary>
    public int? ManualReviewCount { get; init; }

    /// <summary>
    /// Total candidate items used as the denominator for manual-review ratio.
    /// </summary>
    public int? CandidateItemCount { get; init; }

    /// <summary>
    /// Manual-review ratio, computed from counts when omitted.
    /// </summary>
    public double? ManualReviewRatio { get; init; }
}

/// <summary>
/// Raw phase measurement supplied by a scanner, importer, or test harness.
/// </summary>
public sealed record MigrationCostPerformancePhaseMeasurement
{
    /// <summary>
    /// Migration phase identifier. Unknown values are retained after safe normalization.
    /// </summary>
    public required string Phase { get; init; }

    /// <summary>
    /// Measured metrics for the phase.
    /// </summary>
    public MigrationCostPerformanceMetrics Metrics { get; init; } = new();
}

/// <summary>
/// Input used to build deterministic migration cost and performance evidence.
/// </summary>
public sealed record MigrationCostPerformanceEvidenceInput
{
    /// <summary>
    /// Human-readable measurement scope, such as <c>nightly small fixture</c>.
    /// </summary>
    public required string MeasurementScope { get; init; }

    /// <summary>
    /// Optional deterministic run identifier supplied by the harness.
    /// </summary>
    public string? RunId { get; init; }

    /// <summary>
    /// Representative deterministic fixture profile for the measured run.
    /// </summary>
    public MigrationCostPerformanceFixtureProfile? FixtureProfile { get; init; }

    /// <summary>
    /// Phase measurements to aggregate into the artifact.
    /// </summary>
    public MigrationCostPerformancePhaseMeasurement[] PhaseMeasurements { get; init; } = [];

    /// <summary>
    /// Recovery and idempotency measurements for simulated failure scenarios.
    /// </summary>
    public MigrationCostPerformanceRecoveryMeasurement[] RecoveryMeasurements { get; init; } = [];
}

/// <summary>
/// Raw recovery measurement supplied by a migration acceptance harness.
/// </summary>
public sealed record MigrationCostPerformanceRecoveryMeasurement
{
    /// <summary>
    /// Recovery scenario identifier.
    /// </summary>
    public required string Scenario { get; init; }

    /// <summary>
    /// Whether the migration recovered successfully.
    /// </summary>
    public bool? Recovered { get; init; }

    /// <summary>
    /// Whether resume behavior was observed.
    /// </summary>
    public bool? ResumeObserved { get; init; }

    /// <summary>
    /// Whether repeated replay was idempotent.
    /// </summary>
    public bool? IdempotentReplay { get; init; }

    /// <summary>
    /// Attempts observed while proving the scenario.
    /// </summary>
    public int? AttemptCount { get; init; }
}

/// <summary>
/// Recovery and idempotency evidence for one simulated migration failure mode.
/// </summary>
public sealed record MigrationCostPerformanceRecoveryEvidence
{
    /// <summary>
    /// Recovery scenario identifier.
    /// </summary>
    public required string Scenario { get; init; }

    /// <summary>
    /// Scenario classification.
    /// </summary>
    public required string Classification { get; init; }

    /// <summary>
    /// Whether the migration recovered successfully.
    /// </summary>
    public bool? Recovered { get; init; }

    /// <summary>
    /// Whether resume behavior was observed.
    /// </summary>
    public bool? ResumeObserved { get; init; }

    /// <summary>
    /// Whether repeated replay was idempotent.
    /// </summary>
    public bool? IdempotentReplay { get; init; }

    /// <summary>
    /// Attempts observed while proving the scenario.
    /// </summary>
    public int? AttemptCount { get; init; }

    /// <summary>
    /// Short deterministic explanation.
    /// </summary>
    public required string Summary { get; init; }
}

/// <summary>
/// Upper-bound thresholds used to classify migration cost and performance evidence.
/// </summary>
public sealed record MigrationCostPerformanceThresholds
{
    /// <summary>
    /// Baseline profile for release-gated migration evidence.
    /// </summary>
    public static MigrationCostPerformanceThresholds ReleaseEvidenceBaseline { get; } = new()
    {
        ProfileName = "release-evidence-baseline",
        DurationWarnMilliseconds = 300_000,
        DurationFailMilliseconds = 900_000,
        SourceRequestWarnCount = 2_000,
        SourceRequestFailCount = 5_000,
        BytesReadWarn = 1_073_741_824,
        BytesReadFail = 5_368_709_120,
        BytesWrittenWarn = 1_073_741_824,
        BytesWrittenFail = 5_368_709_120,
        RetryWarnCount = 1,
        RetryFailCount = 10,
        ResumeWarnCount = 1,
        ResumeFailCount = 5,
        CpuWarnMilliseconds = 300_000,
        CpuFailMilliseconds = 900_000,
        PeakMemoryWarnBytes = 536_870_912,
        PeakMemoryFailBytes = 2_147_483_648,
        DatabaseGrowthWarnBytes = 1_073_741_824,
        DatabaseGrowthFailBytes = 5_368_709_120,
        ArtifactBytesWarn = 1_048_576,
        ArtifactBytesFail = 5_242_880,
        ResourceThroughputWarnPerSecond = 1,
        ResourceThroughputFailPerSecond = 0.1,
        FeatureThroughputWarnPerSecond = 50,
        FeatureThroughputFailPerSecond = 10,
        CoverageThroughputWarnPerSecond = 0.1,
        CoverageThroughputFailPerSecond = 0.01,
        ManualReviewRatioWarn = 0.10,
        ManualReviewRatioFail = 0.25
    };

    /// <summary>
    /// Stable threshold profile name.
    /// </summary>
    public string ProfileName { get; init; } = "custom";

    /// <summary>
    /// Warning threshold for duration in milliseconds.
    /// </summary>
    public long? DurationWarnMilliseconds { get; init; }

    /// <summary>
    /// Failure threshold for duration in milliseconds.
    /// </summary>
    public long? DurationFailMilliseconds { get; init; }

    /// <summary>
    /// Warning threshold for source request count.
    /// </summary>
    public long? SourceRequestWarnCount { get; init; }

    /// <summary>
    /// Failure threshold for source request count.
    /// </summary>
    public long? SourceRequestFailCount { get; init; }

    /// <summary>
    /// Warning threshold for bytes read.
    /// </summary>
    public long? BytesReadWarn { get; init; }

    /// <summary>
    /// Failure threshold for bytes read.
    /// </summary>
    public long? BytesReadFail { get; init; }

    /// <summary>
    /// Warning threshold for bytes written.
    /// </summary>
    public long? BytesWrittenWarn { get; init; }

    /// <summary>
    /// Failure threshold for bytes written.
    /// </summary>
    public long? BytesWrittenFail { get; init; }

    /// <summary>
    /// Warning threshold for retry count.
    /// </summary>
    public int? RetryWarnCount { get; init; }

    /// <summary>
    /// Failure threshold for retry count.
    /// </summary>
    public int? RetryFailCount { get; init; }

    /// <summary>
    /// Warning threshold for resume count.
    /// </summary>
    public int? ResumeWarnCount { get; init; }

    /// <summary>
    /// Failure threshold for resume count.
    /// </summary>
    public int? ResumeFailCount { get; init; }

    /// <summary>
    /// Warning threshold for CPU time in milliseconds.
    /// </summary>
    public long? CpuWarnMilliseconds { get; init; }

    /// <summary>
    /// Failure threshold for CPU time in milliseconds.
    /// </summary>
    public long? CpuFailMilliseconds { get; init; }

    /// <summary>
    /// Warning threshold for peak resident memory in bytes.
    /// </summary>
    public long? PeakMemoryWarnBytes { get; init; }

    /// <summary>
    /// Failure threshold for peak resident memory in bytes.
    /// </summary>
    public long? PeakMemoryFailBytes { get; init; }

    /// <summary>
    /// Warning threshold for database growth in bytes.
    /// </summary>
    public long? DatabaseGrowthWarnBytes { get; init; }

    /// <summary>
    /// Failure threshold for database growth in bytes.
    /// </summary>
    public long? DatabaseGrowthFailBytes { get; init; }

    /// <summary>
    /// Warning threshold for evidence artifact size in bytes.
    /// </summary>
    public long? ArtifactBytesWarn { get; init; }

    /// <summary>
    /// Failure threshold for evidence artifact size in bytes.
    /// </summary>
    public long? ArtifactBytesFail { get; init; }

    /// <summary>
    /// Warning lower bound for resource throughput per second.
    /// </summary>
    public double? ResourceThroughputWarnPerSecond { get; init; }

    /// <summary>
    /// Failure lower bound for resource throughput per second.
    /// </summary>
    public double? ResourceThroughputFailPerSecond { get; init; }

    /// <summary>
    /// Warning lower bound for feature throughput per second.
    /// </summary>
    public double? FeatureThroughputWarnPerSecond { get; init; }

    /// <summary>
    /// Failure lower bound for feature throughput per second.
    /// </summary>
    public double? FeatureThroughputFailPerSecond { get; init; }

    /// <summary>
    /// Warning lower bound for coverage throughput per second.
    /// </summary>
    public double? CoverageThroughputWarnPerSecond { get; init; }

    /// <summary>
    /// Failure lower bound for coverage throughput per second.
    /// </summary>
    public double? CoverageThroughputFailPerSecond { get; init; }

    /// <summary>
    /// Warning threshold for manual-review ratio.
    /// </summary>
    public double? ManualReviewRatioWarn { get; init; }

    /// <summary>
    /// Failure threshold for manual-review ratio.
    /// </summary>
    public double? ManualReviewRatioFail { get; init; }
}

/// <summary>
/// Metric-level cost and performance threshold result.
/// </summary>
public sealed record MigrationCostPerformanceSignal
{
    /// <summary>
    /// Stable metric identifier.
    /// </summary>
    public required string Metric { get; init; }

    /// <summary>
    /// Metric classification.
    /// </summary>
    public required string Classification { get; init; }

    /// <summary>
    /// Observed metric value when available.
    /// </summary>
    public double? Observed { get; init; }

    /// <summary>
    /// Metric unit, such as <c>milliseconds</c>, <c>bytes</c>, <c>count</c>, or <c>ratio</c>.
    /// </summary>
    public required string Unit { get; init; }

    /// <summary>
    /// Warning threshold used by the classifier.
    /// </summary>
    public double? WarnThreshold { get; init; }

    /// <summary>
    /// Failure threshold used by the classifier.
    /// </summary>
    public double? FailThreshold { get; init; }

    /// <summary>
    /// Short deterministic explanation.
    /// </summary>
    public required string Summary { get; init; }
}

/// <summary>
/// Privacy posture for migration cost and performance evidence.
/// </summary>
public sealed record MigrationCostPerformancePrivacySummary
{
    /// <summary>
    /// Whether source URLs are present in the artifact.
    /// </summary>
    public bool SourceUrlsIncluded { get; init; }

    /// <summary>
    /// Whether credential values are present in the artifact.
    /// </summary>
    public bool CredentialValuesIncluded { get; init; }

    /// <summary>
    /// Whether source feature, coverage, or customer data is present in the artifact.
    /// </summary>
    public bool SourceDataIncluded { get; init; }

    /// <summary>
    /// Fields intentionally omitted from the artifact.
    /// </summary>
    public string[] OmittedFields { get; init; } = [];
}
