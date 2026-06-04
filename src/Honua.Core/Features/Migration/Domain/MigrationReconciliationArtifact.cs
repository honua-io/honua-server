// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Migration.Domain;

/// <summary>
/// Stable classification values reused across every reconciliation probe so reports stay
/// machine-readable. Matches the existing parity-runner vocabulary (pass / warn / fail) so
/// downstream UI and admin endpoints can render them consistently.
/// </summary>
public static class MigrationReconciliationClassifications
{
    /// <summary>The probe is inside its pass band.</summary>
    public const string Pass = "pass";

    /// <summary>The probe is outside the pass band but inside the warn band; operator review suggested.</summary>
    public const string Warn = "warn";

    /// <summary>The probe is outside the warn band; the run cannot transition to Completed.</summary>
    public const string Fail = "fail";

    /// <summary>The probe was skipped because the input was missing or unsupported. Treated as warn for aggregation.</summary>
    public const string Skipped = "skipped";
}

/// <summary>
/// Deterministic per-run reconciliation artifact persisted by the Validating phase.
/// </summary>
public sealed record MigrationReconciliationArtifact
{
    /// <summary>
    /// Stable artifact kind identifier. Used by the evidence pack and admin endpoints to
    /// discriminate this artifact from the apply-execution / parity artifacts.
    /// </summary>
    public string ArtifactKind { get; init; } = "honua.migration.reconciliation";

    /// <summary>
    /// Artifact schema version.
    /// </summary>
    public string ArtifactVersion { get; init; } = "1.0";

    /// <summary>
    /// Originating migration run identifier.
    /// </summary>
    public required string RunId { get; init; }

    /// <summary>
    /// Source kind that produced the layers.
    /// </summary>
    public required string SourceKind { get; init; }

    /// <summary>
    /// Aggregate classification across every layer. Worst probe classification wins:
    /// any <c>fail</c> → <c>fail</c>; any <c>warn</c> → <c>warn</c>; otherwise <c>pass</c>.
    /// </summary>
    public required string Classification { get; init; }

    /// <summary>
    /// UTC instant the reconciliation began.
    /// </summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// UTC instant the reconciliation completed.
    /// </summary>
    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>
    /// Aggregate counts across every per-layer report. Useful for badge rendering in the
    /// admin UI and for the run summary line.
    /// </summary>
    public required MigrationReconciliationSummary Summary { get; init; }

    /// <summary>
    /// Per-layer reconciliation reports. Ordered by source layer id for deterministic
    /// fingerprinting.
    /// </summary>
    public MigrationReconciliationLayerReport[] Layers { get; init; } = [];

    /// <summary>
    /// Sorted, deduplicated, secret-safe reason strings rolled up from every per-probe
    /// failure across every layer. Empty when the run is <c>pass</c>.
    /// </summary>
    public string[] Reasons { get; init; } = [];

    /// <summary>
    /// Tolerances applied to this run. Persisted so an audit can replay the same gate.
    /// </summary>
    public required LayerReconciliationOptions Options { get; init; }
}

/// <summary>
/// Aggregate counts for a <see cref="MigrationReconciliationArtifact"/>.
/// </summary>
public sealed record MigrationReconciliationSummary
{
    /// <summary>Total per-layer reports.</summary>
    public int LayerCount { get; init; }

    /// <summary>Number of layers classified <c>pass</c>.</summary>
    public int PassCount { get; init; }

    /// <summary>Number of layers classified <c>warn</c>.</summary>
    public int WarnCount { get; init; }

    /// <summary>Number of layers classified <c>fail</c>.</summary>
    public int FailCount { get; init; }

    /// <summary>Number of layers classified <c>skipped</c> (e.g. missing target id).</summary>
    public int SkippedCount { get; init; }
}

/// <summary>
/// Per-layer reconciliation report (one per source layer in the run).
/// </summary>
public sealed record MigrationReconciliationLayerReport
{
    /// <summary>Source-side layer identifier.</summary>
    public required string SourceLayerId { get; init; }

    /// <summary>Optional source-side layer display name.</summary>
    public string? SourceLayerName { get; init; }

    /// <summary>
    /// Honua catalog layer id (when the apply produced one). <c>null</c> when the apply did
    /// not publish a queryable layer; the report is recorded with <c>skipped</c>.
    /// </summary>
    public int? TargetHonuaLayerId { get; init; }

    /// <summary>Aggregate classification across the four probes for this layer.</summary>
    public required string Classification { get; init; }

    /// <summary>Feature-count probe.</summary>
    public required MigrationReconciliationCountProbe Count { get; init; }

    /// <summary>Geometry-validity probe.</summary>
    public required MigrationReconciliationGeometryProbe Geometry { get; init; }

    /// <summary>Content / attribute-keys probe.</summary>
    public required MigrationReconciliationContentProbe Content { get; init; }

    /// <summary>Extent probe.</summary>
    public required MigrationReconciliationExtentProbe Extent { get; init; }
}

/// <summary>
/// Feature-count probe result.
/// </summary>
public sealed record MigrationReconciliationCountProbe
{
    /// <summary>Source-side count snapshot at apply time. <c>null</c> when the source did not advertise.</summary>
    public long? SourceCount { get; init; }

    /// <summary>Target-side count returned by Honua post-apply.</summary>
    public long? TargetCount { get; init; }

    /// <summary>Target - Source. <c>null</c> when either side is unavailable.</summary>
    public long? Delta { get; init; }

    /// <summary>|Delta| / Source. <c>null</c> when source is <c>0</c> or unavailable.</summary>
    public double? DeltaRatio { get; init; }

    /// <summary>Secret-safe filter mirror used on the count query, when one was supplied.</summary>
    public string? FilterMirror { get; init; }

    /// <summary>Pass / warn / fail / skipped.</summary>
    public required string Classification { get; init; }

    /// <summary>Operator-visible explanation. <c>null</c> when the probe passed.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Geometry-validity probe result.
/// </summary>
public sealed record MigrationReconciliationGeometryProbe
{
    /// <summary>Number of features sampled (clamped by available rows).</summary>
    public int Sampled { get; init; }

    /// <summary>Number of sampled features whose geometry was present and well-formed.</summary>
    public int Valid { get; init; }

    /// <summary>Valid / Sampled, or <c>1</c> when nothing was sampled.</summary>
    public double Ratio { get; init; }

    /// <summary>Pass / warn / fail / skipped.</summary>
    public required string Classification { get; init; }

    /// <summary>Operator-visible explanation. <c>null</c> when the probe passed.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Content / attribute-keys probe result.
/// </summary>
public sealed record MigrationReconciliationContentProbe
{
    /// <summary>Sorted source-side field names (snapshot).</summary>
    public string[] SourceFieldNames { get; init; } = [];

    /// <summary>Sorted target-side field names sampled from the published layer.</summary>
    public string[] TargetFieldNames { get; init; } = [];

    /// <summary>
    /// Source field names that were not present on the target. Hard-failure trigger.
    /// </summary>
    public string[] MissingOnTarget { get; init; } = [];

    /// <summary>Target field names that were not present in the source (e.g. ObjectID remap).</summary>
    public string[] ExtraOnTarget { get; init; } = [];

    /// <summary>Pass / warn / fail / skipped.</summary>
    public required string Classification { get; init; }

    /// <summary>Operator-visible explanation. <c>null</c> when the probe passed.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Spatial-extent probe result.
/// </summary>
public sealed record MigrationReconciliationExtentProbe
{
    /// <summary>Source-side extent snapshot at apply time. <c>null</c> when none was advertised.</summary>
    public ExtentBox? Source { get; init; }

    /// <summary>Target-side extent returned by Honua post-apply.</summary>
    public ExtentBox? Target { get; init; }

    /// <summary>
    /// Absolute differences between source and target extents normalized by source dimension.
    /// <c>null</c> when either side is unavailable or source has zero width/height.
    /// </summary>
    public double? MaxDimensionDelta { get; init; }

    /// <summary>Pass / warn / fail / skipped.</summary>
    public required string Classification { get; init; }

    /// <summary>Operator-visible explanation. <c>null</c> when the probe passed.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Minimal flat extent representation used by the reconciliation artifact so the schema
/// does not pull in <see cref="Honua.Core.Features.Shared.Models.BoundingBox"/>'s richer
/// type surface (CRS uri, axis order, etc.) — those concerns belong to the catalog layer,
/// not to the reconciliation evidence.
/// </summary>
public readonly record struct ExtentBox
{
    /// <summary>Minimum X coordinate.</summary>
    public required double MinX { get; init; }

    /// <summary>Minimum Y coordinate.</summary>
    public required double MinY { get; init; }

    /// <summary>Maximum X coordinate.</summary>
    public required double MaxX { get; init; }

    /// <summary>Maximum Y coordinate.</summary>
    public required double MaxY { get; init; }

    /// <summary>Spatial reference SRID (default <c>4326</c> when unknown).</summary>
    public required int Srid { get; init; }
}
