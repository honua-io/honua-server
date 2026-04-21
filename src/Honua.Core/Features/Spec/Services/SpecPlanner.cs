// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Spec.Abstractions;
using Honua.Core.Features.Spec.Domain;

namespace Honua.Core.Features.Spec.Services;

/// <summary>
/// Default <see cref="ISpecPlanner"/> implementation. Validates DAG structure,
/// computes content hashes, and collects per-node cost estimates.
/// </summary>
internal sealed class SpecPlanner : ISpecPlanner
{
    private readonly ISpecCostEstimator _costEstimator;
    private readonly IEnumerable<ISpecResourceStateStore> _stateStores;

    public SpecPlanner(
        ISpecCostEstimator costEstimator,
        IEnumerable<ISpecResourceStateStore> stateStores)
    {
        ArgumentNullException.ThrowIfNull(costEstimator);
        ArgumentNullException.ThrowIfNull(stateStores);
        _costEstimator = costEstimator;
        _stateStores = stateStores;
    }

    public async Task<SpecPlan> PlanAsync(CanonicalSpecDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var resolution = SpecDagResolver.Resolve(document);
        var documentWarnings = new List<SpecWarning>(resolution.Diagnostics);

        if (resolution.HasFatalErrors)
        {
            return new SpecPlan
            {
                PlanId = Guid.NewGuid().ToString("n"),
                GrammarVersion = document.GrammarVersion,
                ProcessFamilyVersion = document.ProcessFamilyVersion,
                Nodes = [],
                Warnings = documentWarnings
            };
        }

        var nodesById = document.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var planNodesById = new Dictionary<string, SpecPlanNode>(StringComparer.Ordinal);
        var planNodes = new List<SpecPlanNode>(resolution.Order.Count);

        foreach (var nodeId in resolution.Order)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var node = nodesById[nodeId];
            var deps = resolution.Dependencies[nodeId];
            var nodeWarnings = new List<SpecWarning>();

            nodeWarnings.AddRange(EvaluateKindWarnings(node));
            nodeWarnings.AddRange(EvaluateSourceWarnings(node));

            var resolvedDependencies = deps.ToDictionary(
                id => id,
                id => planNodesById[id],
                StringComparer.Ordinal);

            var estimation = await _costEstimator.EstimateAsync(node, resolvedDependencies, cancellationToken)
                .ConfigureAwait(false);
            nodeWarnings.AddRange(estimation.Warnings);

            var inputHashes = resolvedDependencies.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.ContentHash,
                StringComparer.Ordinal);

            var contentHash = SpecContentHashCalculator.Compute(
                document.GrammarVersion,
                document.ProcessFamilyVersion,
                node,
                inputHashes);

            var planNode = new SpecPlanNode
            {
                NodeId = node.Id,
                Kind = node.Kind,
                Op = node.Op,
                DependsOn = deps,
                ContentHash = contentHash,
                Cost = estimation.Estimate,
                Warnings = nodeWarnings
            };

            planNodesById[nodeId] = planNode;
            planNodes.Add(planNode);
        }

        return new SpecPlan
        {
            PlanId = Guid.NewGuid().ToString("n"),
            GrammarVersion = document.GrammarVersion,
            ProcessFamilyVersion = document.ProcessFamilyVersion,
            Nodes = planNodes,
            Warnings = documentWarnings
        };
    }

    private IEnumerable<SpecWarning> EvaluateKindWarnings(CanonicalSpecNode node)
    {
        if (node.Kind is SpecResourceKind.Compute or SpecResourceKind.Report)
        {
            yield break;
        }

        var store = _stateStores.FirstOrDefault(s => s.Kind == node.Kind);
        if (store is null)
        {
            yield return new SpecWarning
            {
                Code = SpecDiagnosticCodes.SpecKindNotInS1,
                Message = $"Node kind '{node.Kind}' is reserved for a future release; apply will reject '{node.Id}'.",
                Severity = SpecDiagnosticSeverity.Warning,
                NodeId = node.Id,
                Remedy = "Restructure the spec to a compute/report node for the current release."
            };
        }
    }

    private static IEnumerable<SpecWarning> EvaluateSourceWarnings(CanonicalSpecNode node)
    {
        if (!IsMutableSource(node))
        {
            yield break;
        }

        if (node.SourcePins.Count > 0)
        {
            yield break;
        }

        yield return new SpecWarning
        {
            Code = SpecDiagnosticCodes.MutableSourceNoPin,
            Message = $"Node '{node.Id}' reads from a mutable source without a pinned version; cache entries degrade to TTL.",
            Severity = SpecDiagnosticSeverity.Warning,
            NodeId = node.Id,
            Remedy = "Pin the source via the '@version' hint or snapshot it explicitly."
        };
    }

    private static bool IsMutableSource(CanonicalSpecNode node)
    {
        return node.Parameters.TryGetValue("source.mutable", out var flag) &&
               string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);
    }
}
