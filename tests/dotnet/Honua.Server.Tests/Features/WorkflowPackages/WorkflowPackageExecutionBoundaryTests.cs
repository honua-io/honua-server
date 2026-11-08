// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Orchestration.Domain;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Server.Features.Orchestration;
using Honua.Server.Features.WorkflowPackages;
using Honua.Server.Tests.Features.Orchestration;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.WorkflowPackages;

public sealed partial class WorkflowPackageSchedulePublicationTests
{
    [UnitTest]
    public async Task ReconcileCompiledPackage_TimeoutRacesSuccessfulChild_PreservesSuccessWithoutRetry()
    {
        var harness = CreateHarness();
        var definition = await CompileGraphAsync(harness, TimeoutRetryGraph());
        var execution = Execute(harness);
        var run = await execution.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Author());
        await execution.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var queued = (await execution.Runs.GetAsync(run.RunId))!;
        execution.Clock.Advance(TimeSpan.FromSeconds(1));
        await execution.Engine.ReconcileWorkflowRunAsync(run.RunId);
        execution.Jobs.Complete(queued.StepStates[0].JobId!);
        await execution.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var completed = (await execution.Runs.GetAsync(run.RunId))!;
        Assert.Equal(WorkflowStepStatus.Succeeded, completed.StepStates[0].Status);
        Assert.Equal(WorkflowRunStatus.Succeeded, completed.Status);
        Assert.NotNull(completed.StepStates[0].OutputArtifacts);
        Assert.Single(execution.Jobs.Submitted);
    }

    [UnitTest]
    public async Task ReconcileCompiledPackage_AcknowledgedTimeoutAfterRestart_WaitsWithoutRecancelling()
    {
        var harness = CreateHarness();
        var definition = await CompileGraphAsync(harness, TimeoutRetryGraph());
        var execution = Execute(harness);
        execution.Jobs.CancellationStatus = ExecutionJobStatus.Queued;
        var run = await execution.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Author());
        await execution.Engine.ReconcileWorkflowRunAsync(run.RunId);
        execution.Clock.Advance(TimeSpan.FromSeconds(1));
        await execution.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var cancelling = (await execution.Runs.GetAsync(run.RunId))!;
        Assert.True(cancelling.StepStates[0].TimeoutCancellationRequested);
        var restored = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(cancelling, OrchestrationJsonContext.Default.WorkflowRun),
            OrchestrationJsonContext.Default.WorkflowRun)!;
        var runs = new FakeWorkflowRunStore();
        await runs.SetAsync(restored);
        var engine = new WorkflowOrchestrationEngine(runs, harness.Definitions, execution.Jobs,
            new FakeProgressStore(), execution.Clock, NullLogger<WorkflowOrchestrationEngine>.Instance);
        for (var tick = 0; tick < 3; tick++)
        {
            execution.Clock.Advance(TimeSpan.FromSeconds(1));
            await engine.ReconcileWorkflowRunAsync(run.RunId);
        }
        Assert.Single(execution.Jobs.Cancelled);
        Assert.Single(execution.Jobs.Submitted);
        Assert.Same(restored, await runs.GetAsync(run.RunId));
        execution.Jobs.Fail(restored.StepStates[0].JobId!, "worker failed after cancellation");
        await engine.ReconcileWorkflowRunAsync(run.RunId);
        var failed = (await runs.GetAsync(run.RunId))!;
        Assert.Equal(WorkflowStepStatus.Pending, failed.StepStates[0].Status);
        Assert.Equal("worker failed after cancellation", failed.StepStates[0].ErrorMessage);
    }

    [UnitTest]
    public async Task CreateVersion_FailureAndControlBetweenSameNodes_RejectsAmbiguousRouting()
    {
        var harness = CreateHarness();
        var graph = FailureGraph();
        graph = graph with { Edges = [.. graph.Edges, graph.Edges[0] with { Kind = WorkflowEdgeKind.Control }] };
        var saved = await harness.Service.SavePackageAsync(new SaveWorkflowPackageRequest { Name = "Mixed edges", Graph = graph }, Author(), CancellationToken.None);
        var exception = await Assert.ThrowsAsync<WorkflowPackageValidationException>(() => harness.Service.CreateVersionAsync(saved.PackageId, Author(), CancellationToken.None));
        Assert.Contains(exception.Result.Failures, failure => failure.Code == "CONFLICTING_FAILURE_EDGE");
    }

    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public async Task ReconcileCompiledPackage_MultipleFailureSources_RoutesOnlyWhenAllFail(bool firstFails, bool secondFails, bool skipFailures)
    {
        var harness = CreateHarness();
        var graph = FailureGraph();
        var source = graph.Nodes[0] with { FailurePolicy = skipFailures ? WorkflowStepFailurePolicy.Skip : WorkflowStepFailurePolicy.Fail };
        graph = graph with
        {
            Nodes = [source, source with { NodeId = "second" }, graph.Nodes[1]],
            Edges = [graph.Edges[0], graph.Edges[0] with { SourceNodeId = "second" }]
        };
        var definition = await CompileGraphAsync(harness, graph);
        definition = JsonSerializer.Deserialize(JsonSerializer.Serialize(definition, OrchestrationJsonContext.Default.WorkflowDefinition),
            OrchestrationJsonContext.Default.WorkflowDefinition)!;
        Assert.Equal(new[] { "work", "second" }, definition.Steps[2].FailureDependsOn);
        await harness.Definitions.SetAsync(definition);
        var execution = Execute(harness);
        var run = await execution.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Author());
        await execution.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var queued = (await execution.Runs.GetAsync(run.RunId))!;
        Assert.Equal(2, execution.Jobs.Submitted.Count);
        if (firstFails) execution.Jobs.Fail(queued.StepStates[0].JobId!, "first failed");
        else execution.Jobs.Complete(queued.StepStates[0].JobId!);
        await execution.Engine.ReconcileWorkflowRunAsync(run.RunId);
        Assert.NotEqual(WorkflowStepStatus.Queued, (await execution.Runs.GetAsync(run.RunId))!.StepStates[2].Status);
        if (secondFails) execution.Jobs.Fail(queued.StepStates[1].JobId!, "second failed");
        else execution.Jobs.Complete(queued.StepStates[1].JobId!);
        await execution.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var routed = (await execution.Runs.GetAsync(run.RunId))!;
        var shouldRoute = firstFails && secondFails && !skipFailures;
        Assert.Equal(shouldRoute ? WorkflowStepStatus.Queued : WorkflowStepStatus.Skipped, routed.StepStates[2].Status);
        if (shouldRoute)
        {
            execution.Jobs.Complete(routed.StepStates[2].JobId!);
            await execution.Engine.ReconcileWorkflowRunAsync(run.RunId);
        }
        Assert.Equal(skipFailures ? WorkflowRunStatus.Succeeded : WorkflowRunStatus.Failed, (await execution.Runs.GetAsync(run.RunId))!.Status);
    }

    [UnitTest]
    public async Task ReconcileCompiledPackage_RetryingFailureSource_DefersHandlerUntilExhausted()
    {
        var harness = CreateHarness();
        var graph = FailureGraph();
        graph = graph with { Nodes = [graph.Nodes[0] with { RetryPolicy = new StepRetryPolicy { MaxAttempts = 2, InitialDelaySeconds = 0 } }, graph.Nodes[1]] };
        var definition = await CompileGraphAsync(harness, graph);
        var execution = Execute(harness);
        var run = await execution.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Author());
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await execution.Engine.ReconcileWorkflowRunAsync(run.RunId);
            var queued = (await execution.Runs.GetAsync(run.RunId))!;
            Assert.Equal(WorkflowStepStatus.Pending, queued.StepStates[1].Status);
            execution.Jobs.Fail(queued.StepStates[0].JobId!, "failure");
            await execution.Engine.ReconcileWorkflowRunAsync(run.RunId);
            Assert.Equal(attempt == 1 ? WorkflowStepStatus.Pending : WorkflowStepStatus.Queued,
                (await execution.Runs.GetAsync(run.RunId))!.StepStates[1].Status);
        }
        Assert.Equal(3, execution.Jobs.Submitted.Count);
    }

    [UnitTest]
    public async Task ReconcileCompiledPackage_ForEachFailureSource_WaitsForEveryIteration()
    {
        var harness = CreateHarness();
        var graph = FailureGraph();
        graph = graph with { Nodes = [graph.Nodes[0] with { ForEach = new WorkflowForEachSpec(["east", "west"]) }, graph.Nodes[1]] };
        var definition = await CompileGraphAsync(harness, graph);
        var expanded = WorkflowDefinitionExpander.Expand(definition);
        Assert.Equal(new[] { "work::0", "work::1" }, expanded.Steps[2].FailureDependsOn);
        var execution = Execute(harness);
        var run = await execution.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Author());
        await execution.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var queued = (await execution.Runs.GetAsync(run.RunId))!;
        execution.Jobs.Fail(queued.StepStates[0].JobId!, "east failed");
        await execution.Engine.ReconcileWorkflowRunAsync(run.RunId);
        Assert.Equal(WorkflowStepStatus.Pending, (await execution.Runs.GetAsync(run.RunId))!.StepStates[2].Status);
        execution.Jobs.Fail(queued.StepStates[1].JobId!, "west failed");
        await execution.Engine.ReconcileWorkflowRunAsync(run.RunId);
        Assert.Equal(WorkflowStepStatus.Queued, (await execution.Runs.GetAsync(run.RunId))!.StepStates[2].Status);
    }

    [UnitTest]
    public async Task PublishVersion_LegacyDefinition_RequiresRepublishToApplyFailureRouting()
    {
        var harness = CreateHarness();
        var definition = await CompileGraphAsync(harness, FailureGraph());
        var legacy = definition with { Steps = [definition.Steps[0], definition.Steps[1] with { DependsOn = [], FailureDependsOn = [] }] };
        await harness.Definitions.SetAsync(legacy);
        Assert.Empty((await harness.Definitions.GetAsync(definition.WorkflowId))!.Steps[1].FailureDependsOn);
        var publication = Assert.Single(await harness.Packages.ListPublicationsAsync());
        await harness.Service.PublishVersionAsync(publication.PackageId, publication.PackageVersion,
            ScheduleRequest(publication.PublicationId, true, true, "0 * * * *"), Author(), CancellationToken.None);
        Assert.Equal(new[] { "work" }, (await harness.Definitions.GetAsync(definition.WorkflowId))!.Steps[1].FailureDependsOn);
    }

    [UnitTest]
    public async Task ReadGraph_OmittedExecutionDefaults_UsesSupportedValues()
    {
        var graph = ReadGraph("""
            {"nodes":[{"nodeId":"source","nodeTypeId":"process:test.echo"},
            {"nodeId":"work","nodeTypeId":"process:test.echo","forEach":{"items":["east"]},
            "condition":{"sourceStepId":"source","kind":"UpstreamSucceeded"}}],
            "edges":[{"sourceNodeId":"source","targetNodeId":"work","kind":"Control"}]}
            """);
        var node = graph.Nodes[1];
        Assert.Equal(WorkflowStepFailurePolicy.Fail, node.FailurePolicy);
        Assert.Equal("${item}", node.ForEach!.ItemPlaceholder);
        Assert.Null(node.ForEach.MaxIterations);
        Assert.Equal(0, node.Condition!.Threshold);
        Assert.False(node.Condition.Negate);
        await CompileGraphAsync(CreateHarness(), graph);
    }

    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData("Failure", "port", "UNSUPPORTED_EDGE")]
    [InlineData("99", null, "UNSUPPORTED_EDGE")]
    public async Task CreateVersion_InvalidFailureEdge_ReturnsValidation(string kind, string? port, string code)
    {
        var graph = FailureGraph();
        graph = graph with { Edges = [graph.Edges[0] with { Kind = kind == "Failure" ? WorkflowEdgeKind.Failure : (WorkflowEdgeKind)99, TargetPort = port }] };
        var snapshot = await new EchoNodeRegistry().GetSnapshotAsync();
        var result = WorkflowPackageGraphValidator.Validate(graph, snapshot.Nodes.ToDictionary(node => node.NodeTypeId));
        Assert.Contains(result.Failures, failure => failure.Code == code);
    }

    [UnitTest]
    public async Task ValidateGraph_FailureCycleReservedIdAndIterationBinding_ReturnsSpecificFailures()
    {
        var snapshot = await new EchoNodeRegistry().GetSnapshotAsync();
        var definitions = snapshot.Nodes.ToDictionary(node => node.NodeTypeId);
        var graph = FailureGraph();
        var cycle = graph with { Edges = [.. graph.Edges, graph.Edges[0] with { SourceNodeId = "handler", TargetNodeId = "work" }] };
        Assert.Contains(WorkflowPackageGraphValidator.Validate(cycle, definitions).Failures, f => f.Code == "DEPENDENCY_CYCLE");
        var reserved = EchoGraph() with { Nodes = [EchoGraph().Nodes[0] with { NodeId = "work::0" }] };
        Assert.Contains(WorkflowPackageGraphValidator.Validate(reserved, definitions).Failures, f => f.Code == "RESERVED_NODE_ID");
        var binding = graph with
        {
            Nodes = [graph.Nodes[0] with { ForEach = new WorkflowForEachSpec(["east"]) }, graph.Nodes[1]],
            Edges = [graph.Edges[0] with { Kind = WorkflowEdgeKind.Data, TargetPort = "input" }]
        };
        Assert.Contains(WorkflowPackageGraphValidator.Validate(binding, definitions).Failures, f => f.Code == "UNSUPPORTED_ITERATION_BINDING");
        var nested = binding with { Nodes = [binding.Nodes[0], binding.Nodes[1] with { ForEach = new WorkflowForEachSpec(["west"]) }] };
        Assert.Contains(WorkflowPackageGraphValidator.Validate(nested, definitions).Failures, f => f.FieldPath == "graph.nodes[handler].forEach");
    }

    [UnitTest]
    public void ComputePackageHash_GraphWithoutExecutionSettings_KeepsLegacyDigest()
    {
        var graph = ReadGraph("""{"nodes":[{"nodeId":"work","nodeTypeId":"process:test.echo"}]}""");
        Assert.Equal("a54da6ad2a5d74de71fe80b3f29a61e17968480a69d1eaffd7ba7b7cada0cc37", WorkflowPackageGraphHasher.Compute(graph));
    }

    [UnitTest]
    public void ValidateExecution_HardLimitAndConditionOutsideDependencies_ReturnSpecificFields()
    {
        var node = EchoGraph().Nodes[0];
        var tooMany = node with { ForEach = new WorkflowForEachSpec(Enumerable.Repeat("item", WorkflowForEachSpec.HardIterationLimit + 1).ToArray()) };
        Assert.Contains("forEach.items", WorkflowStepExecutionValidator.Validate(tooMany).Keys);
        var condition = node with { Condition = new WorkflowStepCondition("source", WorkflowStepConditionKind.UpstreamSucceeded) };
        Assert.Contains("condition.sourceStepId", WorkflowStepExecutionValidator.ValidateReferences(condition, [],
            (new HashSet<string> { "source", node.NodeId }, new HashSet<string>())).Keys);
    }

    private static WorkflowGraph TimeoutRetryGraph() => ReadGraph("""
        {"nodes":[{"nodeId":"work","nodeTypeId":"process:test.echo","timeoutSeconds":1,
        "retryPolicy":{"maxAttempts":2,"initialDelaySeconds":0}}]}
        """);

    private static WorkflowGraph FailureGraph() => ReadGraph("""
        {"nodes":[{"nodeId":"work","nodeTypeId":"process:test.echo"},
        {"nodeId":"handler","nodeTypeId":"process:test.echo"}],
        "edges":[{"sourceNodeId":"work","targetNodeId":"handler","kind":"Failure"}]}
        """);
}
