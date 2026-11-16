// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Domain;

public sealed record MetadataReleaseChangePolicyResult
{
    public bool IsAllowed => Blockers.Count == 0;

    public IReadOnlyList<string> Blockers { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();
}

public static class MetadataReleaseChangePolicy
{
    public const string DestructiveChange = "metadata-release-destructive-change";

    public const string NonNullableAdd = "metadata-release-non-nullable-add";

    public const string NotReversible = "metadata-release-not-reversible";

    public const string InverseNotOwned = "metadata-release-inverse-not-owned";

    public const string EtlUnprovenCompensation = "metadata-release-etl-unproven-compensation";

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
