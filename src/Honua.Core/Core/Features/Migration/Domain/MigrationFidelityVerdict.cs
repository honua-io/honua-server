// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Migration.Domain;

// A finished job is not full fidelity: a skipped check is not evidence of parity.
public static class MigrationFidelityVerdicts
{
    // Full fidelity is the only verdict that may be reported as a complete, equivalent migration.
    public const string FullFidelity = "full-fidelity";

    // A skipped required check leaves the data usable but must not be presented as full fidelity.
    public const string Unverified = "unverified";

    // A blocking discrepancy routes the run to operator review instead of a successful migration.
    public const string Incomplete = "incomplete";
}

public static class MigrationFidelityDifferenceSeverities
{
    public const string Blocking = "blocking";

    // An unexecuted check is not proof of a difference and not proof of parity, so the verdict drops to unverified.
    public const string Unverified = "unverified";
}

// These codes are the persisted evidence contract and must not be renamed.
public static class MigrationFidelityDifferenceCodes
{
    public const string RecordsLost = "fidelity.records.lost";

    public const string AttachmentsLost = "fidelity.attachments.lost";

    // The attachment inventory could not be read, so the attachment count was never established apart from the feature count.
    public const string AttachmentsUnverified = "fidelity.attachments.unverified";

    public const string RelationshipOmitted = "fidelity.relationship.omitted";

    public const string DataReconciliationFailed = "fidelity.data-reconciliation.failed";

    public const string CatalogReconciliationFailed = "fidelity.catalog-reconciliation.failed";

    public const string DataReconciliationNotExecuted = "fidelity.data-reconciliation.not-executed";

    public const string CatalogReconciliationNotExecuted = "fidelity.catalog-reconciliation.not-executed";

    public const string PublishNotCompleted = "fidelity.publish.not-completed";

    // The layer failed, was cancelled, never ran, or was routed to review with a blocking difference.
    public const string ServiceLayerIncomplete = "fidelity.service.layer-incomplete";

    // The layer finished, but a required check did not execute or no verdict was recorded.
    public const string ServiceLayerUnverified = "fidelity.service.layer-unverified";

    // Relationship apply was requested and never ran, so every manifest relationship is missing from the target.
    public const string RelationshipApplyNotExecuted = "fidelity.relationship-apply.not-executed";

    // The source population changed between the start and the end of the transfer, so the target is not one snapshot.
    public const string SourceChangedDuringTransfer = "fidelity.source.changed-during-transfer";

    // The source count was not read at both ends, so changes during the copy were never ruled out.
    public const string SourceSnapshotUnverified = "fidelity.source.snapshot-unverified";

    // There was no readable source manifest, so discovered constructs were never accounted before apply.
    public const string ConstructAccountingNotExecuted = "fidelity.construct-accounting.not-executed";

    public const string ServiceTypeUnsupported = "fidelity.service.type-unsupported";

    public const string ServiceResourceUnselected = "fidelity.service.resource-unselected";

    // A selected layer or table is absent from the source manifest, so its constructs were never accounted.
    public const string ServiceResourceUndiscovered = "fidelity.service.resource-undiscovered";

    public const string ConstructUnsupported = "fidelity.construct.unsupported";

    public const string ConstructReviewRequired = "fidelity.construct.review-required";

    // The source count does not equal imported rows plus failed rows, so the transfer did not account for every record.
    public const string TransferCountMismatch = "fidelity.transfer.count-mismatch";

    public const string GeometryUnconverted = "fidelity.geometry.unconverted";

    // Replacing a null or invalid source geometry is neither transfer loss nor a successful repair, so parity is not proven.
    public const string GeometrySourceNotRepaired = "fidelity.geometry.source-not-repaired";
}

public sealed record MigrationFidelityDifference
{
    public required string Code { get; init; }

    public required string Severity { get; init; }

    public string? Subject { get; init; }

    public string? Expected { get; init; }

    public string? Actual { get; init; }

    public required string Summary { get; init; }
}
