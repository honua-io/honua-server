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

    public SpecPlanner(ISpecCostEstimator costEstimator)
    {
        ArgumentNullException.ThrowIfNull(costEstimator);
        _costEstimator = costEstimator;
    }

    public async Task<SpecPlan> PlanAsync(CanonicalSpecDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var documentWarnings = new List<SpecWarning>();
        var versionSkew = EvaluateVersionSkew(document);
        if (versionSkew is not null)
        {
            documentWarnings.Add(versionSkew);
            return new SpecPlan
            {
                PlanId = Guid.NewGuid().ToString("n"),
                GrammarVersion = document.GrammarVersion,
                ProcessFamilyVersion = document.ProcessFamilyVersion,
                Nodes = [],
                Warnings = documentWarnings
            };
        }

        var resolution = SpecDagResolver.Resolve(document);
        documentWarnings.AddRange(resolution.Diagnostics);

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

    private static SpecWarning? EvaluateVersionSkew(CanonicalSpecDocument document)
    {
        // Missing grammar / process-family versions poison the content-hash
        // cache key (empty strings collide across unrelated specs). Reject at
        // the planner so both REST and gRPC translate to 400 / InvalidArgument
        // before the executor is invoked.
        var grammarBlank = string.IsNullOrWhiteSpace(document.GrammarVersion);
        var processBlank = string.IsNullOrWhiteSpace(document.ProcessFamilyVersion);
        if (!grammarBlank && !processBlank)
        {
            return null;
        }

        string message;
        string remedy;
        if (grammarBlank && processBlank)
        {
            message = "Spec is missing both 'grammarVersion' and 'processFamilyVersion'.";
            remedy = "Populate 'grammarVersion' and 'processFamilyVersion' on the canonical document.";
        }
        else if (grammarBlank)
        {
            message = "Spec is missing 'grammarVersion'.";
            remedy = "Populate 'grammarVersion' on the canonical document.";
        }
        else
        {
            message = "Spec is missing 'processFamilyVersion'.";
            remedy = "Populate 'processFamilyVersion' on the canonical document.";
        }

        return new SpecWarning
        {
            Code = SpecDiagnosticCodes.VersionSkew,
            Message = message,
            Severity = SpecDiagnosticSeverity.Error,
            Remedy = remedy
        };
    }

    private static IEnumerable<SpecWarning> EvaluateKindWarnings(CanonicalSpecNode node)
    {
        if (node.Kind is SpecResourceKind.Compute or SpecResourceKind.Report)
        {
            yield break;
        }

        // Dataset/Service/App slots are reserved in S1. Emit the warning
        // based on kind alone: ServiceCollectionExtensions registers a
        // placeholder ReservedSpecResourceStateStore in production DI, so
        // using store presence as the support signal would mask the warning
        // and let apply silently route these nodes through the compute path.
        yield return new SpecWarning
        {
            Code = SpecDiagnosticCodes.SpecKindNotInS1,
            Message = $"Node kind '{node.Kind}' is reserved for a future release; apply will reject '{node.Id}'.",
            Severity = SpecDiagnosticSeverity.Warning,
            NodeId = node.Id,
            Remedy = "Restructure the spec to a compute/report node for the current release."
        };
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
