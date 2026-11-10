// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
namespace Honua.Core.Features.Migration.Domain;

public static class MigrationCostPerformanceClassifications
{
    public const string Pass = "pass";

    public const string Warn = "warn";

    public const string Fail = "fail";
}

public static class MigrationCostPerformancePhases
{
    public const string Scan = "scan";

    public const string Manifest = "manifest";

    public const string Apply = "apply";

    public const string Import = "import";
}

public static class MigrationCostPerformanceSourceFamilies
{
    public const string GeoServicesRest = MigrationSourceKinds.GeoServicesRest;

    public const string GeoServerRest = "geoserver-rest";

    public const string OgcFeature = "ogc-feature";

    public const string OgcMapTileMetadata = "ogc-map-tile-metadata";

    public const string Coverage = "coverage";
}

public static class MigrationCostPerformanceFixtureSizes
{
    public const string Small = "small";

    public const string Medium = "medium";

    public const string Large = "large";
}

public static class MigrationCostPerformanceRecoveryScenarios
{
    public const string SourceFailure = "source-failure";

    public const string JobCancellation = "job-cancellation";

    public const string TransientNetworkError = "transient-network-error";

    public const string RepeatedApplyAttempt = "repeated-apply-attempt";
}

public sealed record MigrationCostPerformanceEvidenceArtifact
{
    public string ArtifactKind { get; init; } = "honua.migration.cost-performance-evidence";

    public string ArtifactVersion { get; init; } = "1.0";

    public required string SourceKind { get; init; }

    public required MigrationCostPerformanceSourceSummary Source { get; init; }

    public required string MeasurementScope { get; init; }

    public string? RunId { get; init; }

    public required MigrationCostPerformanceFixtureProfile FixtureProfile { get; init; }

    public required string OverallClassification { get; init; }

    public required string Summary { get; init; }

    public required MigrationCostPerformanceThresholds Thresholds { get; init; }

    public required MigrationCostPerformanceMetrics Totals { get; init; }

    public MigrationCostPerformancePhaseEvidence[] Phases { get; init; } = [];

    public MigrationCostPerformanceRecoveryEvidence[] Recovery { get; init; } = [];

    public required MigrationCostPerformancePrivacySummary Privacy { get; init; }
}

public sealed record MigrationCostPerformanceSourceSummary
{
    public required string DisplayName { get; init; }

    public string? Product { get; init; }

    public string? Version { get; init; }

    public string? ServiceType { get; init; }
}

public sealed record MigrationCostPerformancePhaseEvidence
{
    public required string Id { get; init; }

    public required string Phase { get; init; }

    public required string Classification { get; init; }

    public required MigrationCostPerformanceMetrics Metrics { get; init; }

    public MigrationCostPerformanceSignal[] Signals { get; init; } = [];
}

public sealed record MigrationCostPerformanceFixtureProfile
{
    public required string SourceFamily { get; init; }

    public required string Size { get; init; }

    public int? ExpectedResourceCount { get; init; }

    public long? ExpectedFeatureCount { get; init; }

    public long? ExpectedCoverageCount { get; init; }

    public string? Description { get; init; }
}

public sealed record MigrationCostPerformanceMetrics
{
    public long? DurationMilliseconds { get; init; }

    public long? SourceRequestCount { get; init; }

    public long? BytesRead { get; init; }

    public long? BytesWritten { get; init; }

    public int? RetryCount { get; init; }

    public int? ResumeCount { get; init; }

    public long? CpuMilliseconds { get; init; }

    public long? PeakMemoryBytes { get; init; }

    public long? DatabaseGrowthBytes { get; init; }

    public long? ArtifactBytes { get; init; }

    public long? ResourceCount { get; init; }

    public long? FeatureCount { get; init; }

    public long? CoverageCount { get; init; }

    public double? ResourceThroughputPerSecond { get; init; }

    public double? FeatureThroughputPerSecond { get; init; }

    public double? CoverageThroughputPerSecond { get; init; }

    public int? ManualReviewCount { get; init; }

    public int? CandidateItemCount { get; init; }

    public double? ManualReviewRatio { get; init; }
}

public sealed record MigrationCostPerformancePhaseMeasurement
{
    public required string Phase { get; init; }

    public MigrationCostPerformanceMetrics Metrics { get; init; } = new();
}

public sealed record MigrationCostPerformanceEvidenceInput
{
    public required string MeasurementScope { get; init; }

    public string? RunId { get; init; }

    public MigrationCostPerformanceFixtureProfile? FixtureProfile { get; init; }

    public MigrationCostPerformancePhaseMeasurement[] PhaseMeasurements { get; init; } = [];

    public MigrationCostPerformanceRecoveryMeasurement[] RecoveryMeasurements { get; init; } = [];
}

public sealed record MigrationCostPerformanceRecoveryMeasurement
{
    public required string Scenario { get; init; }

    public bool? Recovered { get; init; }

    public bool? ResumeObserved { get; init; }

    public bool? IdempotentReplay { get; init; }

    public int? AttemptCount { get; init; }
}

public sealed record MigrationCostPerformanceRecoveryEvidence
{
    public required string Scenario { get; init; }

    public required string Classification { get; init; }

    public bool? Recovered { get; init; }

    public bool? ResumeObserved { get; init; }

    public bool? IdempotentReplay { get; init; }

    public int? AttemptCount { get; init; }

    public required string Summary { get; init; }
}

public sealed record MigrationCostPerformanceThresholds
{
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

    public string ProfileName { get; init; } = "custom";

    public long? DurationWarnMilliseconds { get; init; }

    public long? DurationFailMilliseconds { get; init; }

    public long? SourceRequestWarnCount { get; init; }

    public long? SourceRequestFailCount { get; init; }

    public long? BytesReadWarn { get; init; }

    public long? BytesReadFail { get; init; }

    public long? BytesWrittenWarn { get; init; }

    public long? BytesWrittenFail { get; init; }

    public int? RetryWarnCount { get; init; }

    public int? RetryFailCount { get; init; }

    public int? ResumeWarnCount { get; init; }

    public int? ResumeFailCount { get; init; }

    public long? CpuWarnMilliseconds { get; init; }

    public long? CpuFailMilliseconds { get; init; }

    public long? PeakMemoryWarnBytes { get; init; }

    public long? PeakMemoryFailBytes { get; init; }

    public long? DatabaseGrowthWarnBytes { get; init; }

    public long? DatabaseGrowthFailBytes { get; init; }

    public long? ArtifactBytesWarn { get; init; }

    public long? ArtifactBytesFail { get; init; }

    public double? ResourceThroughputWarnPerSecond { get; init; }

    public double? ResourceThroughputFailPerSecond { get; init; }

    public double? FeatureThroughputWarnPerSecond { get; init; }

    public double? FeatureThroughputFailPerSecond { get; init; }

    public double? CoverageThroughputWarnPerSecond { get; init; }

    public double? CoverageThroughputFailPerSecond { get; init; }

    public double? ManualReviewRatioWarn { get; init; }

    public double? ManualReviewRatioFail { get; init; }
}

public sealed record MigrationCostPerformanceSignal
{
    public required string Metric { get; init; }

    public required string Classification { get; init; }

    public double? Observed { get; init; }

    public required string Unit { get; init; }

    public double? WarnThreshold { get; init; }

    public double? FailThreshold { get; init; }

    public required string Summary { get; init; }
}

public sealed record MigrationCostPerformancePrivacySummary
{
    public bool SourceUrlsIncluded { get; init; }

    public bool CredentialValuesIncluded { get; init; }

    public bool SourceDataIncluded { get; init; }

    public string[] OmittedFields { get; init; } = [];
}
