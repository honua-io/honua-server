// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.WorkflowPackages.Domain;

/// <summary>
/// Validates graph invariants that are independent of a specific runtime provider.
/// </summary>
public static class WorkflowPackageGraphValidator
{
    /// <summary>
    /// Validates the graph structure against the supplied node definitions.
    /// </summary>
    public static WorkflowPackageValidationResult Validate(
        WorkflowGraph graph,
        IReadOnlyDictionary<string, WorkflowNodeDefinition> nodeDefinitions,
        string? packageHash = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(nodeDefinitions);

        var failures = new List<WorkflowPackageValidationFailure>();
        var warnings = new List<string>();

        if (graph.Nodes.Count == 0)
        {
            failures.Add(new WorkflowPackageValidationFailure
            {
                Code = "EMPTY_GRAPH",
                Message = "Workflow graph must contain at least one node.",
                FieldPath = "graph.nodes"
            });
        }

        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in graph.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.NodeId))
            {
                failures.Add(new WorkflowPackageValidationFailure
                {
                    Code = "EMPTY_NODE_ID",
                    Message = "Every workflow node must declare a non-empty nodeId.",
                    FieldPath = "graph.nodes[].nodeId"
                });
                continue;
            }

            if (!nodeIds.Add(node.NodeId))
            {
                failures.Add(new WorkflowPackageValidationFailure
                {
                    Code = "DUPLICATE_NODE_ID",
                    Message = $"Duplicate workflow node id '{node.NodeId}'.",
                    FieldPath = $"graph.nodes[{node.NodeId}]"
                });
            }

            if (string.IsNullOrWhiteSpace(node.NodeTypeId))
            {
                failures.Add(new WorkflowPackageValidationFailure
                {
                    Code = "EMPTY_NODE_TYPE",
                    Message = $"Workflow node '{node.NodeId}' must declare a nodeTypeId.",
                    FieldPath = $"graph.nodes[{node.NodeId}].nodeTypeId"
                });
                continue;
            }

            if (!nodeDefinitions.TryGetValue(node.NodeTypeId, out var definition))
            {
                failures.Add(new WorkflowPackageValidationFailure
                {
                    Code = "UNKNOWN_NODE_TYPE",
                    Message = $"Workflow node '{node.NodeId}' references unknown node type '{node.NodeTypeId}'.",
                    FieldPath = $"graph.nodes[{node.NodeId}].nodeTypeId"
                });
                continue;
            }

            if (!definition.CapabilityFlags.Executable)
            {
                failures.Add(new WorkflowPackageValidationFailure
                {
                    Code = "NODE_NOT_EXECUTABLE",
                    Message = $"Workflow node '{node.NodeId}' uses node type '{node.NodeTypeId}', which is not executable on this server.",
                    FieldPath = $"graph.nodes[{node.NodeId}].nodeTypeId"
                });
            }

            foreach (var parameter in definition.ParameterSchemas.Where(parameter => parameter.Required))
            {
                if (!node.Parameters.ContainsKey(parameter.Name))
                {
                    failures.Add(new WorkflowPackageValidationFailure
                    {
                        Code = "MISSING_REQUIRED_PARAMETER",
                        Message = $"Workflow node '{node.NodeId}' is missing required parameter '{parameter.Name}'.",
                        FieldPath = $"graph.nodes[{node.NodeId}].parameters.{parameter.Name}"
                    });
                }
            }
        }

        foreach (var edge in graph.Edges)
        {
            if (string.IsNullOrWhiteSpace(edge.SourceNodeId))
            {
                failures.Add(new WorkflowPackageValidationFailure
                {
                    Code = "EMPTY_EDGE_SOURCE",
                    Message = "Workflow edge sourceNodeId is required.",
                    FieldPath = "graph.edges[].sourceNodeId"
                });
            }
            else if (!nodeIds.Contains(edge.SourceNodeId))
            {
                failures.Add(new WorkflowPackageValidationFailure
                {
                    Code = "UNKNOWN_EDGE_SOURCE",
                    Message = $"Workflow edge references unknown source node '{edge.SourceNodeId}'.",
                    FieldPath = "graph.edges[].sourceNodeId"
                });
            }

            if (string.IsNullOrWhiteSpace(edge.TargetNodeId))
            {
                failures.Add(new WorkflowPackageValidationFailure
                {
                    Code = "EMPTY_EDGE_TARGET",
                    Message = "Workflow edge targetNodeId is required.",
                    FieldPath = "graph.edges[].targetNodeId"
                });
            }
            else if (!nodeIds.Contains(edge.TargetNodeId))
            {
                failures.Add(new WorkflowPackageValidationFailure
                {
                    Code = "UNKNOWN_EDGE_TARGET",
                    Message = $"Workflow edge references unknown target node '{edge.TargetNodeId}'.",
                    FieldPath = "graph.edges[].targetNodeId"
                });
            }

            if (string.Equals(edge.SourceNodeId, edge.TargetNodeId, StringComparison.Ordinal))
            {
                failures.Add(new WorkflowPackageValidationFailure
                {
                    Code = "SELF_EDGE",
                    Message = $"Workflow edge on node '{edge.SourceNodeId}' cannot target the same node.",
                    FieldPath = "graph.edges"
                });
            }

            if (edge.Kind == WorkflowEdgeKind.Failure && string.IsNullOrWhiteSpace(edge.TargetPort))
            {
                warnings.Add($"Failure edge from '{edge.SourceNodeId}' to '{edge.TargetNodeId}' has no targetPort; it will be treated as a control-only failure branch.");
            }
        }

        if (nodeIds.Count == graph.Nodes.Count && HasCycle(graph))
        {
            failures.Add(new WorkflowPackageValidationFailure
            {
                Code = "DEPENDENCY_CYCLE",
                Message = "Workflow graph contains a dependency cycle.",
                FieldPath = "graph.edges"
            });
        }

        return failures.Count == 0
            ? WorkflowPackageValidationResult.Success(packageHash, warnings)
            : WorkflowPackageValidationResult.Failed(failures, packageHash, warnings);
    }

    private static bool HasCycle(WorkflowGraph graph)
    {
        var adjacency = graph.Nodes.ToDictionary(
            node => node.NodeId,
            _ => new List<string>(),
            StringComparer.Ordinal);

        foreach (var edge in graph.Edges.Where(edge => edge.Kind != WorkflowEdgeKind.Failure))
        {
            if (adjacency.TryGetValue(edge.SourceNodeId, out var targets)
                && adjacency.ContainsKey(edge.TargetNodeId))
            {
                targets.Add(edge.TargetNodeId);
            }
        }

        var unvisited = new HashSet<string>(adjacency.Keys, StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);

        foreach (var nodeId in adjacency.Keys)
        {
            if (unvisited.Contains(nodeId) && Visit(nodeId, adjacency, unvisited, active))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Visit(
        string nodeId,
        IReadOnlyDictionary<string, List<string>> adjacency,
        HashSet<string> unvisited,
        HashSet<string> active)
    {
        if (!active.Add(nodeId))
        {
            return true;
        }

        if (adjacency.TryGetValue(nodeId, out var targets))
        {
            foreach (var target in targets)
            {
                if (Visit(target, adjacency, unvisited, active))
                {
                    return true;
                }
            }
        }

        active.Remove(nodeId);
        unvisited.Remove(nodeId);
        return false;
    }
}
