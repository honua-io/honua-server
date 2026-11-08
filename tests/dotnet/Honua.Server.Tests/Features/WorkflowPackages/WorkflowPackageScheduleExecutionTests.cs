// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
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
    public async Task PublishVersion_ExecutableNodeFields_SurviveWireStorageAndCompilation()
    {
        var graph = ExecutionGraph();
        var harness = CreateHarness();
        var definition = await CompileGraphAsync(harness, graph);
        var compiled = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(definition, OrchestrationJsonContext.Default.WorkflowDefinition),
            OrchestrationJsonContext.Default.WorkflowDefinition)!;
        var step = compiled.Steps[1];
        var reconstructed = graph with
        {
            Nodes = [graph.Nodes[0], graph.Nodes[1] with
            {
                RetryPolicy = step.RetryPolicy,
                FailurePolicy = step.FailurePolicy,
                TimeoutSeconds = step.TimeoutSeconds,
                Condition = step.Condition,
                ForEach = step.ForEach
            }]
        };
        var stored = Assert.Single(await harness.Packages.ListPackagesAsync()).Graph;
        var expected = JsonSerializer.SerializeToNode(graph, WorkflowPackagesJsonContext.Default.WorkflowGraph);
        Assert.True(JsonNode.DeepEquals(expected, JsonSerializer.SerializeToNode(stored, WorkflowPackagesJsonContext.Default.WorkflowGraph)));
        Assert.True(JsonNode.DeepEquals(expected, JsonSerializer.SerializeToNode(reconstructed, WorkflowPackagesJsonContext.Default.WorkflowGraph)));
    }

    [UnitTest]
    public void ComputePackageHash_ExecutableSettingsChange_ChangesHash()
    {
        var baseline = ReadGraph("""{"nodes":[{"nodeId":"work","nodeTypeId":"process:test.echo"}]}""");
        foreach (var settings in new[]
        {
            "\"retryPolicy\":{\"maxAttempts\":3,\"initialDelaySeconds\":2}",
            "\"failurePolicy\":\"Skip\"",
            "\"timeoutSeconds\":10",
            "\"condition\":{\"sourceStepId\":\"source\",\"kind\":\"UpstreamSucceeded\"}",
            "\"forEach\":{\"items\":[\"east\",\"west\"]}"
        })
        {
            var changed = ReadGraph("{\"nodes\":[{\"nodeId\":\"work\",\"nodeTypeId\":\"process:test.echo\"," + settings + "}]}");
            Assert.NotEqual(WorkflowPackageGraphHasher.Compute(baseline), WorkflowPackageGraphHasher.Compute(changed));
        }
    }

    [UnitTest]
    public async Task ReconcileCompiledPackage_RetryThreeWithBackoff_StopsAfterThreeAttempts()
    {
        var harness = CreateHarness();
        var definition = await CompileGraphAsync(harness, ReadGraph("""
            {"nodes":[{"nodeId":"work","nodeTypeId":"process:test.echo",
            "retryPolicy":{"maxAttempts":3,"initialDelaySeconds":2,"backoffMultiplier":2,"maxDelaySeconds":30}}]}
            """));
        var execution = Execute(harness);
        var run = await execution.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Author());
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await execution.Engine.ReconcileWorkflowRunAsync(run.RunId);
            var submitted = await execution.Runs.GetAsync(run.RunId);
            Assert.Equal(attempt, submitted!.StepStates[0].AttemptCount);
            execution.Jobs.Fail(submitted.StepStates[0].JobId!, "test failure");
            await execution.Engine.ReconcileWorkflowRunAsync(run.RunId);
            var observed = await execution.Runs.GetAsync(run.RunId);
            if (attempt < 3)
            {
                Assert.Equal(WorkflowStepStatus.Pending, observed!.StepStates[0].Status);
                Assert.Equal(execution.Clock.GetUtcNow().AddSeconds(attempt == 1 ? 2 : 4), observed.StepStates[0].NextAttemptAt);
                await execution.Engine.ReconcileWorkflowRunAsync(run.RunId);
                Assert.Equal(attempt, execution.Jobs.Submitted.Count);
                execution.Clock.Advance(TimeSpan.FromSeconds(attempt == 1 ? 2 : 4));
            }
            else
            {
                Assert.Equal(WorkflowRunStatus.Failed, observed!.Status);
            }
        }
        Assert.Equal(3, execution.Jobs.Submitted.Count);
    }

    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReconcileCompiledPackage_FailureEdge_OnlyRunsAfterFailure(bool failSource)
    {
        var harness = CreateHarness();
        var definition = await CompileGraphAsync(harness, ReadGraph("""
            {"nodes":[{"nodeId":"work","nodeTypeId":"process:test.echo"},
            {"nodeId":"handler","nodeTypeId":"process:test.echo"}],
            "edges":[{"sourceNodeId":"work","targetNodeId":"handler","kind":"Failure"}]}
            """));
        var execution = Execute(harness);
        var run = await execution.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Author());
        await execution.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var queued = await execution.Runs.GetAsync(run.RunId);
        Assert.Equal(WorkflowStepStatus.Pending, queued!.StepStates[1].Status);
        Assert.Single(execution.Jobs.Submitted);
        if (failSource)
        {
            execution.Jobs.Fail(queued.StepStates[0].JobId!, "failure");
        }
        else
        {
            execution.Jobs.Complete(queued.StepStates[0].JobId!);
        }
        await execution.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var routed = await execution.Runs.GetAsync(run.RunId);
        Assert.Equal(failSource ? WorkflowStepStatus.Queued : WorkflowStepStatus.Skipped, routed!.StepStates[1].Status);
        Assert.Equal(failSource ? 2 : 1, execution.Jobs.Submitted.Count);
    }

    [UnitTest]
    public async Task ReconcileCompiledPackage_Timeout_CancelsChildBeforeFailingStep()
    {
        var harness = CreateHarness();
        var definition = await CompileGraphAsync(harness, ReadGraph("""
            {"nodes":[{"nodeId":"work","nodeTypeId":"process:test.echo","timeoutSeconds":5}]}
            """));
        var execution = Execute(harness);
        var run = await execution.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Author());
        await execution.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var queued = await execution.Runs.GetAsync(run.RunId);
        execution.Clock.Advance(TimeSpan.FromSeconds(4));
        await execution.Engine.ReconcileWorkflowRunAsync(run.RunId);
        Assert.Empty(execution.Jobs.Cancelled);
        execution.Clock.Advance(TimeSpan.FromSeconds(1));
        await execution.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var cancelling = await execution.Runs.GetAsync(run.RunId);
        Assert.Equal(WorkflowStepStatus.Queued, cancelling!.StepStates[0].Status);
        Assert.Equal(queued!.StepStates[0].JobId, Assert.Single(execution.Jobs.Cancelled));
        await execution.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var timedOut = await execution.Runs.GetAsync(run.RunId);
        Assert.Equal(WorkflowRunStatus.Failed, timedOut!.Status);
        Assert.Contains("timeout", timedOut.StepStates[0].ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [UnitTest]
    public async Task ReconcileCompiledPackage_ConditionAndFanOut_ExecuteOnlyTakenIterations()
    {
        var harness = CreateHarness();
        var definition = await CompileGraphAsync(harness, ExecutionGraph());
        var execution = Execute(harness);
        var run = await execution.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Author());
        await execution.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var queued = await execution.Runs.GetAsync(run.RunId);
        Assert.Equal(3, queued!.StepStates.Count);
        Assert.Single(execution.Jobs.Submitted);
        execution.Jobs.Complete(queued.StepStates[0].JobId!);
        await execution.Engine.ReconcileWorkflowRunAsync(run.RunId);
        Assert.Equal(new[] { "east", "west" }, execution.Jobs.Submitted.Skip(1).Select(plan => plan.Steps[0].Inputs["region"]));
    }

    [UnitTest]
    public async Task CreateVersion_UnsupportedExecutionSetting_ReturnsFieldValidation()
    {
        var harness = CreateHarness();
        var graph = ReadGraph("""
            {"nodes":[{"nodeId":"work","nodeTypeId":"process:test.echo","timeoutSeconds":0}]}
            """ );
        var saved = await harness.Service.SavePackageAsync(new SaveWorkflowPackageRequest { Name = "Invalid", Graph = graph }, Author(), CancellationToken.None);
        var exception = await Assert.ThrowsAsync<WorkflowPackageValidationException>(() => harness.Service.CreateVersionAsync(saved.PackageId, Author(), CancellationToken.None));
        Assert.Contains(exception.Result.Failures, failure => failure.FieldPath == "graph.nodes[work].timeoutSeconds");
    }

    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData("\"retryPolicy\":{\"maxAttempts\":0,\"initialDelaySeconds\":0}", "retryPolicy.maxAttempts")]
    [InlineData("\"retryPolicy\":{\"maxAttempts\":2,\"initialDelaySeconds\":-1}", "retryPolicy.initialDelaySeconds")]
    [InlineData("\"retryPolicy\":{\"maxAttempts\":2,\"initialDelaySeconds\":1,\"backoffMultiplier\":0}", "retryPolicy.backoffMultiplier")]
    [InlineData("\"retryPolicy\":{\"maxAttempts\":2,\"initialDelaySeconds\":1,\"maxDelaySeconds\":0}", "retryPolicy.maxDelaySeconds")]
    [InlineData("\"failurePolicy\":99", "failurePolicy")]
    [InlineData("\"condition\":{\"sourceStepId\":\"ghost\",\"kind\":\"HasArtifact\"}", "condition.sourceStepId")]
    [InlineData("\"condition\":{\"sourceStepId\":\"work\",\"kind\":99}", "condition.kind")]
    [InlineData("\"forEach\":{\"items\":[]}", "forEach.items")]
    [InlineData("\"forEach\":{\"items\":null}", "forEach.items")]
    [InlineData("\"forEach\":{\"items\":[null]}", "forEach.items")]
    [InlineData("\"forEach\":{\"items\":[\"one\",\"two\"],\"maxIterations\":1}", "forEach.maxIterations")]
    public async Task CreateVersion_InvalidExecutionOption_ReturnsSpecificField(string settings, string field)
    {
        var harness = CreateHarness();
        var graph = ReadGraph("{\"nodes\":[{\"nodeId\":\"work\",\"nodeTypeId\":\"process:test.echo\"," + settings + "}]}");
        var saved = await harness.Service.SavePackageAsync(new SaveWorkflowPackageRequest { Name = "Invalid option", Graph = graph }, Author(), CancellationToken.None);
        var exception = await Assert.ThrowsAsync<WorkflowPackageValidationException>(() => harness.Service.CreateVersionAsync(saved.PackageId, Author(), CancellationToken.None));
        Assert.Contains(exception.Result.Failures, failure => failure.FieldPath == "graph.nodes[work]." + field);
    }

    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData(WorkflowPublicationTarget.Job)]
    [InlineData(WorkflowPublicationTarget.ProcessEndpoint)]
    public async Task PublishVersion_ExecutionOptionsOnDirectJobTarget_RejectsInsteadOfDropping(WorkflowPublicationTarget target)
    {
        var harness = CreateHarness();
        var graph = ReadGraph("""
            {"nodes":[{"nodeId":"work","nodeTypeId":"process:test.echo","timeoutSeconds":5}]}
            """);
        var saved = await harness.Service.SavePackageAsync(new SaveWorkflowPackageRequest { Name = "Direct", Graph = graph }, Author(), CancellationToken.None);
        var version = await harness.Service.CreateVersionAsync(saved.PackageId, Author(), CancellationToken.None);
        var exception = await Assert.ThrowsAsync<WorkflowPackageValidationException>(() => harness.Service.PublishVersionAsync(saved.PackageId, version.Version, new PublishWorkflowPackageRequest { Target = target }, Author(), CancellationToken.None));
        Assert.Contains(exception.Result.Failures, failure => failure.Code == "UNSUPPORTED_EXECUTION_TARGET");
    }

    [UnitTest]
    public async Task ReconcileCompiledPackage_SkipFailurePolicy_RecordsSkippedRunSuccess()
    {
        var harness = CreateHarness();
        var definition = await CompileGraphAsync(harness, ReadGraph("""
            {"nodes":[{"nodeId":"work","nodeTypeId":"process:test.echo","failurePolicy":"Skip"}]}
            """));
        var execution = Execute(harness);
        var run = await execution.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Author());
        await execution.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var queued = await execution.Runs.GetAsync(run.RunId);
        execution.Jobs.Fail(queued!.StepStates[0].JobId!, "failed");
        await execution.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var skipped = await execution.Runs.GetAsync(run.RunId);
        Assert.Equal(WorkflowStepStatus.Skipped, skipped!.StepStates[0].Status);
        Assert.Equal(WorkflowRunStatus.Succeeded, skipped.Status);
    }

    [UnitTest]
    public async Task ReconcileCompiledPackage_TimeoutCancellationFails_RetainsChildAndRetriesCancellation()
    {
        var harness = CreateHarness();
        var definition = await CompileGraphAsync(harness, ReadGraph("""
            {"nodes":[{"nodeId":"work","nodeTypeId":"process:test.echo","timeoutSeconds":1,
            "retryPolicy":{"maxAttempts":2,"initialDelaySeconds":0}}]}
            """));
        var execution = Execute(harness);
        var run = await execution.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Author());
        await execution.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var queued = await execution.Runs.GetAsync(run.RunId);
        var originalJob = queued!.StepStates[0].JobId;
        execution.Clock.Advance(TimeSpan.FromSeconds(1));
        execution.Jobs.NextCancelJobFailure = new InvalidOperationException("unavailable");
        await execution.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var retryCancel = await execution.Runs.GetAsync(run.RunId);
        Assert.Equal(originalJob, retryCancel!.StepStates[0].JobId);
        Assert.Equal(WorkflowStepStatus.Queued, retryCancel.StepStates[0].Status);
        Assert.Single(execution.Jobs.Submitted);
        await execution.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var cancelling = await execution.Runs.GetAsync(run.RunId);
        Assert.True(cancelling!.StepStates[0].TimeoutCancellationRequested);
        Assert.Single(execution.Jobs.Submitted);
        await execution.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var retry = await execution.Runs.GetAsync(run.RunId);
        Assert.Equal(WorkflowStepStatus.Pending, retry!.StepStates[0].Status);
        Assert.False(retry.StepStates[0].TimeoutCancellationRequested);
        Assert.Null(retry.StepStates[0].JobId);
        await execution.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var resubmitted = await execution.Runs.GetAsync(run.RunId);
        Assert.Equal(2, resubmitted!.StepStates[0].AttemptCount);
        Assert.NotEqual(originalJob, resubmitted.StepStates[0].JobId);
    }

    [UnitTest]
    public async Task ReconcileCompiledPackage_NegatedCondition_DoesNotSubmitIterations()
    {
        var harness = CreateHarness();
        var graph = ExecutionGraph();
        graph = graph with { Nodes = [graph.Nodes[0], graph.Nodes[1] with { Condition = graph.Nodes[1].Condition! with { Negate = true } }] };
        var definition = await CompileGraphAsync(harness, graph);
        var execution = Execute(harness);
        var run = await execution.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Author());
        await execution.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var queued = await execution.Runs.GetAsync(run.RunId);
        execution.Jobs.Complete(queued!.StepStates[0].JobId!);
        await execution.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var skipped = await execution.Runs.GetAsync(run.RunId);
        Assert.All(skipped!.StepStates.Skip(1), step => Assert.Equal(WorkflowStepStatus.Skipped, step.Status));
        Assert.Single(execution.Jobs.Submitted);
    }

    private static WorkflowGraph ExecutionGraph() => ReadGraph("""
        {"nodes":[{"nodeId":"source","nodeTypeId":"process:test.echo"},
        {"nodeId":"work","nodeTypeId":"process:test.echo","parameters":{"region":"${item}"},
        "retryPolicy":{"maxAttempts":3,"initialDelaySeconds":2,"backoffMultiplier":2,"maxDelaySeconds":30},
        "failurePolicy":"Skip","timeoutSeconds":10,
        "condition":{"sourceStepId":"source","kind":"UpstreamSucceeded","threshold":0,"negate":false},
        "forEach":{"items":["east","west"],"itemPlaceholder":"${item}","maxIterations":2}}],
        "edges":[{"sourceNodeId":"source","targetNodeId":"work","kind":"Control"}]}
        """);

    private static WorkflowGraph ReadGraph(string json) => JsonSerializer.Deserialize(json, WorkflowPackagesJsonContext.Default.WorkflowGraph)!;

    private static async Task<WorkflowDefinition> CompileGraphAsync(Harness harness, WorkflowGraph graph)
    {
        var saved = await harness.Service.SavePackageAsync(new SaveWorkflowPackageRequest { Name = "Execution", Graph = graph }, Author(), CancellationToken.None);
        var version = await harness.Service.CreateVersionAsync(saved.PackageId, Author(), CancellationToken.None);
        var published = await harness.Service.PublishVersionAsync(version.PackageId, version.Version,
            ScheduleRequest("execution", enabled: true, scheduleEnabled: true, cron: "0 * * * *"), Author(), CancellationToken.None);
        return (await harness.Definitions.GetAsync(published.WorkflowDefinitionId!))!;
    }

    private static (WorkflowOrchestrationEngine Engine, FakeWorkflowRunStore Runs, FakeWorkflowJobExecutor Jobs, TestClock Clock) Execute(Harness harness)
    {
        var runs = new FakeWorkflowRunStore();
        var jobs = new FakeWorkflowJobExecutor();
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var engine = new WorkflowOrchestrationEngine(runs, harness.Definitions, jobs, new FakeProgressStore(), clock, NullLogger<WorkflowOrchestrationEngine>.Instance);
        return (engine, runs, jobs, clock);
    }
}
