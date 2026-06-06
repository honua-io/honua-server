// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Migration.Domain;

/// <summary>
/// Signed, deterministic roll-up of a migration run's reconciliation outcome (issue #1381). The
/// scorecard deliberately keeps two distinct dimensions of "did the migration succeed?" separate so
/// operators never conflate them:
/// <list type="bullet">
/// <item>
/// <description>
/// <b>Data reconciliation</b> (<see cref="DataReconciliation"/>): did the bytes land — feature
/// counts, geometry validity, attribute keys, and spatial extent of the published layer matched the
/// apply-time source snapshot. Sourced from <see cref="MigrationReconciliationArtifact"/> and the
/// catalog parity report (<see cref="MigrationCatalogReconciliationReport"/>).
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Capability parity</b> (<see cref="CapabilityParity"/>): could the construct be expressed at
/// all on Honua — the per-construct automation/fidelity assessment from the Esri construct
/// capability registry / fidelity matrix. A construct can be unsupported (low capability parity)
/// while the data that <i>was</i> imported reconciles perfectly, and vice versa; the scorecard keeps
/// the two scores side by side rather than averaging them into a single misleading number.
/// </description>
/// </item>
/// </list>
/// </summary>
/// <remarks>
/// The scorecard is fingerprinted with the same SHA-256-over-canonical-JSON mechanism used by the
/// evidence pack (<see cref="MigrationReconciliationScorecard.Fingerprint"/>). The fingerprint is
/// computed over the unsigned body so a verifier can recompute it independently.
/// </remarks>
public sealed record MigrationReconciliationScorecard
{
    /// <summary>Stable artifact kind identifier.</summary>
    public string ArtifactKind { get; init; } = "honua.migration.reconciliation-scorecard";

    /// <summary>Artifact schema version.</summary>
    public string ArtifactVersion { get; init; } = "1.0";

    /// <summary>Originating migration run identifier.</summary>
    public required string RunId { get; init; }

    /// <summary>Source kind that produced the layers, for example <c>arcgis-geoservices-rest</c>.</summary>
    public required string SourceKind { get; init; }

    /// <summary>UTC instant the scorecard was generated.</summary>
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>
    /// Overall gate verdict for the run: <c>pass</c>, <c>warn</c>, or <c>fail</c>. Derived from the
    /// data-reconciliation dimension only — capability parity is advisory and never gates a run by
    /// itself (an unsupported construct that was deliberately skipped is not a faithless import).
    /// </summary>
    public required string Verdict { get; init; }

    /// <summary>Data-reconciliation dimension (count/geometry/content/extent + catalog parity).</summary>
    public required MigrationScorecardDataReconciliation DataReconciliation { get; init; }

    /// <summary>Capability-parity dimension (per-construct automation/fidelity), kept distinct.</summary>
    public required MigrationScorecardCapabilityParity CapabilityParity { get; init; }

    /// <summary>
    /// SHA-256 fingerprint of the scorecard body (every field except this one), formatted
    /// <c>sha256:&lt;hex&gt;</c>. Reuses the evidence-pack fingerprint mechanism so downstream
    /// verifiers can prove the scorecard was not altered. <c>null</c> on an unsigned body; set by the
    /// builder/signer.
    /// </summary>
    public string? Fingerprint { get; init; }
}

/// <summary>
/// Data-reconciliation dimension of a <see cref="MigrationReconciliationScorecard"/>. Aggregates the
/// per-layer data-reconciliation outcomes (count/geometry/content/extent) and, when available, the
/// deeper catalog-parity findings (schema/domain/identifier/relationship/attachment/subtype).
/// </summary>
public sealed record MigrationScorecardDataReconciliation
{
    /// <summary>Aggregate data-reconciliation classification: <c>pass</c>, <c>warn</c>, or <c>fail</c>.</summary>
    public required string Classification { get; init; }

    /// <summary>Total layers covered by data reconciliation.</summary>
    public int LayerCount { get; init; }

    /// <summary>Layers whose data reconciled cleanly.</summary>
    public int PassCount { get; init; }

    /// <summary>Layers with warn-level data-reconciliation findings.</summary>
    public int WarnCount { get; init; }

    /// <summary>Layers with at least one hard data-reconciliation finding.</summary>
    public int FailCount { get; init; }

    /// <summary>Layers skipped (e.g. no published target to reconcile against).</summary>
    public int SkippedCount { get; init; }

    /// <summary>
    /// Number of catalog-parity findings folded into this dimension (0 when no catalog report was
    /// supplied). Catalog findings are part of data reconciliation, not capability parity.
    /// </summary>
    public int CatalogFindingCount { get; init; }

    /// <summary>Per-layer scorecard rows, ordered deterministically by source layer id.</summary>
    public MigrationScorecardLayer[] Layers { get; init; } = [];

    /// <summary>Sorted, deduplicated, secret-safe blocking/advisory reasons rolled up across all layers.</summary>
    public string[] Reasons { get; init; } = [];
}

/// <summary>
/// One per-layer row in the data-reconciliation dimension of the scorecard.
/// </summary>
public sealed record MigrationScorecardLayer
{
    /// <summary>Source-side layer identifier.</summary>
    public required string SourceLayerId { get; init; }

    /// <summary>Optional source-side layer display name.</summary>
    public string? SourceLayerName { get; init; }

    /// <summary>Honua catalog layer id, when a queryable layer was published.</summary>
    public int? TargetHonuaLayerId { get; init; }

    /// <summary>Aggregate per-layer data-reconciliation classification.</summary>
    public required string Classification { get; init; }
}

/// <summary>
/// Capability-parity dimension of a <see cref="MigrationReconciliationScorecard"/>. Summarizes how
/// many source constructs could be expressed automatically / with assistance / not at all on Honua.
/// Kept strictly separate from data reconciliation: this answers "could we express it?" not "did the
/// bytes land?".
/// </summary>
public sealed record MigrationScorecardCapabilityParity
{
    /// <summary>Total source constructs assessed for capability parity.</summary>
    public int ConstructCount { get; init; }

    /// <summary>Constructs Honua can express without operator involvement.</summary>
    public int AutomatedCount { get; init; }

    /// <summary>Constructs Honua can express with operator assistance.</summary>
    public int AssistedCount { get; init; }

    /// <summary>Constructs requiring manual review to express.</summary>
    public int ManualReviewCount { get; init; }

    /// <summary>Constructs Honua cannot express (no capability parity).</summary>
    public int UnsupportedCount { get; init; }

    /// <summary>
    /// Capability-parity ratio in <c>[0, 1]</c>: (automated + assisted) / construct count, or
    /// <c>1</c> when no constructs were assessed. Advisory only — never gates the run.
    /// </summary>
    public double ParityRatio { get; init; }
}
