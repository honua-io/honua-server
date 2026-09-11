// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Domain;

/// <summary>
/// Outcome of the pre-mutation change policy for a metadata-release plan.
/// </summary>
public sealed record MetadataReleaseChangePolicyResult
{
    /// <summary>
    /// Whether the plan may proceed to mutation.
    /// </summary>
    public bool IsAllowed => Blockers.Count == 0;

    /// <summary>
    /// Stable blocker codes.
    /// </summary>
    public IReadOnlyList<string> Blockers { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Operator-facing reasons, one per blocker.
    /// </summary>
    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Protected-change policy evaluated before any mutation. The staged-activation lifecycle can only
/// promise verified recovery for changes it can revert without restoring data, so it admits only
/// nullable additive field changes whose inverse drops exactly what the release adds, and an ETL
/// workload only when it writes nothing but those new nullable fields (old readers and writers
/// never depend on them). Everything else is rejected before the first write.
/// </summary>
public static class MetadataReleaseChangePolicy
{
    /// <summary>Forward script contains a non-additive (destructive) operation.</summary>
    public const string DestructiveChange = "metadata-release-destructive-change";

    /// <summary>Forward script adds a non-nullable field, which breaks old writers.</summary>
    public const string NonNullableAdd = "metadata-release-non-nullable-add";

    /// <summary>Script is not reversible or declares no forward operations.</summary>
    public const string NotReversible = "metadata-release-not-reversible";

    /// <summary>Declared inverse drops a field the release does not add.</summary>
    public const string InverseNotOwned = "metadata-release-inverse-not-owned";

    /// <summary>ETL workload writes data that the release cannot revert without compensation.</summary>
    public const string EtlUnprovenCompensation = "metadata-release-etl-unproven-compensation";

    /// <summary>
    /// Evaluates <paramref name="plan"/> against the protected-change policy.
    /// </summary>
    public static MetadataReleaseChangePolicyResult Evaluate(MetadataReleaseExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var blockers = new List<string>();
        var reasons = new List<string>();
        void Block(string code, string reason)
        {
            if (!blockers.Contains(code, StringComparer.Ordinal))
            {
                blockers.Add(code);
                reasons.Add(reason);
            }
        }

        var forward = plan.Script.ForwardOperations;
        if (!plan.Script.Reversible || forward.Count == 0)
        {
            Block(NotReversible, $"Script '{plan.Script.ScriptId}' is not a reversible additive change.");
        }

        foreach (var operation in forward)
        {
            if (operation.Kind != MetadataReleaseScriptOperationKind.AddColumn)
            {
                Block(DestructiveChange, $"Forward operation {operation.Kind} on '{operation.ResourceSemanticId}.{operation.FieldName}' is destructive; only additive field changes are admitted.");
            }
            else if (!operation.Nullable)
            {
                Block(NonNullableAdd, $"Field '{operation.ResourceSemanticId}.{operation.FieldName}' is added as non-nullable; old writers could no longer insert rows.");
            }
        }

        var added = forward
            .Where(static operation => operation.Kind == MetadataReleaseScriptOperationKind.AddColumn && operation.Nullable)
            .Select(static operation => (operation.ResourceSemanticId, Field: operation.FieldName.ToUpperInvariant()))
            .ToHashSet();

        var unownedInverses = plan.Script.InverseOperations.Where(inverse =>
            inverse.Kind != MetadataReleaseScriptOperationKind.DropColumn ||
            !added.Contains((inverse.ResourceSemanticId, inverse.FieldName.ToUpperInvariant())));
        foreach (var inverse in unownedInverses)
        {
            Block(InverseNotOwned, $"Inverse operation {inverse.Kind} on '{inverse.ResourceSemanticId}.{inverse.FieldName}' reverts something the release does not add.");
        }

        if (!string.IsNullOrWhiteSpace(plan.DataPopulateWorkloadId))
        {
            if (plan.DataPopulateFields.Count == 0)
            {
                Block(EtlUnprovenCompensation, $"Data-populate workload '{plan.DataPopulateWorkloadId}' does not declare the fields it writes, so rollback cannot be proven to need no data compensation.");
            }

            var unqualifiedFields = plan.DataPopulateFields.Where(field =>
                !added.Contains((plan.ResourceSemanticId, field.ToUpperInvariant())));
            foreach (var field in unqualifiedFields)
            {
                Block(EtlUnprovenCompensation, $"Data-populate workload '{plan.DataPopulateWorkloadId}' writes '{plan.ResourceSemanticId}.{field}', which this release does not add as a nullable field.");
            }
        }

        return new MetadataReleaseChangePolicyResult { Blockers = blockers, Reasons = reasons };
    }
}
