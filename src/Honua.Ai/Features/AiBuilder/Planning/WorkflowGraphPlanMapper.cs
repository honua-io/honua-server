// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.AiBuilder.Planning;

/// <summary>
/// Pure mapping helpers that adapt a validated
/// <see cref="WorkflowGraph"/> (the output of the
/// <c>WorkflowGeneration</c> Bedrock seam) into the canonical
/// <see cref="McpAnalysisPlanOutput"/> shape the MCP plan lane returns.
/// Kept in one place so the live planner and unit tests pin the same contract
/// translation, mirroring <see cref="AiBuilderScenarioMapper"/> for fixtures.
/// </summary>
/// <remarks>
/// The two subsystems carry different plan models: the workflow-generation
/// provider emits a node/edge graph keyed by registry <c>NodeTypeId</c>; the
/// MCP <c>validate_plan</c>/<c>dry_run_plan</c>/<c>execute_plan</c> lane consumes
/// an <see cref="Honua.Core.Features.Geoprocessing.Domain.AnalysisPlan"/> with
/// steps keyed by <see cref="Honua.Core.Features.Geoprocessing.Domain.AnalysisPlanStepKind"/>.
/// The bridge is that a registry node carries the canonical
/// <see cref="WorkflowNodeDefinition.ProcessId"/> and a
/// <see cref="WorkflowNodeRuntimeKind"/>, so each graph node maps deterministically
/// to one analysis-plan step without re-prompting the model.
/// </remarks>
internal static class WorkflowGraphPlanMapper
{
    /// <summary>
    /// Adapts a validated graph into the wire-format analysis plan. Edges feed
    /// each step's <c>dependsOn</c>; node parameters become step inputs; the
    /// node definition (when resolvable in <paramref name="nodeDefinitions"/>)
    /// supplies the canonical process id and step kind.
    /// </summary>
    public static McpAnalysisPlanOutput ToAnalysisPlan(
        WorkflowGraph graph,
        string planId,
        string intentId,
        IReadOnlyDictionary<string, WorkflowNodeDefinition> nodeDefinitions)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(nodeDefinitions);

        var dependsOn = BuildDependencyIndex(graph.Edges);

        var steps = new List<McpAnalysisPlanStepOutput>(graph.Nodes.Count);
        foreach (var node in graph.Nodes)
        {
            nodeDefinitions.TryGetValue(node.NodeTypeId, out var definition);

            steps.Add(new McpAnalysisPlanStepOutput
            {
                StepId = node.NodeId,
                Kind = MapStepKind(definition, node.NodeTypeId),
                ProcessId = ResolveProcessId(definition, node.NodeTypeId),
                DependsOn = dependsOn.TryGetValue(node.NodeId, out var deps)
                    ? deps
                    : [],
                Inputs = node.Parameters.Count == 0
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : new Dictionary<string, string>(node.Parameters, StringComparer.Ordinal)
            });
        }

        return new McpAnalysisPlanOutput
        {
            PlanId = planId,
            IntentId = intentId,
            Steps = steps,
            Outputs = [],
            Warnings = []
        };
    }

    /// <summary>
    /// Maps a node's runtime kind (and, when present, the canonical process id
    /// it adapts) to the canonical <c>AnalysisPlanStepKind</c> string accepted
    /// by <c>McpToolHelpers.ToDomainPlan</c>. Unknown or non-geoprocessing
    /// runtime families fall through to <c>Geoprocess</c> so the wire output
    /// never carries an invalid step kind.
    /// </summary>
    private static string MapStepKind(WorkflowNodeDefinition? definition, string nodeTypeId)
    {
        var runtimeKind = definition?.RuntimeKind ?? WorkflowNodeRuntimeKind.Geoprocessing;

        return runtimeKind switch
        {
            WorkflowNodeRuntimeKind.Geoprocessing => MapGeoprocessingKind(definition, nodeTypeId),
            // ETL and workflow-utility nodes adapt to the canonical Geoprocess
            // step (the executor treats them as process invocations); query/
            // render/export specialisations are derived from the node id below.
            _ => MapGeoprocessingKind(definition, nodeTypeId)
        };
    }

    // Derives the most specific analysis step kind from the node's category and
    // type id. The workflow node registry uses dotted ids such as
    // process:feature.query, process:geometry.buffer, process:map.render,
    // process:export.geojson; we key off those stems so the plan executor
    // schedules each step against the right runtime family.
    private static string MapGeoprocessingKind(WorkflowNodeDefinition? definition, string nodeTypeId)
    {
        var probe = (nodeTypeId + " " + (definition?.Category ?? string.Empty)).ToLowerInvariant();

        if (probe.Contains("query", StringComparison.Ordinal)
            || probe.Contains("select", StringComparison.Ordinal)
            || probe.Contains("filter", StringComparison.Ordinal))
        {
            return "QueryFeatures";
        }

        if (probe.Contains("aggregate", StringComparison.Ordinal)
            || probe.Contains("summar", StringComparison.Ordinal)
            || probe.Contains("statistic", StringComparison.Ordinal))
        {
            return "Aggregate";
        }

        if (probe.Contains("render", StringComparison.Ordinal)
            || probe.Contains("map", StringComparison.Ordinal))
        {
            return "RenderMap";
        }

        if (probe.Contains("export", StringComparison.Ordinal)
            || probe.Contains("package", StringComparison.Ordinal)
            || probe.Contains("artifact", StringComparison.Ordinal))
        {
            return "Export";
        }

        return "Geoprocess";
    }

    private static string? ResolveProcessId(WorkflowNodeDefinition? definition, string nodeTypeId)
    {
        if (!string.IsNullOrWhiteSpace(definition?.ProcessId))
        {
            return definition!.ProcessId;
        }

        // The id convention is "<provider>:<process-id>"; when the node adapts a
        // geoprocessing process but did not surface ProcessId explicitly, the
        // suffix is the canonical process id the executor resolves.
        var separator = nodeTypeId.IndexOf(':', StringComparison.Ordinal);
        return separator >= 0 && separator < nodeTypeId.Length - 1
            ? nodeTypeId[(separator + 1)..]
            : null;
    }

    // Builds a TargetNodeId -> [SourceNodeId, ...] index so each step lists the
    // upstream nodes it depends on, deduplicated and in graph edge order.
    private static Dictionary<string, IReadOnlyList<string>> BuildDependencyIndex(
        IReadOnlyList<WorkflowEdge> edges)
    {
        var pending = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            if (string.IsNullOrEmpty(edge.TargetNodeId) || string.IsNullOrEmpty(edge.SourceNodeId))
            {
                continue;
            }

            if (!pending.TryGetValue(edge.TargetNodeId, out var sources))
            {
                sources = [];
                pending[edge.TargetNodeId] = sources;
            }

            if (!sources.Contains(edge.SourceNodeId, StringComparer.Ordinal))
            {
                sources.Add(edge.SourceNodeId);
            }
        }

        var index = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (target, sources) in pending)
        {
            index[target] = sources;
        }

        return index;
    }
}
