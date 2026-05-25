// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Orchestration.Domain;
using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Server.Features.WorkflowPackages;

/// <summary>
/// Translates workflow graph data edges into the canonical execution-plan wiring used by
/// the geoprocessing and orchestration runtimes. Keeps edge-to-binding semantics in one
/// place so the single-plan and scheduled-definition compilers stay consistent.
/// </summary>
internal static class WorkflowPackageGraphCompiler
{
    private const string ArtifactSelectorPrefix = "artifact:";

    /// <summary>
    /// Builds the analysis-plan step inputs for <paramref name="node"/>: literal node parameters,
    /// plus a placeholder for every required input that an incoming data edge supplies. The
    /// placeholder lets plan validation/dry-run treat wired inputs as present; the orchestration
    /// runtime later overrides it with the resolved upstream artifact via <see cref="StepInputBinding"/>.
    /// Literal parameters always win so explicit values are never replaced by a placeholder.
    /// </summary>
    public static Dictionary<string, string> BuildStepInputs(WorkflowNode node, WorkflowGraph graph)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(graph);

        var inputs = new Dictionary<string, string>(node.Parameters, StringComparer.Ordinal);
        foreach (var edge in DataBindingsInto(graph, node.NodeId))
        {
            var targetKey = edge.TargetPort!.Trim();
            if (!inputs.ContainsKey(targetKey))
            {
                inputs[targetKey] = ArtifactSelector(edge.SourcePort);
            }
        }

        return inputs;
    }

    /// <summary>
    /// Builds the canonical <see cref="StepInputBinding"/> list for <paramref name="nodeId"/> from the
    /// incoming data edges so the orchestration engine can chain upstream outputs into downstream inputs.
    /// </summary>
    public static IReadOnlyList<StepInputBinding> BuildStepInputBindings(WorkflowGraph graph, string nodeId)
    {
        ArgumentNullException.ThrowIfNull(graph);

        List<StepInputBinding>? bindings = null;
        foreach (var edge in DataBindingsInto(graph, nodeId))
        {
            bindings ??= [];
            bindings.Add(new StepInputBinding
            {
                SourceStepId = edge.SourceNodeId,
                SourceArtifactSelector = ArtifactSelector(edge.SourcePort),
                TargetInputKey = edge.TargetPort!.Trim()
            });
        }

        return (IReadOnlyList<StepInputBinding>?)bindings ?? Array.Empty<StepInputBinding>();
    }

    /// <summary>
    /// Builds the geoprocessing validator field paths for inputs supplied by data edges. Static
    /// plan validation still checks literal inputs, unknown parameters, and unsupported processes,
    /// but runtime-resolved data bindings cannot be type-checked until the upstream artifact exists.
    /// </summary>
    public static HashSet<string> BuildDataBoundInputFieldPaths(WorkflowGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in graph.Edges)
        {
            if (edge.Kind == WorkflowEdgeKind.Data && !string.IsNullOrWhiteSpace(edge.TargetPort))
            {
                paths.Add($"steps[{edge.TargetNodeId}].inputs.{edge.TargetPort.Trim()}");
            }
        }

        return paths;
    }

    /// <summary>
    /// Returns <c>true</c> when the graph wires data between distinct nodes — i.e. carries at least one
    /// data edge with a target input port. Such graphs require a runtime that can resolve cross-step
    /// artifacts (the orchestration engine) and cannot run as a single dispatched geoprocessing job.
    /// </summary>
    public static bool HasCrossNodeDataBindings(WorkflowGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        foreach (var edge in graph.Edges)
        {
            if (edge.Kind == WorkflowEdgeKind.Data && !string.IsNullOrWhiteSpace(edge.TargetPort))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<WorkflowEdge> DataBindingsInto(WorkflowGraph graph, string nodeId)
    {
        foreach (var edge in graph.Edges)
        {
            if (edge.Kind == WorkflowEdgeKind.Data
                && !string.IsNullOrWhiteSpace(edge.TargetPort)
                && string.Equals(edge.TargetNodeId, nodeId, StringComparison.Ordinal))
            {
                yield return edge;
            }
        }
    }

    private static string ArtifactSelector(string? sourcePort)
        => string.IsNullOrWhiteSpace(sourcePort)
            ? ArtifactSelectorPrefix + "0"
            : ArtifactSelectorPrefix + sourcePort.Trim();
}
