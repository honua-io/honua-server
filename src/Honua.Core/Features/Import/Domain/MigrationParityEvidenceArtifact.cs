// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

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
