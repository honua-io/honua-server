// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// Deterministic artifact that describes the safe first apply actions for a
/// translated migration manifest.
/// </summary>
public sealed record MigrationApplyPlanArtifact
{
    /// <summary>
    /// Stable artifact kind identifier.
    /// </summary>
    public string ArtifactKind { get; init; } = "honua.migration.apply-plan";

    /// <summary>
    /// Artifact schema version.
    /// </summary>
    public string ArtifactVersion { get; init; } = "1.0";

    /// <summary>
    /// Source manifest artifact kind this plan was derived from.
    /// </summary>
    public string SourceManifestArtifactKind { get; init; } = "honua.migration.manifest";

    /// <summary>
    /// Source manifest artifact version this plan was derived from.
    /// </summary>
    public string SourceManifestArtifactVersion { get; init; } = "1.0";

    /// <summary>
    /// Source kind identifier such as <c>geoserver-rest</c>.
    /// </summary>
    public required string SourceKind { get; init; }

    /// <summary>
    /// Identity and version information for the scanned source.
    /// </summary>
    public required MigrationSourceIdentity Source { get; init; }

    /// <summary>
    /// Stable SHA-256 replay token over the plan steps and review items.
    /// </summary>
    public required string ReplayToken { get; init; }

    /// <summary>
    /// Stable SHA-256 fingerprint for the plan payload.
    /// </summary>
    public required string PlanFingerprint { get; init; }

    /// <summary>
    /// Aggregate counts for the apply plan.
    /// </summary>
    public required MigrationApplyPlanSummary Summary { get; init; }

    /// <summary>
    /// Ordered apply-plan steps.
    /// </summary>
    public MigrationApplyPlanStep[] Steps { get; init; } = [];

    /// <summary>
    /// Items that require operator review before catalog apply can run.
    /// </summary>
    public MigrationManifestReviewItem[] ManualReviewItems { get; init; } = [];

    /// <summary>
    /// Items that cannot be applied by this migration path.
    /// </summary>
    public MigrationManifestReviewItem[] UnsupportedItems { get; init; } = [];
}

/// <summary>
/// Aggregate counts for a migration apply plan.
/// </summary>
public sealed record MigrationApplyPlanSummary
{
    /// <summary>
    /// Total number of emitted plan steps.
    /// </summary>
    public int TotalStepCount { get; init; }

    /// <summary>
    /// Number of steps that are ready for catalog apply.
    /// </summary>
    public int ReadyStepCount { get; init; }

    /// <summary>
    /// Number of steps that need operator review.
    /// </summary>
    public int ManualReviewStepCount { get; init; }

    /// <summary>
    /// Number of steps whose source item is unsupported by this apply path.
    /// </summary>
    public int UnsupportedStepCount { get; init; }

    /// <summary>
    /// Number of unsupported source items, including items that do not emit a step.
    /// </summary>
    public int UnsupportedItemCount { get; init; }
}

/// <summary>
/// One deterministic operation in a migration apply plan.
/// </summary>
public sealed record MigrationApplyPlanStep
{
    /// <summary>
    /// One-based sequence number for replay.
    /// </summary>
    public int Sequence { get; init; }

    /// <summary>
    /// Stable step identifier.
    /// </summary>
    public required string StepId { get; init; }

    /// <summary>
    /// Source manifest item identifier.
    /// </summary>
    public required string SourceId { get; init; }

    /// <summary>
    /// Source item kind.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Planned action, such as <c>stage-catalog-resource</c> or <c>manual-review</c>.
    /// </summary>
    public required string Action { get; init; }

    /// <summary>
    /// Step disposition: <c>ready</c>, <c>manual-review</c>, or <c>unsupported</c>.
    /// </summary>
    public required string Disposition { get; init; }

    /// <summary>
    /// Target service name from the manifest, when applicable.
    /// </summary>
    public string? TargetServiceName { get; init; }

    /// <summary>
    /// Target resource name from the manifest, when applicable.
    /// </summary>
    public string? TargetResourceName { get; init; }

    /// <summary>
    /// Related source style identifiers.
    /// </summary>
    public string[] StyleIds { get; init; } = [];

    /// <summary>
    /// Related external dependency identifiers.
    /// </summary>
    public string[] ExternalDependencyIds { get; init; } = [];

    /// <summary>
    /// Manual-review or unsupported classification codes related to this step.
    /// </summary>
    public string[] ReviewCodes { get; init; } = [];

    /// <summary>
    /// Compatibility assessment that justified the step disposition.
    /// </summary>
    public required MigrationCompatibilityAssessment Compatibility { get; init; }
}
