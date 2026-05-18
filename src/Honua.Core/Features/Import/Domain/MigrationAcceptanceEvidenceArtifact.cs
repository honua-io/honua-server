// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

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
    /// Deterministic notes explaining important evidence gaps.
    /// </summary>
    public string[] Notes { get; init; } = [];
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
