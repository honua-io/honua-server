// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.FileImport.Services.FileGdb;
namespace Honua.Core.Features.Migration.Domain;

/// <summary>
/// Deterministic acceptance-suite index for automated migration evidence.
/// </summary>
public sealed record MigrationAcceptanceEvidenceArtifact
{
    /// <summary>
    /// Stable artifact kind identifier.
    /// </summary>
    public string ArtifactKind { get; init; } = "honua.migration.acceptance-evidence-suite";

    /// <summary>
    /// Artifact schema version.
    /// </summary>
    public string ArtifactVersion { get; init; } = "1.0";

    /// <summary>
    /// Stable run identifier supplied by the workflow or release gate.
    /// </summary>
    public required string RunId { get; init; }

    /// <summary>
    /// Aggregate suite state and coverage counts.
    /// </summary>
    public required MigrationAcceptanceEvidenceSummary Summary { get; init; }

    /// <summary>
    /// Aggregate cost and performance evidence across measured source entries.
    /// </summary>
    public required MigrationAcceptanceCostEvidenceSummary CostEvidence { get; init; }

    /// <summary>
    /// Per-source evidence entries in deterministic order.
    /// </summary>
    public MigrationAcceptanceEvidenceEntry[] Entries { get; init; } = [];

    /// <summary>
    /// Blocking evidence gaps in deterministic order.
    /// </summary>
    public MigrationAcceptanceEvidenceGap[] BlockingGaps { get; init; } = [];
}

/// <summary>
/// Aggregate counts for a migration acceptance evidence suite.
/// </summary>
public sealed record MigrationAcceptanceEvidenceSummary
{
    /// <summary>
    /// Overall suite state: <c>pass</c>, <c>fail</c>, or <c>unknown</c>.
    /// </summary>
    public required string OverallState { get; init; }

    /// <summary>
    /// Number of source evidence entries.
    /// </summary>
    public int SourceCount { get; init; }

    /// <summary>
    /// Number of source entries whose acceptance stage state passed.
    /// </summary>
    public int PassingSourceCount { get; init; }

    /// <summary>
    /// Number of source entries whose acceptance stage state failed.
    /// </summary>
    public int FailingSourceCount { get; init; }

    /// <summary>
    /// Number of source entries still requiring additional evidence or review.
    /// </summary>
    public int UnknownSourceCount { get; init; }

    /// <summary>
    /// Number of entries classified as automated.
    /// </summary>
    public int AutomatedSourceCount { get; init; }

    /// <summary>
    /// Number of entries classified as assisted or manual review.
    /// </summary>
    public int ManualReviewSourceCount { get; init; }

    /// <summary>
    /// Number of entries classified as unsupported.
    /// </summary>
    public int UnsupportedSourceCount { get; init; }

    /// <summary>
    /// Source kind requirements evaluated by the suite gate.
    /// </summary>
    public string[] RequiredSourceKinds { get; init; } = [];

    /// <summary>
    /// Source kinds covered by this evidence suite.
    /// </summary>
    public string[] CoveredSourceKinds { get; init; } = [];
}

/// <summary>
/// Aggregate cost and performance evidence for a migration acceptance suite.
/// </summary>
public sealed record MigrationAcceptanceCostEvidenceSummary
{
    /// <summary>
    /// Cost evidence classification: <c>pass</c>, <c>warn</c>, <c>fail</c>, or <c>unknown</c>.
    /// </summary>
    public required string State { get; init; }

    /// <summary>
    /// Number of source entries with cost evidence attached.
    /// </summary>
    public int MeasuredSourceCount { get; init; }

    /// <summary>
    /// Number of source entries missing cost evidence.
    /// </summary>
    public int MissingSourceCount { get; init; }

    /// <summary>
    /// Total measured duration across entries in milliseconds.
    /// </summary>
    public long? DurationMilliseconds { get; init; }

    /// <summary>
    /// Total scan duration across entries in milliseconds.
    /// </summary>
    public long? ScanDurationMilliseconds { get; init; }

    /// <summary>
    /// Total apply, import, or dry-run duration across entries in milliseconds.
    /// </summary>
    public long? ApplyDurationMilliseconds { get; init; }

    /// <summary>
    /// Aggregate feature throughput across entries in features per second.
    /// </summary>
    public double? FeatureThroughputPerSecond { get; init; }

    /// <summary>
    /// Total source-system requests observed across entries.
    /// </summary>
    public int? SourceRequestCount { get; init; }

    /// <summary>
    /// Total retry attempts observed across entries.
    /// </summary>
    public int? RetryCount { get; init; }

    /// <summary>
    /// Total bytes read from source systems or staged inputs.
    /// </summary>
    public long? BytesRead { get; init; }

    /// <summary>
    /// Total bytes written to Honua-owned stores or output artifacts.
    /// </summary>
    public long? BytesWritten { get; init; }

    /// <summary>
    /// Total emitted evidence artifact size in bytes.
    /// </summary>
    public long? ArtifactSizeBytes { get; init; }

    /// <summary>
    /// Aggregate manual-review ratio across measured entries.
    /// </summary>
    public double? ManualReviewRatio { get; init; }

    /// <summary>
    /// Findings that explain warning, failure, or unknown classifications.
    /// </summary>
    public MigrationAcceptanceCostEvidenceFinding[] Findings { get; init; } = [];
}

/// <summary>
/// Per-source migration acceptance evidence entry.
/// </summary>
public sealed record MigrationAcceptanceEvidenceEntry
{
    /// <summary>
    /// Stable source evidence entry identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Source kind identifier, such as <c>arcgis-geoservices-rest</c> or <c>geoserver-rest</c>.
    /// </summary>
    public required string SourceKind { get; init; }

    /// <summary>
    /// Source identity copied from the inventory artifact.
    /// </summary>
    public required MigrationSourceIdentity Source { get; init; }

    /// <summary>
    /// Entry state aggregated from canonical acceptance stages.
    /// </summary>
    public required string State { get; init; }

    /// <summary>
    /// Automation classification for the migration path.
    /// </summary>
    public required string AutomationLevel { get; init; }

    /// <summary>
    /// Canonical scan, manifest, apply or dry-run, publish, parity, and readiness stages.
    /// </summary>
    public MigrationAcceptanceEvidenceStage[] Stages { get; init; } = [];

    /// <summary>
    /// Number of manifest items requiring operator review.
    /// </summary>
    public int ManualReviewCount { get; init; }

    /// <summary>
    /// Number of manifest items unsupported by deterministic migration.
    /// </summary>
    public int UnsupportedCount { get; init; }

    /// <summary>
    /// Whether a manifest artifact was available or generated for this entry.
    /// </summary>
    public bool ManifestAvailable { get; init; }

    /// <summary>
    /// Artifact kind for the source inventory input.
    /// </summary>
    public required string InventoryArtifactKind { get; init; }

    /// <summary>
    /// Artifact kind for the migration manifest input.
    /// </summary>
    public required string ManifestArtifactKind { get; init; }

    /// <summary>
    /// Artifact kind for the parity evidence input.
    /// </summary>
    public required string ParityEvidenceArtifactKind { get; init; }

    /// <summary>
    /// Secret-safe links or paths to supporting evidence artifacts.
    /// </summary>
    public string[] EvidenceReferences { get; init; } = [];

    /// <summary>
    /// Optional cost and performance evidence derived from the parity evidence pack.
    /// </summary>
    public MigrationAcceptanceCostEvidenceEntry? CostEvidence { get; init; }

    /// <summary>
    /// Deterministic notes explaining important evidence gaps.
    /// </summary>
    public string[] Notes { get; init; } = [];
}

/// <summary>
/// Per-source cost and performance evidence summary.
/// </summary>
public sealed record MigrationAcceptanceCostEvidenceEntry
{
    /// <summary>
    /// Cost evidence classification: <c>pass</c>, <c>warn</c>, <c>fail</c>, or <c>unknown</c>.
    /// </summary>
    public required string State { get; init; }

    /// <summary>
    /// Short reviewer-facing summary of the measured cost evidence.
    /// </summary>
    public required string Summary { get; init; }

    /// <summary>
    /// Measurement scope supplied by the source collector or test harness.
    /// </summary>
    public required string MeasurementScope { get; init; }

    /// <summary>
    /// Total measured duration in milliseconds.
    /// </summary>
    public long? DurationMilliseconds { get; init; }

    /// <summary>
    /// Measured scan duration in milliseconds.
    /// </summary>
    public long? ScanDurationMilliseconds { get; init; }

    /// <summary>
    /// Measured apply, import, or dry-run duration in milliseconds.
    /// </summary>
    public long? ApplyDurationMilliseconds { get; init; }

    /// <summary>
    /// Feature throughput in features per second.
    /// </summary>
    public double? FeatureThroughputPerSecond { get; init; }

    /// <summary>
    /// Resource throughput in resources per second.
    /// </summary>
    public double? ResourceThroughputPerSecond { get; init; }

    /// <summary>
    /// Total source-system requests issued by the run.
    /// </summary>
    public int? SourceRequestCount { get; init; }

    /// <summary>
    /// Total retry attempts observed by the run.
    /// </summary>
    public int? RetryCount { get; init; }

    /// <summary>
    /// Total bytes read from sources or staged inputs.
    /// </summary>
    public long? BytesRead { get; init; }

    /// <summary>
    /// Total bytes written to Honua-owned stores or artifacts.
    /// </summary>
    public long? BytesWritten { get; init; }

    /// <summary>
    /// Size in bytes of the emitted cost evidence artifact.
    /// </summary>
    public long? ArtifactSizeBytes { get; init; }

    /// <summary>
    /// Ratio of manual-review items to measured resources.
    /// </summary>
    public double? ManualReviewRatio { get; init; }

    /// <summary>
    /// Normalized operation-level cost measurements.
    /// </summary>
    public MigrationAcceptanceCostOperationEvidence[] Operations { get; init; } = [];

    /// <summary>
    /// Secret-safe cost evidence artifact references.
    /// </summary>
    public string[] EvidenceReferences { get; init; } = [];

    /// <summary>
    /// Findings that explain warning, failure, or unknown classifications.
    /// </summary>
    public MigrationAcceptanceCostEvidenceFinding[] Findings { get; init; } = [];
}

/// <summary>
/// Operation-level cost and performance evidence normalized for the acceptance suite.
/// </summary>
public sealed record MigrationAcceptanceCostOperationEvidence
{
    /// <summary>
    /// Stable operation identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Migration stage measured by this operation.
    /// </summary>
    public required string Stage { get; init; }

    /// <summary>
    /// Operation evidence state.
    /// </summary>
    public required string State { get; init; }

    /// <summary>
    /// Measured operation duration in milliseconds.
    /// </summary>
    public long? DurationMilliseconds { get; init; }

    /// <summary>
    /// Operation feature throughput in features per second.
    /// </summary>
    public double? FeatureThroughputPerSecond { get; init; }

    /// <summary>
    /// Source-system requests issued by this operation.
    /// </summary>
    public int? SourceRequestCount { get; init; }

    /// <summary>
    /// Retry attempts observed for this operation.
    /// </summary>
    public int? RetryCount { get; init; }

    /// <summary>
    /// Bytes read by this operation.
    /// </summary>
    public long? BytesRead { get; init; }

    /// <summary>
    /// Bytes written by this operation.
    /// </summary>
    public long? BytesWritten { get; init; }

    /// <summary>
    /// Operation evidence artifact size in bytes.
    /// </summary>
    public long? ArtifactSizeBytes { get; init; }

    /// <summary>
    /// Secret-safe operation evidence references.
    /// </summary>
    public string[] EvidenceReferences { get; init; } = [];
}

/// <summary>
/// Finding that explains a cost evidence classification.
/// </summary>
public sealed record MigrationAcceptanceCostEvidenceFinding
{
    /// <summary>
    /// Stable finding identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Finding state: <c>warn</c>, <c>fail</c>, or <c>unknown</c>.
    /// </summary>
    public required string State { get; init; }

    /// <summary>
    /// Human-readable finding summary.
    /// </summary>
    public required string Summary { get; init; }

    /// <summary>
    /// Remediation guidance for reviewers or follow-up work.
    /// </summary>
    public string[] Remediation { get; init; } = [];
}

/// <summary>
/// Evidence for one canonical migration acceptance stage.
/// </summary>
public sealed record MigrationAcceptanceEvidenceStage
{
    /// <summary>
    /// Stable stage identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Stage state: <c>pass</c>, <c>fail</c>, <c>unknown</c>, or <c>not-applicable</c>.
    /// </summary>
    public required string State { get; init; }

    /// <summary>
    /// Human-readable stage summary.
    /// </summary>
    public required string Summary { get; init; }

    /// <summary>
    /// Artifact kinds supporting this stage.
    /// </summary>
    public string[] ArtifactKinds { get; init; } = [];

    /// <summary>
    /// Secret-safe links or paths to supporting evidence artifacts for this stage.
    /// </summary>
    public string[] EvidenceReferences { get; init; } = [];

    /// <summary>
    /// Deterministic notes explaining stage limitations or follow-up work.
    /// </summary>
    public string[] Notes { get; init; } = [];
}

/// <summary>
/// Blocking migration acceptance evidence gap.
/// </summary>
public sealed record MigrationAcceptanceEvidenceGap
{
    /// <summary>
    /// Stable gap identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Source kind associated with the gap.
    /// </summary>
    public required string SourceKind { get; init; }

    /// <summary>
    /// Gap state. Blocking gaps normally use <c>fail</c> or <c>unknown</c>.
    /// </summary>
    public required string State { get; init; }

    /// <summary>
    /// Human-readable gap summary.
    /// </summary>
    public required string Summary { get; init; }

    /// <summary>
    /// Remediation guidance needed before the website migration claim can use this suite.
    /// </summary>
    public string[] Remediation { get; init; } = [];
}

/// <summary>
/// Stable automation classifications for migration acceptance entries.
/// </summary>
public static class MigrationAutomationLevels
{
    /// <summary>All entry resources can be migrated automatically based on current evidence.</summary>
    public const string Automated = "automated";

    /// <summary>Some resources require assisted operator review before cutover.</summary>
    public const string Assisted = "assisted";

    /// <summary>The entry is dominated by manual-review items.</summary>
    public const string ManualReview = "manual-review";

    /// <summary>The entry cannot be migrated by the current deterministic path.</summary>
    public const string Unsupported = "unsupported";
}

/// <summary>
/// Stable stage identifiers for migration acceptance evidence entries.
/// </summary>
public static class MigrationAcceptanceStageIds
{
    /// <summary>Source scan stage.</summary>
    public const string Scan = "scan";

    /// <summary>Manifest translation stage.</summary>
    public const string Manifest = "manifest";

    /// <summary>Apply or dry-run stage.</summary>
    public const string ApplyOrDryRun = "apply-dry-run";

    /// <summary>Target publish stage.</summary>
    public const string Publish = "publish";

    /// <summary>Parity evidence stage.</summary>
    public const string Parity = "parity";

    /// <summary>Cutover readiness stage.</summary>
    public const string Readiness = "readiness";
}

/// <summary>
/// Stable cost evidence classifications.
/// </summary>
public static class MigrationCostEvidenceStates
{
    /// <summary>Cost evidence satisfies configured thresholds.</summary>
    public const string Pass = "pass";

    /// <summary>Cost evidence is present but exceeds warning thresholds.</summary>
    public const string Warn = "warn";

    /// <summary>Cost evidence is missing required measurements or exceeds failure thresholds.</summary>
    public const string Fail = "fail";

    /// <summary>Cost evidence is not available or not yet reviewed.</summary>
    public const string Unknown = "unknown";
}
