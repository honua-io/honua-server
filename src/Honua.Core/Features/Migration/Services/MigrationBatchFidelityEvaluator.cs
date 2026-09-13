// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// Facts the service-level fidelity fold consumes (issue #4600). Every field is already persisted on
/// the batch and its children, so the verdict is replayable without re-running any import.
/// </summary>
public sealed record MigrationBatchFidelityInput
{
    /// <summary>Every child layer in the batch footprint, in execution order.</summary>
    public IReadOnlyList<MigrationBatchChildRecord> Children { get; init; } = [];

    /// <summary>True when the batch asked for manifest relationships to be applied.</summary>
    public bool RelationshipApplyRequested { get; init; }

    /// <summary>True when relationship apply actually ran and returned outcomes.</summary>
    public bool RelationshipApplyExecuted { get; init; }

    /// <summary>Why relationship apply did not run, when it was requested but did not execute.</summary>
    public string? RelationshipApplyNotExecutedReason { get; init; }

    /// <summary>Relationship apply outcomes, when apply executed.</summary>
    public MigrationRelationshipApplyOutcome[] Relationships { get; init; } = [];
}

/// <summary>
/// Folds a service (batch) import into one fidelity verdict (issue #4600, acceptance criteria 2 and
/// 3). A service migration is full fidelity only when every layer completed at full fidelity and
/// every requested relationship was persisted onto the target.
/// </summary>
/// <remarks>
/// Before this fold a batch rolled up child <em>statuses</em> only: a layer that completed without
/// its reconciliation checks counted as a success, and relationship apply could defer every
/// relationship (or never run because the manifest did not parse) while the batch still reported
/// <c>succeeded</c>.
/// </remarks>
public static class MigrationBatchFidelityEvaluator
{
    /// <summary>
    /// Evaluates the service-level verdict. Pure and deterministic.
    /// </summary>
    /// <param name="input">Batch facts.</param>
    /// <returns>The service-level verdict and its supporting differences.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is <c>null</c>.</exception>
    public static MigrationFidelityEvaluation Evaluate(MigrationBatchFidelityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var differences = new List<MigrationFidelityDifference>();
        foreach (var child in input.Children)
        {
            CollectChild(child, differences);
        }

        if (input.RelationshipApplyRequested && !input.RelationshipApplyExecuted)
        {
            differences.Add(new MigrationFidelityDifference
            {
                Code = MigrationFidelityDifferenceCodes.RelationshipApplyNotExecuted,
                Severity = MigrationFidelityDifferenceSeverities.Blocking,
                Subject = "relationships",
                Expected = "manifest relationships applied to the target",
                Actual = "relationship apply did not run",
                Summary = "Relationship apply was requested but did not run, so the manifest relationships are "
                    + "missing from the target"
                    + (string.IsNullOrWhiteSpace(input.RelationshipApplyNotExecutedReason)
                        ? "."
                        : ": " + input.RelationshipApplyNotExecutedReason)
            });
        }

        // Relationship omissions are classified by the per-layer evaluator so a deferred relationship
        // reads identically whether it surfaced on a single-layer or a service import.
        differences.AddRange(MigrationFidelityEvaluator.Evaluate(new MigrationFidelityEvaluationInput
        {
            Relationships = input.Relationships
        }).Differences);

        return MigrationFidelityEvaluator.Fold(differences);
    }

    private static void CollectChild(MigrationBatchChildRecord child, List<MigrationFidelityDifference> differences)
    {
        if (child.Status == MigrationBatchChildStatus.Succeeded)
        {
            if (string.Equals(child.FidelityVerdict, MigrationFidelityVerdicts.FullFidelity, StringComparison.Ordinal))
            {
                return;
            }

            if (!string.Equals(child.FidelityVerdict, MigrationFidelityVerdicts.Incomplete, StringComparison.Ordinal))
            {
                differences.Add(new MigrationFidelityDifference
                {
                    Code = MigrationFidelityDifferenceCodes.ServiceLayerUnverified,
                    Severity = MigrationFidelityDifferenceSeverities.Unverified,
                    Subject = child.SourceResourceId,
                    Expected = MigrationFidelityVerdicts.FullFidelity,
                    Actual = child.FidelityVerdict ?? "no verdict recorded",
                    Summary = $"Layer '{child.SourceResourceId}' completed, but its import did not prove full fidelity "
                        + $"(verdict: {child.FidelityVerdict ?? "none recorded"}); see the job's fidelity differences."
                });
                return;
            }
        }

        var actual = StatusText(child.Status)
            + (string.IsNullOrWhiteSpace(child.FidelityVerdict) ? string.Empty : $" ({child.FidelityVerdict})");
        differences.Add(new MigrationFidelityDifference
        {
            Code = MigrationFidelityDifferenceCodes.ServiceLayerIncomplete,
            Severity = MigrationFidelityDifferenceSeverities.Blocking,
            Subject = child.SourceResourceId,
            Expected = "layer imported at " + MigrationFidelityVerdicts.FullFidelity,
            Actual = actual,
            Summary = $"Layer '{child.SourceResourceId}' did not complete at full fidelity: {actual}"
                + (string.IsNullOrWhiteSpace(child.StatusNote) ? "." : $". {child.StatusNote}")
        });
    }

    private static string StatusText(MigrationBatchChildStatus status) => status switch
    {
        MigrationBatchChildStatus.Pending => "pending",
        MigrationBatchChildStatus.Running => "running",
        MigrationBatchChildStatus.Succeeded => "succeeded",
        MigrationBatchChildStatus.Failed => "failed",
        MigrationBatchChildStatus.NeedsReview => "needs-review",
        MigrationBatchChildStatus.Cancelled => "cancelled",
        _ => "unknown"
    };
}
