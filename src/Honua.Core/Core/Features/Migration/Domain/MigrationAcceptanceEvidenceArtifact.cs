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

public sealed record MigrationAcceptanceEvidenceArtifact
{
    public string ArtifactKind { get; init; } = "honua.migration.acceptance-evidence-suite";

    public string ArtifactVersion { get; init; } = "1.0";

    public required string RunId { get; init; }

    public required MigrationAcceptanceEvidenceSummary Summary { get; init; }

    public required MigrationAcceptanceCostEvidenceSummary CostEvidence { get; init; }

    public MigrationAcceptanceEvidenceEntry[] Entries { get; init; } = [];

    public MigrationAcceptanceEvidenceGap[] BlockingGaps { get; init; } = [];
}

public sealed record MigrationAcceptanceEvidenceSummary
{
    public required string OverallState { get; init; }

    public int SourceCount { get; init; }

    public int PassingSourceCount { get; init; }

    public int FailingSourceCount { get; init; }

    public int UnknownSourceCount { get; init; }

    public int AutomatedSourceCount { get; init; }

    public int ManualReviewSourceCount { get; init; }

    public int UnsupportedSourceCount { get; init; }

    public string[] RequiredSourceKinds { get; init; } = [];

    public string[] CoveredSourceKinds { get; init; } = [];
}

public sealed record MigrationAcceptanceCostEvidenceSummary
{
    public required string State { get; init; }

    public int MeasuredSourceCount { get; init; }

    public int MissingSourceCount { get; init; }

    public long? DurationMilliseconds { get; init; }

    public long? ScanDurationMilliseconds { get; init; }

    public long? ApplyDurationMilliseconds { get; init; }

    public double? FeatureThroughputPerSecond { get; init; }

    public int? SourceRequestCount { get; init; }

    public int? RetryCount { get; init; }

    public long? BytesRead { get; init; }

    public long? BytesWritten { get; init; }

    public long? ArtifactSizeBytes { get; init; }

    public double? ManualReviewRatio { get; init; }

    public MigrationAcceptanceCostEvidenceFinding[] Findings { get; init; } = [];
}

public sealed record MigrationAcceptanceEvidenceEntry
{
    public required string Id { get; init; }

    public required string SourceKind { get; init; }

    public required MigrationSourceIdentity Source { get; init; }

    public required string State { get; init; }

    public required string AutomationLevel { get; init; }

    public MigrationAcceptanceEvidenceStage[] Stages { get; init; } = [];

    public int ManualReviewCount { get; init; }

    public int UnsupportedCount { get; init; }

    public bool ManifestAvailable { get; init; }

    public required string InventoryArtifactKind { get; init; }

    public required string ManifestArtifactKind { get; init; }

    public required string ParityEvidenceArtifactKind { get; init; }

    public string[] EvidenceReferences { get; init; } = [];

    public MigrationAcceptanceCostEvidenceEntry? CostEvidence { get; init; }

    public string[] Notes { get; init; } = [];
}

public sealed record MigrationAcceptanceCostEvidenceEntry
{
    public required string State { get; init; }

    public required string Summary { get; init; }

    public required string MeasurementScope { get; init; }

    public long? DurationMilliseconds { get; init; }

    public long? ScanDurationMilliseconds { get; init; }

    public long? ApplyDurationMilliseconds { get; init; }

    public double? FeatureThroughputPerSecond { get; init; }

    public double? ResourceThroughputPerSecond { get; init; }

    public int? SourceRequestCount { get; init; }

    public int? RetryCount { get; init; }

    public long? BytesRead { get; init; }

    public long? BytesWritten { get; init; }

    public long? ArtifactSizeBytes { get; init; }

    public double? ManualReviewRatio { get; init; }

    public MigrationAcceptanceCostOperationEvidence[] Operations { get; init; } = [];

    public string[] EvidenceReferences { get; init; } = [];

    public MigrationAcceptanceCostEvidenceFinding[] Findings { get; init; } = [];
}

public sealed record MigrationAcceptanceCostOperationEvidence
{
    public required string Id { get; init; }

    public required string Stage { get; init; }

    public required string State { get; init; }

    public long? DurationMilliseconds { get; init; }

    public double? FeatureThroughputPerSecond { get; init; }

    public int? SourceRequestCount { get; init; }

    public int? RetryCount { get; init; }

    public long? BytesRead { get; init; }

    public long? BytesWritten { get; init; }

    public long? ArtifactSizeBytes { get; init; }

    public string[] EvidenceReferences { get; init; } = [];
}

public sealed record MigrationAcceptanceCostEvidenceFinding
{
    public required string Id { get; init; }

    public required string State { get; init; }

    public required string Summary { get; init; }

    public string[] Remediation { get; init; } = [];
}

public sealed record MigrationAcceptanceEvidenceStage
{
    public required string Id { get; init; }

    public required string State { get; init; }

    public required string Summary { get; init; }

    public string[] ArtifactKinds { get; init; } = [];

    public string[] EvidenceReferences { get; init; } = [];

    public string[] Notes { get; init; } = [];
}

public sealed record MigrationAcceptanceEvidenceGap
{
    public required string Id { get; init; }

    public required string SourceKind { get; init; }

    public required string State { get; init; }

    public required string Summary { get; init; }

    public string[] Remediation { get; init; } = [];
}

public static class MigrationAutomationLevels
{
    public const string Automated = "automated";

    public const string Assisted = "assisted";

    public const string ManualReview = "manual-review";

    public const string Unsupported = "unsupported";
}

public static class MigrationAcceptanceStageIds
{
    public const string Scan = "scan";

    public const string Manifest = "manifest";

    public const string ApplyOrDryRun = "apply-dry-run";

    public const string Publish = "publish";

    public const string Parity = "parity";

    public const string Readiness = "readiness";
}

public static class MigrationCostEvidenceStates
{
    public const string Pass = "pass";

    public const string Warn = "warn";

    public const string Fail = "fail";

    public const string Unknown = "unknown";
}
