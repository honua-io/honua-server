// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// Inputs the fidelity evaluator folds into a single migration verdict (issue #4600). Every field
/// is a fact captured by the import pipeline; the evaluator itself performs no I/O so the verdict
/// is deterministic and replayable from persisted evidence.
/// </summary>
public sealed record MigrationFidelityEvaluationInput
{
    /// <summary>Source layer display name, used as the default difference subject.</summary>
    public string? LayerName { get; init; }

    /// <summary>
    /// Data-movement reconciliation artifact, or <c>null</c> when the probe did not execute.
    /// </summary>
    public MigrationReconciliationArtifact? DataReconciliation { get; init; }

    /// <summary>
    /// True when the data-movement reconciliation probe was actually run. Distinguishes "ran and
    /// passed" from "never ran", which <see cref="DataReconciliation"/> being non-null cannot.
    /// </summary>
    public bool DataReconciliationExecuted { get; init; }

    /// <summary>
    /// Catalog (schema/domain/identifier/subtype/relationship) reconciliation report, or
    /// <c>null</c> when the pass did not execute.
    /// </summary>
    public MigrationCatalogReconciliationReport? CatalogReconciliation { get; init; }

    /// <summary>True when the catalog reconciliation pass was actually run.</summary>
    public bool CatalogReconciliationExecuted { get; init; }

    /// <summary>
    /// True when the run published a queryable target layer. When the run did not publish there is
    /// nothing for the post-publish probes to reconcile against, so they are reported as not
    /// executed only when a target actually exists.
    /// </summary>
    public bool PublishedTarget { get; init; }

    /// <summary>Number of source records read that failed to land in the target table.</summary>
    public int FailedFeatures { get; init; }

    /// <summary>Attachment copy outcome, or <c>null</c> when the source advertises no attachments.</summary>
    public MigrationFidelityAttachmentInput? Attachments { get; init; }

    /// <summary>
    /// Relationship apply outcomes for the run. Deferred/skipped relationships are blocking
    /// omissions: a migration that silently drops a relationship is not full fidelity.
    /// </summary>
    public MigrationRelationshipApplyOutcome[] Relationships { get; init; } = [];
}

/// <summary>
/// Attachment-side facts, captured independently of the feature count so a run cannot pass the
/// attachment check merely because the feature counts matched (issue #4600 acceptance criterion 6).
/// </summary>
public sealed record MigrationFidelityAttachmentInput
{
    /// <summary>Attachments the source advertised across every parent feature that was probed.</summary>
    public int Advertised { get; init; }

    /// <summary>Attachments whose bytes were successfully written to the Honua attachment store.</summary>
    public int Copied { get; init; }

    /// <summary>Attachments that were advertised but could not be copied.</summary>
    public int Failed { get; init; }

    /// <summary>
    /// Parent features whose attachment inventory could never be read (the source metadata query
    /// failed). Their attachments are neither copied nor counted as failed, so the attachment total
    /// is unknown and the check is unverified rather than passing.
    /// </summary>
    public int UnverifiedParents { get; init; }

    /// <summary>
    /// True when the source layer advertised attachments but the copy step never ran (disabled by
    /// request, or no attachment store registered).
    /// </summary>
    public bool CopySkipped { get; init; }
}

/// <summary>
/// The unified migration verdict: one classification plus the per-resource differences that
/// produced it.
/// </summary>
public sealed record MigrationFidelityEvaluation
{
    /// <summary>One of <see cref="MigrationFidelityVerdicts"/>.</summary>
    public required string Verdict { get; init; }

    /// <summary>
    /// Differences ordered deterministically by (code, subject) so persisted evidence and test
    /// assertions are stable.
    /// </summary>
    public MigrationFidelityDifference[] Differences { get; init; } = [];

    /// <summary>
    /// True when the run must be routed to operator review instead of being reported as a
    /// successful migration.
    /// </summary>
    public bool IsBlocking => string.Equals(Verdict, MigrationFidelityVerdicts.Incomplete, StringComparison.Ordinal);

    /// <summary>
    /// Operator-visible summary of the blocking differences, or <c>null</c> when nothing blocks.
    /// </summary>
    public string? BlockingReason { get; init; }
}

/// <summary>
/// Folds the data-movement reconciliation artifact, the catalog reconciliation report, the
/// record/attachment loss counters and the relationship apply outcomes into a single migration
/// verdict (issue #4600, acceptance criterion 3).
/// </summary>
/// <remarks>
/// Before this evaluator the two reconciliation passes disagreed by construction: the data artifact
/// alone decided <c>Completed</c> vs <c>NeedsReview</c>, while catalog findings — the pass that
/// catches "counts match but the schema is wrong" — were recorded and then ignored. Lost records,
/// lost attachments and deferred relationships were likewise only warnings. A job could therefore
/// report success while being a strictly weaker migration than the source service.
/// </remarks>
public static class MigrationFidelityEvaluator
{
    /// <summary>
    /// Evaluates the unified verdict. Pure and deterministic: identical inputs always yield an
    /// identical verdict and an identically ordered difference set.
    /// </summary>
    /// <param name="input">Facts captured by the import pipeline.</param>
    /// <returns>The unified verdict and its supporting differences.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is <c>null</c>.</exception>
    public static MigrationFidelityEvaluation Evaluate(MigrationFidelityEvaluationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var differences = new List<MigrationFidelityDifference>();
        var subject = string.IsNullOrWhiteSpace(input.LayerName) ? "layer" : input.LayerName!;

        CollectRecordLoss(input, subject, differences);
        CollectAttachmentDifferences(input, differences);
        CollectRelationshipOmissions(input, differences);
        CollectDataReconciliation(input, subject, differences);
        CollectCatalogReconciliation(input, subject, differences);

        return Fold(differences);
    }

    /// <summary>
    /// Orders differences deterministically and folds them into a verdict: any blocking difference
    /// is <see cref="MigrationFidelityVerdicts.Incomplete"/>, any other difference is
    /// <see cref="MigrationFidelityVerdicts.Unverified"/>, none is
    /// <see cref="MigrationFidelityVerdicts.FullFidelity"/>. Shared with the service-level fold in
    /// <see cref="MigrationBatchFidelityEvaluator"/> so both levels classify identically.
    /// </summary>
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
            if (input.PublishedTarget)
            {
                differences.Add(new MigrationFidelityDifference
                {
                    Code = MigrationFidelityDifferenceCodes.DataReconciliationNotExecuted,
                    Severity = MigrationFidelityDifferenceSeverities.Unverified,
                    Subject = subject,
                    Expected = "data-movement reconciliation executed against the published layer",
                    Actual = "not executed",
                    Summary =
                        "The published layer was never reconciled against the source snapshot, so record counts, "
                        + "geometry validity and extent parity are unproven."
                });
            }

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
}
