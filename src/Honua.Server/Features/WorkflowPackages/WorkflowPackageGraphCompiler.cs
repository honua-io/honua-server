// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Orchestration.Domain;
using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Server.Features.WorkflowPackages;

internal static class WorkflowPackageGraphCompiler
{
    private const string ArtifactSelectorPrefix = "artifact:";

    // Literal parameters always win over a placeholder for a wired input.
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

    public static HashSet<string> BuildDataBoundInputFieldPaths(WorkflowGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in graph.Edges.Where(edge => edge.Kind == WorkflowEdgeKind.Data && !string.IsNullOrWhiteSpace(edge.TargetPort)))
        {
            // The Where filter above already proves TargetPort is non-null/non-whitespace,
            // but that narrowing doesn't flow across the LINQ call boundary into this body.
            paths.Add($"steps[{edge.TargetNodeId}].inputs.{edge.TargetPort!.Trim()}");
        }

        return paths;
    }

    // A graph with a data edge between nodes cannot run as one geoprocessing job.
    public static bool HasCrossNodeDataBindings(WorkflowGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        return graph.Edges.Any(edge => edge.Kind == WorkflowEdgeKind.Data && !string.IsNullOrWhiteSpace(edge.TargetPort));
    }

    private static IEnumerable<WorkflowEdge> DataBindingsInto(WorkflowGraph graph, string nodeId)
        => graph.Edges.Where(edge => edge.Kind == WorkflowEdgeKind.Data
            && !string.IsNullOrWhiteSpace(edge.TargetPort)
            && string.Equals(edge.TargetNodeId, nodeId, StringComparison.Ordinal));

    private static string ArtifactSelector(string? sourcePort)
        => string.IsNullOrWhiteSpace(sourcePort)
            ? ArtifactSelectorPrefix + "0"
            : ArtifactSelectorPrefix + sourcePort.Trim();
}
