// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;

namespace Honua.Core.Features.Migration.Services;

public sealed record MigrationFidelityEvaluationInput
{
    public string? LayerName { get; init; }

    public MigrationReconciliationArtifact? DataReconciliation { get; init; }

    // A non-null probe result does not mean the probe ran; this flag separates a pass from a probe that never ran.
    public bool DataReconciliationExecuted { get; init; }

    public MigrationCatalogReconciliationReport? CatalogReconciliation { get; init; }

    public bool CatalogReconciliationExecuted { get; init; }

    // An unpublished import cannot be full fidelity, because data-movement reconciliation is reported as not executed when the probe did not run.
    public bool PublishedTarget { get; init; }

    public bool PublishRequested { get; init; }

    public int FailedFeatures { get; init; }

    public int FeaturesProcessed { get; init; }

    public long? SourceCountBeforeTransfer { get; init; }

    // The source geometry could not be converted, so the feature was stored with a null geometry.
    public int UnconvertedGeometryCount { get; init; }

    public MigrationFidelityAttachmentInput? Attachments { get; init; }

    public MigrationRelationshipApplyOutcome[] Relationships { get; init; } = [];

    public MigrationFidelitySourceSnapshotInput? SourceSnapshot { get; init; }
}

public sealed record MigrationFidelitySourceSnapshotInput
{
    public long? CountBeforeTransfer { get; init; }

    public long? CountAfterTransfer { get; init; }

    public bool MembershipChanged { get; init; }
}

// Attachment facts are captured apart from the feature count, so matching feature counts cannot pass the attachment check.
public sealed record MigrationFidelityAttachmentInput
{
    public int Advertised { get; init; }

    public int Copied { get; init; }

    public int Failed { get; init; }

    // Attachments that could not be listed are unverified, not failed and not passed.
    public int UnverifiedParents { get; init; }

    public bool TargetUnverified { get; init; }

    // The source advertised attachments, but the copy never ran, so the check is not a pass.
    public bool CopySkipped { get; init; }
}

public sealed record MigrationFidelityEvaluation
{
    public required string Verdict { get; init; }

    public MigrationFidelityDifference[] Differences { get; init; } = [];

    public bool IsBlocking => string.Equals(Verdict, MigrationFidelityVerdicts.Incomplete, StringComparison.Ordinal);

    public string? BlockingReason { get; init; }
}

public static class MigrationFidelityEvaluator
{
    public static MigrationFidelityEvaluation Evaluate(MigrationFidelityEvaluationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var differences = new List<MigrationFidelityDifference>();
        var subject = string.IsNullOrWhiteSpace(input.LayerName) ? "layer" : input.LayerName!;

        CollectPublish(input, subject, differences);
        CollectRecordLoss(input, subject, differences);
        CollectInProcessTransfer(input, subject, differences);
        CollectSourceSnapshot(input, subject, differences);
        CollectAttachmentDifferences(input, differences);
        CollectRelationshipOmissions(input, differences);
        CollectDataReconciliation(input, subject, differences);
        CollectSourceGeometryRewrites(input, subject, differences);
        CollectCatalogReconciliation(input, subject, differences);

        return Fold(differences);
    }

    internal static MigrationFidelityEvaluation Fold(IEnumerable<MigrationFidelityDifference> differences)
    {
        var ordered = differences
            .OrderBy(static difference => difference.Code, StringComparer.Ordinal)
            .ThenBy(static difference => difference.Subject ?? string.Empty, StringComparer.Ordinal)
            .ToArray();

        var blocking = ordered
            .Where(static difference => string.Equals(
                difference.Severity,
                MigrationFidelityDifferenceSeverities.Blocking,
                StringComparison.Ordinal))
            .ToArray();

        if (blocking.Length > 0)
        {
            return new MigrationFidelityEvaluation
            {
                Verdict = MigrationFidelityVerdicts.Incomplete,
                Differences = ordered,
                BlockingReason =
                    $"Migration is not full fidelity: {Format(blocking.Length)} blocking difference(s). "
                    + string.Join(" ", blocking.Select(static difference => difference.Summary))
            };
        }

        var verdict = ordered.Length > 0
            ? MigrationFidelityVerdicts.Unverified
            : MigrationFidelityVerdicts.FullFidelity;

        return new MigrationFidelityEvaluation
        {
            Verdict = verdict,
            Differences = ordered
        };
    }

    private static void CollectPublish(
        MigrationFidelityEvaluationInput input,
        string subject,
        List<MigrationFidelityDifference> differences)
    {
        // Without a published target the post-publish probes are not applicable, so nothing else reports
        // them. When the publish was requested, that absence is itself the omission.
        if (!input.PublishRequested || input.PublishedTarget)
        {
            return;
        }

        differences.Add(new MigrationFidelityDifference
        {
            Code = MigrationFidelityDifferenceCodes.PublishNotCompleted,
            Severity = MigrationFidelityDifferenceSeverities.Blocking,
            Subject = subject,
            Expected = "target layer published",
            Actual = "not published",
            Summary =
                "The import was asked to publish its target layer, but no layer was published; the migrated data is "
                + "not served, and record counts, geometry, extent and catalog parity were never checked against a target."
        });
    }

    private static void CollectRecordLoss(
        MigrationFidelityEvaluationInput input,
        string subject,
        List<MigrationFidelityDifference> differences)
    {
        if (input.FailedFeatures <= 0)
        {
            return;
        }

        differences.Add(new MigrationFidelityDifference
        {
            Code = MigrationFidelityDifferenceCodes.RecordsLost,
            Severity = MigrationFidelityDifferenceSeverities.Blocking,
            Subject = subject,
            Expected = "0 dropped source records",
            Actual = Format(input.FailedFeatures) + " dropped source records",
            Summary =
                $"{Format(input.FailedFeatures)} source record(s) were read but did not land in the target table; "
                + "the migrated layer holds strictly less data than the source."
        });
    }

    private static void CollectInProcessTransfer(
        MigrationFidelityEvaluationInput input,
        string subject,
        List<MigrationFidelityDifference> differences)
    {
        if (input.SourceCountBeforeTransfer is { } expected)
        {
            var accounted = (long)input.FeaturesProcessed + input.FailedFeatures;
            if (accounted != expected)
            {
                differences.Add(new MigrationFidelityDifference
                {
                    Code = MigrationFidelityDifferenceCodes.TransferCountMismatch,
                    Severity = MigrationFidelityDifferenceSeverities.Blocking,
                    Subject = subject,
                    Expected = Format(expected) + " source records",
                    Actual = Format(accounted) + " records accounted for (imported plus failed)",
                    Summary =
                        $"The transfer accounted for {Format(accounted)} record(s) but the source count before "
                        + $"the transfer was {Format(expected)}; the imported table is not a complete copy of that source."
                });
            }
        }

        if (input.UnconvertedGeometryCount > 0)
        {
            differences.Add(new MigrationFidelityDifference
            {
                Code = MigrationFidelityDifferenceCodes.GeometryUnconverted,
                Severity = MigrationFidelityDifferenceSeverities.Blocking,
                Subject = subject,
                Expected = "every imported feature keeps its source geometry",
                Actual = Format(input.UnconvertedGeometryCount) + " features stored with a null geometry",
                Summary =
                    $"{Format(input.UnconvertedGeometryCount)} feature(s) were saved with a null geometry because "
                    + "the source geometry could not be converted."
            });
        }
    }

    private static void CollectSourceSnapshot(
        MigrationFidelityEvaluationInput input,
        string subject,
        List<MigrationFidelityDifference> differences)
    {
        if (input.SourceSnapshot is not { } snapshot)
        {
            return;
        }

        if (snapshot.CountBeforeTransfer is not { } before || snapshot.CountAfterTransfer is not { } after)
        {
            var missing = snapshot.CountBeforeTransfer is null ? "before" : "after";
            differences.Add(new MigrationFidelityDifference
            {
                Code = MigrationFidelityDifferenceCodes.SourceSnapshotUnverified,
                Severity = MigrationFidelityDifferenceSeverities.Unverified,
                Subject = subject,
                Expected = "source record count read before and after the transfer",
                Actual = $"source record count unavailable {missing} the transfer",
                Summary =
                    $"The source record count could not be read {missing} the transfer, so changes made to the "
                    + "source while it was being copied cannot be ruled out."
            });
            return;
        }

        if (before == after && !snapshot.MembershipChanged)
        {
            return;
        }

        differences.Add(new MigrationFidelityDifference
        {
            Code = MigrationFidelityDifferenceCodes.SourceChangedDuringTransfer,
            Severity = MigrationFidelityDifferenceSeverities.Blocking,
            Subject = subject,
            Expected = Format(before) + " source records when the transfer started",
            Actual = Format(after) + " source records when the transfer finished"
                + (before == after ? " (different object IDs)" : string.Empty),
            Summary =
                "The source changed while it was being copied, so the target is not a consistent snapshot of "
                + "the source: records added or removed mid-transfer may be missing or retained. Re-run the "
                + "import once the source is quiescent."
        });
    }

    private static void CollectAttachmentDifferences(
        MigrationFidelityEvaluationInput input,
        List<MigrationFidelityDifference> differences)
    {
        if (input.Attachments is not { } attachments)
        {
            return;
        }

        if (attachments.CopySkipped)
        {
            differences.Add(new MigrationFidelityDifference
            {
                Code = MigrationFidelityDifferenceCodes.AttachmentsUnverified,
                Severity = MigrationFidelityDifferenceSeverities.Unverified,
                Subject = "attachments",
                Expected = "attachment inventory reconciled against the source",
                Actual = "attachment copy did not run",
                Summary =
                    "The source layer advertises attachments but the attachment copy step did not run, "
                    + "so attachment parity was never established."
            });
            return;
        }

        if (attachments.Failed > 0)
        {
            differences.Add(new MigrationFidelityDifference
            {
                Code = MigrationFidelityDifferenceCodes.AttachmentsLost,
                Severity = MigrationFidelityDifferenceSeverities.Blocking,
                Subject = "attachments",
                Expected = Format(attachments.Advertised) + " attachments in the target store",
                Actual = Format(attachments.Copied) + " attachments in the target store",
                Summary =
                    $"{Format(attachments.Failed)} source attachment(s) advertised by the layer are missing from the "
                    + "target attachment store; feature counts can still match, so this is reconciled independently."
            });
        }
        else if (attachments.Copied < attachments.Advertised)
        {
            // Defensive: the copy loop reported neither a success nor a failure for some advertised
            // attachment. Treat the shortfall as loss rather than letting it pass silently.
            differences.Add(new MigrationFidelityDifference
            {
                Code = MigrationFidelityDifferenceCodes.AttachmentsLost,
                Severity = MigrationFidelityDifferenceSeverities.Blocking,
                Subject = "attachments",
                Expected = Format(attachments.Advertised) + " attachments in the target store",
                Actual = Format(attachments.Copied) + " attachments in the target store",
                Summary =
                    $"The source advertised {Format(attachments.Advertised)} attachment(s) but only "
                    + $"{Format(attachments.Copied)} reached the target store and none were reported as failures."
            });
        }

        if (attachments.TargetUnverified)
        {
            differences.Add(new MigrationFidelityDifference
            {
                Code = MigrationFidelityDifferenceCodes.AttachmentsUnverified,
                Severity = MigrationFidelityDifferenceSeverities.Blocking,
                Subject = "attachments",
                Expected = "target attachment inventory reconciled against the source",
                Actual = "target attachment inventory requires review",
                Summary = "Retained attachments or an incomplete reconciliation prevent verifying the target attachment inventory."
            });
        }

        if (attachments.UnverifiedParents > 0)
        {
            differences.Add(new MigrationFidelityDifference
            {
                Code = MigrationFidelityDifferenceCodes.AttachmentsUnverified,
                Severity = MigrationFidelityDifferenceSeverities.Unverified,
                Subject = "attachments",
                Expected = "attachment inventory read for every imported feature",
                Actual = Format(attachments.UnverifiedParents) + " features with an unreadable attachment inventory",
                Summary =
                    $"The source attachment inventory could not be read for {Format(attachments.UnverifiedParents)} "
                    + "imported feature(s), so the advertised attachment total is unknown for those features."
            });
        }
    }

    private static void CollectRelationshipOmissions(
        MigrationFidelityEvaluationInput input,
        List<MigrationFidelityDifference> differences)
    {
        foreach (var outcome in input.Relationships)
        {
            // Only genuinely deferred relationships are omissions. A successful write and an
            // idempotent re-apply both carry a message and both report AlreadyExists, so the
            // explicit Deferred flag is the only sound discriminator.
            if (!outcome.Deferred)
            {
                continue;
            }

            differences.Add(new MigrationFidelityDifference
            {
                Code = MigrationFidelityDifferenceCodes.RelationshipOmitted,
                Severity = MigrationFidelityDifferenceSeverities.Blocking,
                Subject = outcome.SourceRelationshipId,
                Expected = "relationship persisted onto the target",
                Actual = "relationship deferred",
                Summary =
                    $"Relationship {Quote(outcome.SourceRelationshipId)} was discovered on the source but not "
                    + $"persisted onto the target: {outcome.Message}"
            });
        }
    }

    private static void CollectDataReconciliation(
        MigrationFidelityEvaluationInput input,
        string subject,
        List<MigrationFidelityDifference> differences)
    {
        if (!input.DataReconciliationExecuted || input.DataReconciliation is null)
        {
            // An unpublished import has no post-publish probe, but skipping the difference let it
            // fold to full fidelity with no in-process evidence. Catalog reconciliation still
            // requires a published entry and is not invented here.
            differences.Add(new MigrationFidelityDifference
            {
                Code = MigrationFidelityDifferenceCodes.DataReconciliationNotExecuted,
                Severity = MigrationFidelityDifferenceSeverities.Unverified,
                Subject = subject,
                Expected = input.PublishedTarget
                    ? "data-movement reconciliation executed against the published layer"
                    : "data-movement reconciliation executed, or the import left unpublished only after in-process checks",
                Actual = "not executed",
                Summary = input.PublishedTarget
                    ? "The published layer was never reconciled against the source snapshot, so record counts, "
                        + "geometry validity and extent parity are unproven."
                    : "Data-movement reconciliation did not run. An unpublished import is not full fidelity; "
                        + "record counts and geometry conversion are unproven beyond the in-process checks."
            });

            return;
        }

        var artifact = input.DataReconciliation;
        if (string.Equals(artifact.Classification, MigrationReconciliationClassifications.Fail, StringComparison.Ordinal))
        {
            differences.Add(new MigrationFidelityDifference
            {
                Code = MigrationFidelityDifferenceCodes.DataReconciliationFailed,
                Severity = MigrationFidelityDifferenceSeverities.Blocking,
                Subject = subject,
                Expected = MigrationReconciliationClassifications.Pass,
                Actual = artifact.Classification,
                Summary =
                    $"Data-movement reconciliation reported {Format(artifact.Summary.FailCount)} blocking finding(s)"
                    + (artifact.Reasons.Length > 0 ? ": " + string.Join(" ", artifact.Reasons) : ".")
            });
            return;
        }

        if (string.Equals(artifact.Classification, MigrationReconciliationClassifications.Skipped, StringComparison.Ordinal))
        {
            differences.Add(new MigrationFidelityDifference
            {
                Code = MigrationFidelityDifferenceCodes.DataReconciliationNotExecuted,
                Severity = MigrationFidelityDifferenceSeverities.Unverified,
                Subject = subject,
                Expected = "data-movement reconciliation executed against the published layer",
                Actual = MigrationReconciliationClassifications.Skipped,
                Summary =
                    "Data-movement reconciliation was skipped for this layer, so record counts, geometry validity "
                    + "and extent parity are unproven."
            });
        }
    }

    private static void CollectSourceGeometryRewrites(
        MigrationFidelityEvaluationInput input,
        string subject,
        List<MigrationFidelityDifference> differences)
    {
        var artifact = input.DataReconciliation;
        if (artifact is null)
        {
            return;
        }

        var rewrites = 0;
        foreach (var layer in artifact.Layers)
        {
            rewrites += layer.Geometry.SourceDefectRewrites;
        }

        if (rewrites <= 0)
        {
            return;
        }

        differences.Add(new MigrationFidelityDifference
        {
            Code = MigrationFidelityDifferenceCodes.GeometrySourceNotRepaired,
            Severity = MigrationFidelityDifferenceSeverities.Unverified,
            Subject = subject,
            Expected = "null or invalid source geometry stays visible and is not described as repaired",
            Actual = Format(rewrites) + " well-formed target geometries replace a null or invalid source geometry",
            Summary =
                $"{Format(rewrites)} feature(s) stored a well-formed geometry where the source geometry was null or "
                + "invalid. That is not transfer loss and it is not a successful repair."
        });
    }

    private static void CollectCatalogReconciliation(
        MigrationFidelityEvaluationInput input,
        string subject,
        List<MigrationFidelityDifference> differences)
    {
        if (!input.CatalogReconciliationExecuted || input.CatalogReconciliation is null)
        {
            if (input.PublishedTarget)
            {
                differences.Add(new MigrationFidelityDifference
                {
                    Code = MigrationFidelityDifferenceCodes.CatalogReconciliationNotExecuted,
                    Severity = MigrationFidelityDifferenceSeverities.Unverified,
                    Subject = subject,
                    Expected = "catalog reconciliation executed against the published catalog entry",
                    Actual = "not executed",
                    Summary =
                        "The published catalog entry was never reconciled against the source service definition, so "
                        + "schema, domain, identifier and subtype parity are unproven. Counts can match while the "
                        + "schema is wrong."
                });
            }

            return;
        }

        foreach (var resource in input.CatalogReconciliation.Resources)
        {
            foreach (var finding in resource.Findings)
            {
                if (!string.Equals(
                        finding.Severity,
                        MigrationCatalogReconciliationSeverities.Fail,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                differences.Add(new MigrationFidelityDifference
                {
                    Code = MigrationFidelityDifferenceCodes.CatalogReconciliationFailed,
                    Severity = MigrationFidelityDifferenceSeverities.Blocking,
                    Subject = string.IsNullOrWhiteSpace(finding.Subject)
                        ? resource.SourceResourceId
                        : $"{resource.SourceResourceId}:{finding.Subject}",
                    Expected = finding.Expected,
                    Actual = finding.Actual,
                    Summary = $"[{finding.Code}] {finding.Summary}"
                });
            }

            if (string.Equals(
                    resource.Classification,
                    MigrationCatalogReconciliationClassifications.NotApplicable,
                    StringComparison.Ordinal))
            {
                differences.Add(new MigrationFidelityDifference
                {
                    Code = MigrationFidelityDifferenceCodes.CatalogReconciliationNotExecuted,
                    Severity = MigrationFidelityDifferenceSeverities.Unverified,
                    Subject = resource.SourceResourceId,
                    Expected = "catalog reconciliation executed against the published catalog entry",
                    Actual = MigrationCatalogReconciliationClassifications.NotApplicable,
                    Summary =
                        $"Resource {Quote(resource.SourceResourceId)} had no published catalog entry to reconcile "
                        + "against, so its schema and metadata parity are unproven."
                });
            }
        }
    }

    private static string Quote(string value) => "'" + value + "'";

    private static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);
}
