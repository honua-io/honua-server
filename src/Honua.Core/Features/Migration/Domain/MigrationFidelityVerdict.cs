// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Migration.Domain;

/// <summary>
/// Stable verdict labels describing how much of a migration was proven faithful (issue #4600).
/// The import <em>status</em> answers "did the job finish"; this verdict answers "is the migrated
/// service actually equivalent to the source". A job that finishes is not automatically a
/// full-fidelity migration: checks can be skipped, and skipped checks are not evidence of parity.
/// </summary>
public static class MigrationFidelityVerdicts
{
    /// <summary>
    /// Every required fidelity check executed and every one of them passed. This is the only
    /// verdict that may be reported to an operator as a complete, equivalent migration.
    /// </summary>
    public const string FullFidelity = "full-fidelity";

    /// <summary>
    /// No blocking discrepancy was observed, but at least one required check did not execute
    /// (no reconciliation service registered, no published catalog entry to read back, an
    /// attachment metadata batch that never returned). The migrated data is usable, but parity
    /// has not been proven and must not be presented as full fidelity.
    /// </summary>
    public const string Unverified = "unverified";

    /// <summary>
    /// At least one blocking discrepancy was observed: lost records, lost attachments, omitted
    /// relationships, or a catalog/schema difference between source and target. The run is routed
    /// to operator review rather than reported as a successful migration.
    /// </summary>
    public const string Incomplete = "incomplete";
}

/// <summary>
/// Stable severity labels for <see cref="MigrationFidelityDifference"/>.
/// </summary>
public static class MigrationFidelityDifferenceSeverities
{
    /// <summary>The difference blocks a successful migration verdict.</summary>
    public const string Blocking = "blocking";

    /// <summary>
    /// A required check did not execute. Not proof of a difference, but equally not proof of
    /// parity, so it downgrades the verdict to <see cref="MigrationFidelityVerdicts.Unverified"/>.
    /// </summary>
    public const string Unverified = "unverified";
}

/// <summary>
/// Stable, machine-readable codes for the per-resource differences that decide the fidelity
/// verdict. Operators and the migration CLI key remediation off these codes, so they are part of
/// the persisted evidence contract and must not be renamed.
/// </summary>
public static class MigrationFidelityDifferenceCodes
{
    /// <summary>Source records were read but did not land in the target table.</summary>
    public const string RecordsLost = "fidelity.records.lost";

    /// <summary>Source attachments were advertised but did not land in the target attachment store.</summary>
    public const string AttachmentsLost = "fidelity.attachments.lost";

    /// <summary>
    /// The advertised attachment inventory could not be read for some parent features, so the
    /// attachment count was never established independently of the feature count.
    /// </summary>
    public const string AttachmentsUnverified = "fidelity.attachments.unverified";

    /// <summary>A relationship discovered on the source was not persisted onto the target.</summary>
    public const string RelationshipOmitted = "fidelity.relationship.omitted";

    /// <summary>The data-movement reconciliation probe reported a blocking finding.</summary>
    public const string DataReconciliationFailed = "fidelity.data-reconciliation.failed";

    /// <summary>The catalog (schema/domain/identifier/subtype) reconciliation reported a fail finding.</summary>
    public const string CatalogReconciliationFailed = "fidelity.catalog-reconciliation.failed";

    /// <summary>The data-movement reconciliation probe did not execute.</summary>
    public const string DataReconciliationNotExecuted = "fidelity.data-reconciliation.not-executed";

    /// <summary>The catalog reconciliation pass did not execute.</summary>
    public const string CatalogReconciliationNotExecuted = "fidelity.catalog-reconciliation.not-executed";

    /// <summary>
    /// A layer in a service (batch) import did not complete at full fidelity: it failed, was
    /// cancelled, never ran, or was routed to review with a blocking difference.
    /// </summary>
    public const string ServiceLayerIncomplete = "fidelity.service.layer-incomplete";

    /// <summary>
    /// A layer in a service (batch) import completed, but its own fidelity verdict is not
    /// full-fidelity (a required check did not execute, or no verdict was recorded).
    /// </summary>
    public const string ServiceLayerUnverified = "fidelity.service.layer-unverified";

    /// <summary>
    /// A service import requested relationship apply, but the apply step never ran, so every
    /// relationship in the manifest is missing from the target.
    /// </summary>
    public const string RelationshipApplyNotExecuted = "fidelity.relationship-apply.not-executed";
}

/// <summary>
/// One actionable source-to-target difference (or unexecuted check) recorded against a migration
/// run. Persisted on the import result so an operator can act on the specific resource rather than
/// re-running the whole migration to find out what moved.
/// </summary>
public sealed record MigrationFidelityDifference
{
    /// <summary>Stable finding code; see <see cref="MigrationFidelityDifferenceCodes"/>.</summary>
    public required string Code { get; init; }

    /// <summary>
    /// <see cref="MigrationFidelityDifferenceSeverities.Blocking"/> or
    /// <see cref="MigrationFidelityDifferenceSeverities.Unverified"/>.
    /// </summary>
    public required string Severity { get; init; }

    /// <summary>
    /// The resource the difference applies to (layer name, field name, relationship id,
    /// <c>attachments</c>, ...). Disambiguates multiple differences sharing a <see cref="Code"/>.
    /// </summary>
    public string? Subject { get; init; }

    /// <summary>Expected value, sourced from the source service snapshot.</summary>
    public string? Expected { get; init; }

    /// <summary>Observed value, sourced from the published target.</summary>
    public string? Actual { get; init; }

    /// <summary>Operator-readable explanation of the difference and what it implies.</summary>
    public required string Summary { get; init; }
}
