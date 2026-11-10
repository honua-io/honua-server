// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;

namespace Honua.Core.Features.Migration.Services;

public sealed record MigrationBatchFidelityInput
{
    public IReadOnlyList<MigrationBatchChildRecord> Children { get; init; } = [];

    public bool RelationshipApplyRequested { get; init; }

    public bool RelationshipApplyExecuted { get; init; }

    public string? RelationshipApplyNotExecutedReason { get; init; }

    public MigrationRelationshipApplyOutcome[] Relationships { get; init; } = [];

    public MigrationConstructAccountingReport? ConstructAccounting { get; init; }
}

// Full fidelity requires every layer at full fidelity and every requested relationship persisted; a completed status alone is not enough.
public static class MigrationBatchFidelityEvaluator
{
    public static MigrationFidelityEvaluation Evaluate(MigrationBatchFidelityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Every construct discovered on the source must be accounted against the selection before apply;
        // a service whose accounting never ran cannot be proven full fidelity.
        var differences = new List<MigrationFidelityDifference>(
            input.ConstructAccounting?.Differences
            ?? MigrationServiceConstructAccountant.NotExecuted("the batch recorded no construct accounting.").Differences);
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
        // Only relationship omissions belong to the batch fold. The per-layer evaluator also
        // records an unexecuted data probe when nothing else is supplied, which this call is not asking for.
        differences.AddRange(MigrationFidelityEvaluator.Evaluate(new MigrationFidelityEvaluationInput
        {
            Relationships = input.Relationships
        }).Differences.Where(static difference =>
            difference.Code == MigrationFidelityDifferenceCodes.RelationshipOmitted));

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
