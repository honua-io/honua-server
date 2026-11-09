// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Orchestration.Domain;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Server.Features.Orchestration;

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

    public static void ValidateScheduledCatalogBindings(IReadOnlyList<WorkflowStepDefinition> steps, string packageHash)
    {
        foreach (var step in steps)
        {
            var inputKey = WorkflowOrchestrationEngine.FindDynamicCatalogIdentifier(step);
            if (inputKey is not null)
            {
                throw new WorkflowPackageValidationException(WorkflowPackageValidationResult.Failed(
                    [new WorkflowPackageValidationFailure
                    {
                        Code = "UNSUPPORTED_DYNAMIC_CATALOG_SCHEDULE",
                        Message = WorkflowOrchestrationEngine.DynamicCatalogIdentifierMessage(step.StepId, inputKey),
                        FieldPath = $"graph.nodes[{step.StepId}].parameters.{inputKey}"
                    }], packageHash));
            }
        }
    }

    public static AnalysisPlan ExpandForEachPlan(AnalysisPlan plan, WorkflowGraph graph)
    {
        if (graph.Nodes.All(node => node.ForEach is null))
        {
            return plan;
        }

        var nodes = graph.Nodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
        var definition = new WorkflowDefinition
        {
            WorkflowId = plan.PlanId,
            Name = plan.IntentId,
            CreatedAt = default,
            UpdatedAt = default,
            Steps = plan.Steps.Select(step => new WorkflowStepDefinition
            {
                StepId = step.StepId,
                Plan = plan with { Steps = [step] },
                DependsOn = step.DependsOn,
                ForEach = nodes[step.StepId].ForEach
            }).ToArray()
        };
        var expanded = WorkflowDefinitionExpander.Expand(definition);
        return plan with
        {
            Steps = expanded.Steps.Select(step => step.Plan.Steps[0] with
            {
                StepId = step.StepId,
                DependsOn = step.DependsOn
            }).ToArray()
        };
    }

    public static HashSet<string> BuildDataBoundInputFieldPaths(WorkflowGraph graph, AnalysisPlan plan)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var paths = new HashSet<string>(StringComparer.Ordinal);
        var stepIds = plan.Steps.Select(step => step.StepId).ToArray();
        foreach (var edge in graph.Edges.Where(edge => edge.Kind == WorkflowEdgeKind.Data && !string.IsNullOrWhiteSpace(edge.TargetPort)))
        {
            foreach (var stepId in stepIds.Where(stepId => stepId == edge.TargetNodeId
                || stepId.StartsWith(edge.TargetNodeId + WorkflowDefinitionExpander.IterationSeparator, StringComparison.Ordinal)))
            {
                paths.Add($"steps[{stepId}].inputs.{edge.TargetPort!.Trim()}");
            }
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
