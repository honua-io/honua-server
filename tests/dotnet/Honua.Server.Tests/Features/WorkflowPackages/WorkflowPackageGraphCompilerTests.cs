// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Server.Features.WorkflowPackages;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.WorkflowPackages;

/// <summary>
/// Unit tests for the graph compiler that translates workflow data edges into the
/// canonical execution-plan wiring (#1185).
/// </summary>
[Protocol(TestProtocols.Admin)]
[Operation(Operations.ProcessExecution)]
public sealed class WorkflowPackageGraphCompilerTests
{
    [UnitTest]
    public void BuildStepInputBindings_DerivesBindingFromDataEdge()
    {
        var graph = Graph(
            edges:
            [
                new WorkflowEdge
                {
                    SourceNodeId = "source",
                    TargetNodeId = "sink",
                    Kind = WorkflowEdgeKind.Data,
                    SourcePort = "result",
                    TargetPort = "wkb"
                }
            ]);

        var bindings = WorkflowPackageGraphCompiler.BuildStepInputBindings(graph, "sink");

        bindings.Should().ContainSingle();
        bindings[0].SourceStepId.Should().Be("source");
        bindings[0].SourceArtifactSelector.Should().Be("artifact:result");
        bindings[0].TargetInputKey.Should().Be("wkb");
    }

    [UnitTest]
    public void BuildStepInputBindings_DefaultsToFirstArtifactWhenSourcePortMissing()
    {
        var graph = Graph(
            edges:
            [
                new WorkflowEdge { SourceNodeId = "source", TargetNodeId = "sink", TargetPort = "wkb" }
            ]);

        var bindings = WorkflowPackageGraphCompiler.BuildStepInputBindings(graph, "sink");

        bindings.Should().ContainSingle();
        bindings[0].SourceArtifactSelector.Should().Be("artifact:0");
    }

    [UnitTest]
    public void BuildStepInputBindings_IgnoresControlFailureAndPortlessDataEdges()
    {
        var graph = Graph(
            edges:
            [
                new WorkflowEdge { SourceNodeId = "a", TargetNodeId = "sink", Kind = WorkflowEdgeKind.Control, TargetPort = "wkb" },
                new WorkflowEdge { SourceNodeId = "b", TargetNodeId = "sink", Kind = WorkflowEdgeKind.Failure, TargetPort = "wkb" },
                new WorkflowEdge { SourceNodeId = "c", TargetNodeId = "sink", Kind = WorkflowEdgeKind.Data }
            ]);

        WorkflowPackageGraphCompiler.BuildStepInputBindings(graph, "sink").Should().BeEmpty();
    }

    [UnitTest]
    public void BuildStepInputs_InjectsWiredInputPlaceholderWhenNotLiteral()
    {
        var node = new WorkflowNode
        {
            NodeId = "sink",
            NodeTypeId = "process:geometry.area",
            Parameters = new Dictionary<string, string> { ["srid"] = "4326" }
        };
        var graph = Graph(
            nodes: [node],
            edges:
            [
                new WorkflowEdge { SourceNodeId = "source", TargetNodeId = "sink", TargetPort = "wkb" }
            ]);

        var inputs = WorkflowPackageGraphCompiler.BuildStepInputs(node, graph);

        inputs.Should().Contain("srid", "4326");
        inputs.Should().Contain("wkb", "artifact:0");
    }

    [UnitTest]
    public void BuildStepInputs_KeepsLiteralOverWiredPlaceholder()
    {
        var node = new WorkflowNode
        {
            NodeId = "sink",
            NodeTypeId = "process:geometry.area",
            Parameters = new Dictionary<string, string> { ["wkb"] = "AQ==", ["srid"] = "4326" }
        };
        var graph = Graph(
            nodes: [node],
            edges:
            [
                new WorkflowEdge { SourceNodeId = "source", TargetNodeId = "sink", SourcePort = "result", TargetPort = "wkb" }
            ]);

        var inputs = WorkflowPackageGraphCompiler.BuildStepInputs(node, graph);

        inputs.Should().Contain("wkb", "AQ==");
    }

    [UnitTest]
    public void BuildDataBoundInputFieldPaths_MapsDataEdgesToPlanValidatorPaths()
    {
        var graph = Graph(
            edges:
            [
                new WorkflowEdge { SourceNodeId = "source", TargetNodeId = "sink", TargetPort = "wkb" },
                new WorkflowEdge { SourceNodeId = "source", TargetNodeId = "sink", Kind = WorkflowEdgeKind.Control, TargetPort = "srid" },
                new WorkflowEdge { SourceNodeId = "source", TargetNodeId = "sink" }
            ]);

        var paths = WorkflowPackageGraphCompiler.BuildDataBoundInputFieldPaths(graph);

        paths.Should().ContainSingle().Which.Should().Be("steps[sink].inputs.wkb");
    }

    [UnitTest]
    public void HasCrossNodeDataBindings_TrueOnlyForDataEdgeWithTargetPort()
    {
        WorkflowPackageGraphCompiler.HasCrossNodeDataBindings(Graph(edges: [])).Should().BeFalse();

        WorkflowPackageGraphCompiler.HasCrossNodeDataBindings(Graph(edges:
        [
            new WorkflowEdge { SourceNodeId = "a", TargetNodeId = "b", Kind = WorkflowEdgeKind.Control, TargetPort = "wkb" }
        ])).Should().BeFalse();

        WorkflowPackageGraphCompiler.HasCrossNodeDataBindings(Graph(edges:
        [
            new WorkflowEdge { SourceNodeId = "a", TargetNodeId = "b", Kind = WorkflowEdgeKind.Data }
        ])).Should().BeFalse();

        WorkflowPackageGraphCompiler.HasCrossNodeDataBindings(Graph(edges:
        [
            new WorkflowEdge { SourceNodeId = "a", TargetNodeId = "b", Kind = WorkflowEdgeKind.Data, TargetPort = "wkb" }
        ])).Should().BeTrue();
    }

    private static WorkflowGraph Graph(
        IReadOnlyList<WorkflowNode>? nodes = null,
        IReadOnlyList<WorkflowEdge>? edges = null)
        => new()
        {
            Nodes = nodes ?? [new WorkflowNode { NodeId = "sink", NodeTypeId = "process:geometry.area" }],
            Edges = edges ?? []
        };
}
