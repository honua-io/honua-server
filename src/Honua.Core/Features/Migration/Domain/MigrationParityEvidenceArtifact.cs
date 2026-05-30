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
/// Stable state values used by migration parity and cutover-readiness artifacts.
/// </summary>
public static class MigrationEvidenceStates
{
    /// <summary>Evidence is present and satisfies the check.</summary>
    public const string Pass = "pass";

    /// <summary>Evidence is present and shows the check failed.</summary>
    public const string Fail = "fail";

    /// <summary>Evidence is missing or not yet reviewed.</summary>
    public const string Unknown = "unknown";

    /// <summary>The check is not applicable to this migration.</summary>
    public const string NotApplicable = "not-applicable";
}

/// <summary>
/// Deterministic technical signoff artifact for a migration pilot or cutover review.
/// </summary>
public sealed record MigrationParityEvidenceArtifact
{
    /// <summary>
    /// Stable artifact kind identifier.
    /// </summary>
    public string ArtifactKind { get; init; } = "honua.migration.parity-evidence-pack";

    /// <summary>
    /// Artifact schema version.
    /// </summary>
    public string ArtifactVersion { get; init; } = "1.0";

    /// <summary>
    /// Source kind identifier such as <c>geoserver-rest</c> or <c>arcgis-geoservices-rest</c>.
    /// </summary>
    public required string SourceKind { get; init; }

    /// <summary>
    /// Identity and version information for the scanned source.
    /// </summary>
    public required MigrationSourceIdentity Source { get; init; }

    /// <summary>
    /// Overall parity state across capability, style, data, and readiness sections.
    /// </summary>
    public required string OverallState { get; init; }

    /// <summary>
    /// Human-readable summary for signoff reviewers.
    /// </summary>
    public required string Summary { get; init; }

    /// <summary>
    /// Whether a migration manifest was available as evidence input.
    /// </summary>
    public bool ManifestAvailable { get; init; }

    /// <summary>
    /// Evidence sections grouped by review category.
    /// </summary>
    public MigrationParityEvidenceSection[] Sections { get; init; } = [];

    /// <summary>
    /// Cutover-readiness checklist and aggregate state.
    /// </summary>
    public required MigrationCutoverReadinessSummary CutoverReadiness { get; init; }

    /// <summary>
    /// Optional bounded performance and migration-cost evidence captured for this run.
    /// </summary>
    public MigrationPerformanceCostEvidence? PerformanceCost { get; init; }
}

/// <summary>
/// Group of parity evidence items for one review category.
/// </summary>
public sealed record MigrationParityEvidenceSection
{
    /// <summary>
    /// Stable section identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Section display title.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Aggregate state for the section.
    /// </summary>
    public required string State { get; init; }

    /// <summary>
    /// Evidence items in deterministic order.
    /// </summary>
    public MigrationParityEvidenceItem[] Items { get; init; } = [];
}

/// <summary>
/// Individual evidence item inside a parity section.
/// </summary>
public sealed record MigrationParityEvidenceItem
{
    /// <summary>
    /// Stable item identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Item state: <c>pass</c>, <c>fail</c>, <c>unknown</c>, or <c>not-applicable</c>.
    /// </summary>
    public required string State { get; init; }

    /// <summary>
    /// Short item summary.
    /// </summary>
    public required string Summary { get; init; }

    /// <summary>
    /// Evidence text supporting the assigned state.
    /// </summary>
    public string[] Evidence { get; init; } = [];

    /// <summary>
    /// Remediation guidance for fail or unknown states.
    /// </summary>
    public string[] Remediation { get; init; } = [];

    /// <summary>
    /// Related manifest or inventory identifiers.
    /// </summary>
    public string[] RelatedIds { get; init; } = [];
}

/// <summary>
/// Cutover-readiness checklist and aggregate state.
/// </summary>
public sealed record MigrationCutoverReadinessSummary
{
    /// <summary>
    /// Aggregate readiness state.
    /// </summary>
    public required string State { get; init; }

    /// <summary>
    /// Checklist items in deterministic order.
    /// </summary>
    public MigrationCutoverReadinessItem[] Items { get; init; } = [];
}

/// <summary>
/// Individual cutover-readiness checklist item.
/// </summary>
public sealed record MigrationCutoverReadinessItem
{
    /// <summary>
    /// Stable checklist item identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Checklist item title.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Item state: <c>pass</c>, <c>fail</c>, <c>unknown</c>, or <c>not-applicable</c>.
    /// </summary>
    public required string State { get; init; }

    /// <summary>
    /// Evidence supplied by the operator or generated by the report.
    /// </summary>
    public string[] Evidence { get; init; } = [];

    /// <summary>
    /// Remediation guidance for fail or unknown states.
    /// </summary>
    public string[] Remediation { get; init; } = [];

    /// <summary>
    /// Optional owner responsible for closing the item.
    /// </summary>
    public string? Owner { get; init; }
}

/// <summary>
/// Operator-supplied readiness attestations. Missing items remain unknown.
/// </summary>
public sealed record MigrationReadinessAttestation
{
    /// <summary>
    /// Checklist item attestations.
    /// </summary>
    public MigrationReadinessAttestationItem[] Items { get; init; } = [];
}

/// <summary>
/// Operator-supplied state for one readiness checklist item.
/// </summary>
public sealed record MigrationReadinessAttestationItem
{
    /// <summary>
    /// Stable checklist item identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Attested state.
    /// </summary>
    public required string State { get; init; }

    /// <summary>
    /// Evidence supporting the attested state.
    /// </summary>
    public string[] Evidence { get; init; } = [];

    /// <summary>
    /// Optional owner responsible for the item.
    /// </summary>
    public string? Owner { get; init; }
}

/// <summary>
/// Bounded migration performance and cost evidence that can be attached to parity review artifacts.
/// </summary>
public sealed record MigrationPerformanceCostEvidence
{
    /// <summary>
    /// Stable artifact kind identifier for embedded performance and cost evidence.
    /// </summary>
    public string ArtifactKind { get; init; } = "honua.migration.performance-cost-evidence";

    /// <summary>
    /// Artifact schema version.
    /// </summary>
    public string ArtifactVersion { get; init; } = "1.0";

    /// <summary>
    /// Evidence state: <c>pass</c>, <c>fail</c>, <c>unknown</c>, or <c>not-applicable</c>.
    /// </summary>
    public required string State { get; init; }

    /// <summary>
    /// Short reviewer-facing summary of the collected measurements.
    /// </summary>
    public required string Summary { get; init; }

    /// <summary>
    /// Human-readable scope for the measurement, such as <c>fixture scan</c> or <c>pilot import dry run</c>.
    /// </summary>
    public required string MeasurementScope { get; init; }

    /// <summary>
    /// Aggregate duration, volume, retry, and review counters.
    /// </summary>
    public required MigrationPerformanceCostTotals Totals { get; init; }

    /// <summary>
    /// Operation-level measurements in deterministic order.
    /// </summary>
    public MigrationPerformanceCostOperation[] Operations { get; init; } = [];

    /// <summary>
    /// Secret-safe evidence artifact references. Query strings, fragments, and URL credentials are removed by the generator.
    /// </summary>
    public string[] EvidenceReferences { get; init; } = [];
}

/// <summary>
/// Aggregate migration performance and cost counters for a measured run.
/// </summary>
public sealed record MigrationPerformanceCostTotals
{
    /// <summary>
    /// Total measured duration in milliseconds.
    /// </summary>
    public long? DurationMilliseconds { get; init; }

    /// <summary>
    /// Total resources processed by the measured run.
    /// </summary>
    public long? ResourceCount { get; init; }

    /// <summary>
    /// Total features processed by the measured run.
    /// </summary>
    public long? FeatureCount { get; init; }

    /// <summary>
    /// Total bytes read from source systems or staged artifacts.
    /// </summary>
    public long? BytesRead { get; init; }

    /// <summary>
    /// Total bytes written to Honua-owned stores or output artifacts.
    /// </summary>
    public long? BytesWritten { get; init; }

    /// <summary>
    /// Total retry attempts observed across measured operations.
    /// </summary>
    public int? RetryCount { get; init; }

    /// <summary>
    /// Total source-system requests issued by the measured run.
    /// </summary>
    public int? SourceRequestCount { get; init; }

    /// <summary>
    /// Total items requiring manual review after the measured run.
    /// </summary>
    public int? ManualReviewCount { get; init; }

    /// <summary>
    /// Size in bytes of the emitted cost or performance evidence artifact.
    /// </summary>
    public long? ArtifactSizeBytes { get; init; }
}

/// <summary>
/// Operation-level performance and migration-cost measurement.
/// </summary>
public sealed record MigrationPerformanceCostOperation
{
    /// <summary>
    /// Stable operation identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Migration stage, such as <c>scan</c>, <c>manifest</c>, <c>import</c>, or <c>parity</c>.
    /// </summary>
    public required string Stage { get; init; }

    /// <summary>
    /// Evidence state for this measurement.
    /// </summary>
    public required string State { get; init; }

    /// <summary>
    /// Measured duration in milliseconds for this operation.
    /// </summary>
    public long? DurationMilliseconds { get; init; }

    /// <summary>
    /// Number of resources processed by this operation.
    /// </summary>
    public long? ResourceCount { get; init; }

    /// <summary>
    /// Number of features processed by this operation.
    /// </summary>
    public long? FeatureCount { get; init; }

    /// <summary>
    /// Number of bytes read by this operation.
    /// </summary>
    public long? BytesRead { get; init; }

    /// <summary>
    /// Number of bytes written by this operation.
    /// </summary>
    public long? BytesWritten { get; init; }

    /// <summary>
    /// Retry attempts observed for this operation.
    /// </summary>
    public int? RetryCount { get; init; }

    /// <summary>
    /// Source-system requests issued by this operation.
    /// </summary>
    public int? SourceRequestCount { get; init; }

    /// <summary>
    /// Items from this operation that require manual review.
    /// </summary>
    public int? ManualReviewCount { get; init; }

    /// <summary>
    /// Size in bytes of the operation-specific evidence artifact.
    /// </summary>
    public long? ArtifactSizeBytes { get; init; }

    /// <summary>
    /// Secret-safe evidence artifact references. Query strings, fragments, and URL credentials are removed by the generator.
    /// </summary>
    public string[] EvidenceReferences { get; init; } = [];
}
