// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Orchestration.Domain;
using Honua.Server.Features.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Orchestration;

/// <summary>
/// Engine-level behavior tests for conditional branching and ForEach/iteration
/// (issue #2146). Each test drives <see cref="WorkflowOrchestrationEngine"/> through
/// explicit reconcile ticks with in-memory stores so the data-dependent path and the
/// per-item fan-out are observable without background timers.
/// </summary>
public sealed class WorkflowBranchingAndForEachTests
{
    private static readonly ClaimsPrincipal Operator = new(
        new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "tester") }, "TestAuth"));

    [Fact]
    public async Task ReconcileWorkflowRun_ConditionalBranch_RunsTakenSkipsNotTaken()
    {
        var harness = new Harness();
        var now = harness.Clock.GetUtcNow();

        // A produces two artifacts. B's branch (>=2 artifacts) is taken; C's branch
        // (>=5 artifacts) is not taken and must be reported Skipped.
        var stepA = new WorkflowStepDefinition { StepId = "a", Plan = BuildPlan("plan-a") };
        var stepB = new WorkflowStepDefinition
        {
            StepId = "b",
            Plan = BuildPlan("plan-b"),
            DependsOn = new[] { "a" },
            Condition = new WorkflowStepCondition("a", WorkflowStepConditionKind.ArtifactCountAtLeast, Threshold: 2)
        };
        var stepC = new WorkflowStepDefinition
        {
            StepId = "c",
            Plan = BuildPlan("plan-c"),
            DependsOn = new[] { "a" },
            Condition = new WorkflowStepCondition("a", WorkflowStepConditionKind.ArtifactCountAtLeast, Threshold: 5)
        };

        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf-branch",
            Name = "branch",
            Steps = new[] { stepA, stepB, stepC },
            CreatedAt = now,
            UpdatedAt = now
        };
        await harness.Definitions.TryCreateAsync(definition);
        var run = await harness.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Operator);

        // Tick 1: submit A.
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var afterA = await harness.RunStore.GetAsync(run.RunId);
        var aJob = afterA!.StepStates.Single(s => s.StepId == "a").JobId!;
        harness.JobService.Complete(aJob, new[]
        {
            new ArtifactRef { ArtifactId = "art-1", Kind = ArtifactKind.FeatureLayer, Uri = "s3://b/1" },
            new ArtifactRef { ArtifactId = "art-2", Kind = ArtifactKind.FeatureLayer, Uri = "s3://b/2" }
        });

        // Tick 2: observe A; B taken (submitted), C not taken (Skipped) in the same pass.
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var afterBranch = await harness.RunStore.GetAsync(run.RunId);
        Assert.Equal(WorkflowStepStatus.Queued, afterBranch!.StepStates.Single(s => s.StepId == "b").Status);
        Assert.Equal(WorkflowStepStatus.Skipped, afterBranch.StepStates.Single(s => s.StepId == "c").Status);
        Assert.Contains("plan-b", harness.JobService.Submitted.Select(p => p.PlanId));
        Assert.DoesNotContain("plan-c", harness.JobService.Submitted.Select(p => p.PlanId));

        // Complete B; run succeeds (a skipped not-taken branch does not fail the run).
        var bJob = afterBranch.StepStates.Single(s => s.StepId == "b").JobId!;
        harness.JobService.Complete(bJob);
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);

        var final = await harness.RunStore.GetAsync(run.RunId);
        Assert.Equal(WorkflowRunStatus.Succeeded, final!.Status);
        Assert.Equal(WorkflowStepStatus.Succeeded, final.StepStates.Single(s => s.StepId == "b").Status);
        Assert.Equal(WorkflowStepStatus.Skipped, final.StepStates.Single(s => s.StepId == "c").Status);
    }

    [Fact]
    public async Task ReconcileWorkflowRun_ForEach_ExecutesSubStepPerItemWithSubstitution()
    {
        var harness = new Harness();
        var now = harness.Clock.GetUtcNow();

        var items = new[] { "east", "west", "north" };
        var forEachStep = new WorkflowStepDefinition
        {
            StepId = "work",
            Plan = BuildPlanWithInput("plan-work", "region", "${item}"),
            ForEach = new WorkflowForEachSpec(items)
        };

        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf-foreach",
            Name = "foreach",
            Steps = new[] { forEachStep },
            CreatedAt = now,
            UpdatedAt = now
        };
        await harness.Definitions.TryCreateAsync(definition);
        var run = await harness.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Operator);

        // The run is unrolled into one concrete step per item, deterministically ordered.
        Assert.Equal(items.Length, run.StepStates.Count);
        Assert.Equal(new[] { "work::0", "work::1", "work::2" }, run.StepStates.Select(s => s.StepId).ToArray());

        // Tick 1: all three iteration jobs are submitted with the item substituted in.
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var submittedRegions = harness.JobService.Submitted
            .Select(p => p.Steps[0].Inputs["region"])
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "east", "north", "west" }, submittedRegions);

        var queued = await harness.RunStore.GetAsync(run.RunId);
        foreach (var state in queued!.StepStates)
        {
            Assert.Equal(WorkflowStepStatus.Queued, state.Status);
            harness.JobService.Complete(state.JobId!);
        }

        // Tick 2: all iterations observed Succeeded; run aggregates to Succeeded.
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var final = await harness.RunStore.GetAsync(run.RunId);
        Assert.Equal(WorkflowRunStatus.Succeeded, final!.Status);
        Assert.All(final.StepStates, s => Assert.Equal(WorkflowStepStatus.Succeeded, s.Status));
    }

    [Fact]
    public async Task ReconcileWorkflowRun_BranchInsideForEach_GatesEveryIteration()
    {
        var harness = new Harness();
        var now = harness.Clock.GetUtcNow();

        // A "gate" step runs first. The ForEach body is conditioned on the gate producing
        // an artifact; when the gate yields none, every unrolled iteration is skipped —
        // the branch predicate composes with the per-item fan-out.
        var gate = new WorkflowStepDefinition { StepId = "gate", Plan = BuildPlan("plan-gate") };
        var work = new WorkflowStepDefinition
        {
            StepId = "work",
            Plan = BuildPlanWithInput("plan-work", "region", "${item}"),
            DependsOn = new[] { "gate" },
            Condition = new WorkflowStepCondition("gate", WorkflowStepConditionKind.HasArtifact),
            ForEach = new WorkflowForEachSpec(new[] { "alpha", "beta" })
        };

        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf-nested",
            Name = "nested",
            Steps = new[] { gate, work },
            CreatedAt = now,
            UpdatedAt = now
        };
        await harness.Definitions.TryCreateAsync(definition);
        var run = await harness.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Operator);

        // gate + two iteration sub-steps.
        Assert.Equal(new[] { "gate", "work::0", "work::1" }, run.StepStates.Select(s => s.StepId).ToArray());

        // Tick 1: submit gate.
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var afterGate = await harness.RunStore.GetAsync(run.RunId);
        var gateJob = afterGate!.StepStates.Single(s => s.StepId == "gate").JobId!;
        harness.JobService.Complete(gateJob); // no artifacts ⇒ branch not taken

        // Tick 2: gate observed Succeeded; both iterations evaluate the branch and skip.
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var final = await harness.RunStore.GetAsync(run.RunId);

        Assert.Equal(WorkflowStepStatus.Succeeded, final!.StepStates.Single(s => s.StepId == "gate").Status);
        Assert.Equal(WorkflowStepStatus.Skipped, final.StepStates.Single(s => s.StepId == "work::0").Status);
        Assert.Equal(WorkflowStepStatus.Skipped, final.StepStates.Single(s => s.StepId == "work::1").Status);
        Assert.Equal(WorkflowRunStatus.Succeeded, final.Status);
        // Only the gate job ran; no iteration jobs were submitted.
        Assert.Single(harness.JobService.Submitted);
    }

    [Fact]
    public async Task ReconcileWorkflowRun_BranchInsideForEach_RunsEveryIterationWhenTaken()
    {
        var harness = new Harness();
        var now = harness.Clock.GetUtcNow();

        var gate = new WorkflowStepDefinition { StepId = "gate", Plan = BuildPlan("plan-gate") };
        var work = new WorkflowStepDefinition
        {
            StepId = "work",
            Plan = BuildPlanWithInput("plan-work", "region", "${item}"),
            DependsOn = new[] { "gate" },
            Condition = new WorkflowStepCondition("gate", WorkflowStepConditionKind.HasArtifact),
            ForEach = new WorkflowForEachSpec(new[] { "alpha", "beta" })
        };

        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf-nested-taken",
            Name = "nested-taken",
            Steps = new[] { gate, work },
            CreatedAt = now,
            UpdatedAt = now
        };
        await harness.Definitions.TryCreateAsync(definition);
        var run = await harness.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Operator);

        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var afterGate = await harness.RunStore.GetAsync(run.RunId);
        var gateJob = afterGate!.StepStates.Single(s => s.StepId == "gate").JobId!;
        harness.JobService.Complete(gateJob, new[]
        {
            new ArtifactRef { ArtifactId = "g-1", Kind = ArtifactKind.FeatureLayer, Uri = "s3://g/1" }
        });

        // Tick 2: gate observed Succeeded WITH an artifact; both iterations are taken.
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var afterTaken = await harness.RunStore.GetAsync(run.RunId);
        Assert.Equal(WorkflowStepStatus.Queued, afterTaken!.StepStates.Single(s => s.StepId == "work::0").Status);
        Assert.Equal(WorkflowStepStatus.Queued, afterTaken.StepStates.Single(s => s.StepId == "work::1").Status);

        foreach (var state in afterTaken.StepStates.Where(s => s.StepId.StartsWith("work", StringComparison.Ordinal)))
        {
            harness.JobService.Complete(state.JobId!);
        }

        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var final = await harness.RunStore.GetAsync(run.RunId);
        Assert.Equal(WorkflowRunStatus.Succeeded, final!.Status);
        Assert.All(final.StepStates, s => Assert.Equal(WorkflowStepStatus.Succeeded, s.Status));
    }

    private static AnalysisPlan BuildPlan(string planId) => new()
    {
        PlanId = planId,
        IntentId = $"intent-{planId}",
        Steps = new[]
        {
            new AnalysisPlanStep
            {
                StepId = $"ap-{planId}",
                Kind = AnalysisPlanStepKind.Geoprocess,
                ProcessId = "noop"
            }
        }
    };

    private static AnalysisPlan BuildPlanWithInput(string planId, string key, string value) => new()
    {
        PlanId = planId,
        IntentId = $"intent-{planId}",
        Steps = new[]
        {
            new AnalysisPlanStep
            {
                StepId = $"ap-{planId}",
                Kind = AnalysisPlanStepKind.Geoprocess,
                ProcessId = "noop",
                Inputs = new Dictionary<string, string>(StringComparer.Ordinal) { [key] = value }
            }
        }
    };

    private sealed class Harness
    {
        public Harness()
        {
            RunStore = new FakeWorkflowRunStore();
            Definitions = new FakeWorkflowDefinitionStore();
            Progress = new FakeProgressStore();
            JobService = new FakeWorkflowJobExecutor();
            Clock = new TestClock(new DateTimeOffset(2026, 4, 16, 12, 0, 0, TimeSpan.Zero));
            Engine = new WorkflowOrchestrationEngine(
                RunStore,
                Definitions,
                JobService,
                Progress,
                Clock,
                NullLogger<WorkflowOrchestrationEngine>.Instance);
        }

        public FakeWorkflowRunStore RunStore { get; }
        public FakeWorkflowDefinitionStore Definitions { get; }
        public FakeProgressStore Progress { get; }
        public FakeWorkflowJobExecutor JobService { get; }
        public TestClock Clock { get; }
        public WorkflowOrchestrationEngine Engine { get; }
    }
}
