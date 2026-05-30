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
namespace Honua.Core.Features.Migration.Domain;

/// <summary>
/// Slice 4 of issue #1015. Single deterministic evidence bundle emitted from a
/// successful GeoServer migration run. Aggregates the upstream
/// <see cref="MigrationSourceInventoryArtifact"/>, the translated
/// <see cref="MigrationManifestArtifact"/>, and the per-stage step results from
/// the apply-execution artifact so reviewers (and nightly fixture runs) have a
/// single artifact to audit catalog, data, and style migration outcomes.
/// </summary>
/// <remarks>
/// <para>
/// The pack carries a SHA-256 fingerprint computed over the canonical JSON of
/// its bundle so identical inputs always produce the same fingerprint. The
/// fingerprint deliberately excludes wall-clock timestamps and the generator
/// label so re-runs across machines stay byte-identical.
/// </para>
/// <para>
/// Privacy posture: <see cref="MigrationEvidencePackBundle.Source"/> and the
/// stage-level summaries reuse the redaction behavior of the upstream
/// inventory artifact. The builder additionally strips credentials from the
/// source URL and never includes raw style bodies or feature payloads — only
/// counts and diagnostics derived from slices 1-3.
/// </para>
/// <para>
/// AOT note: this record uses POCO-only properties (no polymorphic
/// converters, no <c>JsonExtensionData</c>) so the default
/// <c>System.Text.Json</c> serializer remains trim/AOT safe.
/// </para>
/// </remarks>
public sealed record MigrationEvidencePackArtifact
{
    /// <summary>
    /// Stable artifact kind identifier.
    /// </summary>
    public string ArtifactKind { get; init; } = "honua.migration.evidence-pack";

    /// <summary>
    /// Artifact schema version.
    /// </summary>
    public string ArtifactVersion { get; init; } = "1.0";

    /// <summary>
    /// Stable run identifier supplied by the harness or nightly workflow.
    /// </summary>
    public required string RunId { get; init; }

    /// <summary>
    /// Free-form generator label, e.g. <c>geoserver-migration-evidence-builder/1.0</c>.
    /// Excluded from the bundle fingerprint so re-runs across CI images stay
    /// byte-identical.
    /// </summary>
    public required string Generator { get; init; }

    /// <summary>
    /// UTC instant the pack was generated. Excluded from the bundle fingerprint
    /// so re-runs stay byte-identical.
    /// </summary>
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>
    /// SHA-256 fingerprint computed over the canonical JSON of
    /// <see cref="Bundle"/>. Identical inputs always produce the same value.
    /// </summary>
    public required string BundleFingerprint { get; init; }

    /// <summary>
    /// Deterministic evidence bundle that aggregates slice 1-3 artifacts.
    /// </summary>
    public required MigrationEvidencePackBundle Bundle { get; init; }
}

/// <summary>
/// Deterministic content bundle covered by
/// <see cref="MigrationEvidencePackArtifact.BundleFingerprint"/>.
/// </summary>
public sealed record MigrationEvidencePackBundle
{
    /// <summary>
    /// Source kind identifier such as <c>geoserver-rest</c>.
    /// </summary>
    public required string SourceKind { get; init; }

    /// <summary>
    /// Secret-safe source identity (credentials and URL secrets stripped).
    /// </summary>
    public required MigrationSourceIdentity Source { get; init; }

    /// <summary>
    /// Operator-requested workspace scope. Empty when the run applied to all
    /// workspaces. Captured here as workspace-scoping evidence so reviewers can
    /// audit that styles and data sources outside the requested scope were not
    /// migrated.
    /// </summary>
    public required MigrationEvidencePackWorkspaceScope WorkspaceScope { get; init; }

    /// <summary>
    /// Plan fingerprint and replay token copied from the apply-execution
    /// artifact so downstream consumers can correlate the pack with the exact
    /// apply plan that was executed.
    /// </summary>
    public required MigrationEvidencePackApplyIdentity Apply { get; init; }

    /// <summary>
    /// Aggregate counts across the catalog, data, and style stages.
    /// </summary>
    public required MigrationEvidencePackSummary Summary { get; init; }

    /// <summary>
    /// Per-stage step results derived from
    /// <see cref="MigrationApplyExecutionArtifact.StepResults"/>. Ordering is
    /// stable: stages in canonical order, steps within each stage ordered by
    /// step id.
    /// </summary>
    public MigrationEvidencePackStage[] Stages { get; init; } = [];

    /// <summary>
    /// Aggregated style-conversion diagnostics surfaced by slice 3 so the pack
    /// carries explicit manual-review records when SLD-to-MapLibre conversion
    /// did not guarantee visual parity.
    /// </summary>
    public MigrationEvidencePackStyleDiagnostic[] StyleDiagnostics { get; init; } = [];

    /// <summary>
    /// Inventory snapshot copied from the slice-1 input.
    /// </summary>
    public required MigrationSourceInventoryArtifact Inventory { get; init; }

    /// <summary>
    /// Manifest snapshot copied from the slice-1 input.
    /// </summary>
    public required MigrationManifestArtifact Manifest { get; init; }
}

/// <summary>
/// Operator-requested workspace scope captured in the evidence pack.
/// </summary>
public sealed record MigrationEvidencePackWorkspaceScope
{
    /// <summary>
    /// Whether the operator restricted the apply run to specific workspaces.
    /// When <c>false</c>, all source workspaces were eligible.
    /// </summary>
    public required bool Restricted { get; init; }

    /// <summary>
    /// Deterministically ordered list of requested workspace names. Empty when
    /// <see cref="Restricted"/> is <c>false</c>.
    /// </summary>
    public string[] WorkspaceNames { get; init; } = [];
}

/// <summary>
/// Apply identity referenced from the evidence pack. Mirrors the
/// <see cref="MigrationApplyExecutionArtifact"/> identity fields so consumers
/// can correlate the pack with the executed plan without having to load both
/// artifacts.
/// </summary>
public sealed record MigrationEvidencePackApplyIdentity
{
    /// <summary>
    /// Plan fingerprint copied from the apply-execution artifact.
    /// </summary>
    public required string PlanFingerprint { get; init; }

    /// <summary>
    /// Replay token copied from the apply-execution artifact.
    /// </summary>
    public required string ReplayToken { get; init; }

    /// <summary>
    /// Execution mode reported by the apply run, e.g. <c>catalog-apply</c>.
    /// </summary>
    public required string ExecutionMode { get; init; }
}

/// <summary>
/// Aggregate evidence summary across catalog, data, and style stages.
/// </summary>
public sealed record MigrationEvidencePackSummary
{
    /// <summary>
    /// Total number of apply-plan steps considered by the run.
    /// </summary>
    public int TotalStepCount { get; init; }

    /// <summary>
    /// Number of steps that completed with the <c>applied</c> outcome.
    /// </summary>
    public int AppliedStepCount { get; init; }

    /// <summary>
    /// Number of steps that were already present in the target catalog.
    /// </summary>
    public int AlreadyAppliedStepCount { get; init; }

    /// <summary>
    /// Number of steps that remain explicit manual-review work.
    /// </summary>
    public int ManualReviewStepCount { get; init; }

    /// <summary>
    /// Number of steps that are unsupported by this apply path.
    /// </summary>
    public int UnsupportedStepCount { get; init; }

    /// <summary>
    /// Number of steps that failed unexpectedly.
    /// </summary>
    public int FailedStepCount { get; init; }

    /// <summary>
    /// Number of style steps recorded with at least one error-severity
    /// conversion diagnostic. Per issue #1015 AC, these block any visual
    /// parity claim.
    /// </summary>
    public int StyleManualReviewCount { get; init; }
}

/// <summary>
/// Per-stage view of the apply-execution step results.
/// </summary>
public sealed record MigrationEvidencePackStage
{
    /// <summary>
    /// Canonical stage id: <c>catalog</c>, <c>data</c>, or <c>style</c>.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Number of step results in this stage.
    /// </summary>
    public int StepCount { get; init; }

    /// <summary>
    /// Number of step results with the <c>applied</c> outcome.
    /// </summary>
    public int AppliedCount { get; init; }

    /// <summary>
    /// Number of step results with the <c>already-applied</c> outcome.
    /// </summary>
    public int AlreadyAppliedCount { get; init; }

    /// <summary>
    /// Number of step results with the <c>manual-review</c> outcome.
    /// </summary>
    public int ManualReviewCount { get; init; }

    /// <summary>
    /// Number of step results with the <c>unsupported</c> outcome.
    /// </summary>
    public int UnsupportedCount { get; init; }

    /// <summary>
    /// Number of step results with the <c>failed</c> outcome.
    /// </summary>
    public int FailedCount { get; init; }

    /// <summary>
    /// Per-step results ordered by <see cref="MigrationApplyExecutionStepResult.StepId"/>.
    /// </summary>
    public MigrationApplyExecutionStepResult[] StepResults { get; init; } = [];
}

/// <summary>
/// Aggregated style-conversion diagnostic surfaced from slice-3 evidence.
/// </summary>
public sealed record MigrationEvidencePackStyleDiagnostic
{
    /// <summary>
    /// Source style identifier the diagnostic was raised for.
    /// </summary>
    public required string SourceId { get; init; }

    /// <summary>
    /// Resolved step outcome for the style step (e.g. <c>manual-review</c>).
    /// </summary>
    public required string StepOutcome { get; init; }

    /// <summary>
    /// Diagnostic message copied from the slice-3 apply step.
    /// </summary>
    public required string Message { get; init; }
}

/// <summary>
/// Canonical stage identifiers used by the evidence pack.
/// </summary>
public static class MigrationEvidencePackStageIds
{
    /// <summary>Slice 1: catalog (workspace + layer-group) entries.</summary>
    public const string Catalog = "catalog";

    /// <summary>Slice 2: data-source registration + feature data copy.</summary>
    public const string Data = "data";

    /// <summary>Slice 3: style persistence + conversion diagnostics.</summary>
    public const string Style = "style";
}
