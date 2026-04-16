// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Orchestration.Domain;
using Honua.Server.Features.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Orchestration;

/// <summary>
/// Engine-level behavior tests for <see cref="WorkflowOrchestrationEngine"/>. Each test
/// drives the engine through explicit reconcile ticks using in-memory stores, so the
/// acceptance criteria (chained execution, scheduled execution, dependency-aware recovery)
/// can be verified without background timers.
/// </summary>
public sealed class WorkflowOrchestrationEngineTests
{
    private static readonly ClaimsPrincipal Operator = new(
        new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "tester") }, "TestAuth"));

    [Fact]
    public async Task ReconcileWorkflowRun_ChainsStepsWithArtifactBinding()
    {
        var harness = new OrchestrationTestHarness();
        var definition = BuildChainedDefinition(harness.Clock.GetUtcNow());
        await harness.Definitions.TryCreateAsync(definition);

        var run = await harness.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Operator);

        // Tick 1: step A submitted.
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);
        Assert.Single(harness.JobService.Submitted);
        Assert.Equal("plan-a", harness.JobService.Submitted[0].PlanId);
        var submitted = await harness.RunStore.GetAsync(run.RunId);
        Assert.Equal(WorkflowStepStatus.Queued, submitted!.StepStates.Single(s => s.StepId == "a").Status);

        // Step A completes with an artifact.
        var stepAState = submitted.StepStates.Single(s => s.StepId == "a");
        harness.JobService.Complete(stepAState.JobId!, new[]
        {
            new ArtifactRef
            {
                ArtifactId = "art-1",
                Kind = ArtifactKind.FeatureLayer,
                Label = "primary",
                Uri = "s3://bucket/a.parquet"
            }
        });

        // Tick 2: observe A, then submit B with resolved binding.
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);

        Assert.Equal(2, harness.JobService.Submitted.Count);
        var planB = harness.JobService.Submitted[1];
        Assert.Equal("plan-b", planB.PlanId);
        Assert.Equal("s3://bucket/a.parquet", planB.Steps[0].Inputs["upstream_uri"]);

        var observed = await harness.RunStore.GetAsync(run.RunId);
        Assert.Equal(WorkflowStepStatus.Succeeded, observed!.StepStates.Single(s => s.StepId == "a").Status);
        Assert.Equal(WorkflowStepStatus.Queued, observed.StepStates.Single(s => s.StepId == "b").Status);
        Assert.Equal(WorkflowRunStatus.Running, observed.Status);

        // Step B completes; run goes Succeeded.
        var stepBState = observed.StepStates.Single(s => s.StepId == "b");
        harness.JobService.Complete(stepBState.JobId!);
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);

        var final = await harness.RunStore.GetAsync(run.RunId);
        Assert.Equal(WorkflowRunStatus.Succeeded, final!.Status);
        Assert.NotNull(final.CompletedAt);
    }

    [Fact]
    public async Task ReconcileWorkflowRun_RetriesFailedStepBeforeCascading()
    {
        var harness = new OrchestrationTestHarness();
        var retryPolicy = new StepRetryPolicy
        {
            MaxAttempts = 2,
            InitialDelaySeconds = 10,
            BackoffMultiplier = 1.0,
            MaxDelaySeconds = 60
        };
        var definition = BuildSingleStepDefinition(harness.Clock.GetUtcNow(), retryPolicy, WorkflowStepFailurePolicy.Fail);
        await harness.Definitions.TryCreateAsync(definition);

        var run = await harness.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Operator);

        // Submit attempt 1.
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var afterSubmit = await harness.RunStore.GetAsync(run.RunId);
        var step = afterSubmit!.StepStates[0];
        Assert.Equal(1, step.AttemptCount);

        // Fail attempt 1; reconcile should move back to Pending with NextAttemptAt.
        harness.JobService.Fail(step.JobId!, "transient");
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);

        var afterFail = await harness.RunStore.GetAsync(run.RunId);
        var retried = afterFail!.StepStates[0];
        Assert.Equal(WorkflowStepStatus.Pending, retried.Status);
        Assert.NotNull(retried.NextAttemptAt);
        Assert.True(retried.NextAttemptAt > harness.Clock.GetUtcNow());
        Assert.Equal(1, retried.AttemptCount);

        // Advance past the backoff window; engine should resubmit with new idempotency key.
        harness.Clock.Advance(TimeSpan.FromSeconds(15));
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);

        Assert.Equal(2, harness.JobService.Submitted.Count);
        var afterRetry = await harness.RunStore.GetAsync(run.RunId);
        Assert.Equal(WorkflowStepStatus.Queued, afterRetry!.StepStates[0].Status);
        Assert.Equal(2, afterRetry.StepStates[0].AttemptCount);

        // Attempt 2 succeeds; run terminal.
        harness.JobService.Complete(afterRetry.StepStates[0].JobId!);
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var final = await harness.RunStore.GetAsync(run.RunId);
        Assert.Equal(WorkflowRunStatus.Succeeded, final!.Status);
    }

    [Fact]
    public async Task ReconcileWorkflowRun_CascadesSkipWhenFailurePolicyIsSkip()
    {
        var harness = new OrchestrationTestHarness();
        var now = harness.Clock.GetUtcNow();
        var definition = BuildTwoStepDefinition(
            now,
            firstFailurePolicy: WorkflowStepFailurePolicy.Skip,
            firstRetryPolicy: null);
        await harness.Definitions.TryCreateAsync(definition);

        var run = await harness.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Operator);

        // Submit A.
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var afterSubmit = await harness.RunStore.GetAsync(run.RunId);
        var stepA = afterSubmit!.StepStates.Single(s => s.StepId == "a");

        // Fail A (no retry policy ⇒ immediately terminal by skip policy).
        harness.JobService.Fail(stepA.JobId!, "unrecoverable");
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);

        // Next reconcile propagates Skipped to dependent step B and finalises run.
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);

        var final = await harness.RunStore.GetAsync(run.RunId);
        Assert.NotNull(final);
        Assert.Equal(WorkflowStepStatus.Skipped, final.StepStates.Single(s => s.StepId == "a").Status);
        Assert.Equal(WorkflowStepStatus.Skipped, final.StepStates.Single(s => s.StepId == "b").Status);
        Assert.Equal(WorkflowRunStatus.Succeeded, final.Status);
        Assert.Single(harness.JobService.Submitted);
    }

    [Fact]
    public async Task ReconcileWorkflowRun_FailsRunWhenFailurePolicyIsFail()
    {
        var harness = new OrchestrationTestHarness();
        var definition = BuildTwoStepDefinition(
            harness.Clock.GetUtcNow(),
            firstFailurePolicy: WorkflowStepFailurePolicy.Fail,
            firstRetryPolicy: null);
        await harness.Definitions.TryCreateAsync(definition);

        var run = await harness.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Operator);

        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var stepA = (await harness.RunStore.GetAsync(run.RunId))!.StepStates.Single(s => s.StepId == "a");
        harness.JobService.Fail(stepA.JobId!, "hard fail");

        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);

        var final = await harness.RunStore.GetAsync(run.RunId);
        Assert.Equal(WorkflowStepStatus.Failed, final!.StepStates.Single(s => s.StepId == "a").Status);
        Assert.Equal(WorkflowStepStatus.Skipped, final.StepStates.Single(s => s.StepId == "b").Status);
        Assert.Equal(WorkflowRunStatus.Failed, final.Status);
        Assert.Contains("hard fail", final.ErrorMessage);
    }

    [Fact]
    public async Task ReconcileWorkflowRun_FailsRunWhenDefinitionMissing()
    {
        var harness = new OrchestrationTestHarness();
        var definition = BuildSingleStepDefinition(harness.Clock.GetUtcNow(), retryPolicy: null, WorkflowStepFailurePolicy.Fail);
        await harness.Definitions.TryCreateAsync(definition);

        var run = await harness.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Operator);

        await harness.Definitions.DeleteAsync(definition.WorkflowId);

        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);

        var final = await harness.RunStore.GetAsync(run.RunId);
        Assert.Equal(WorkflowRunStatus.Failed, final!.Status);
        Assert.NotNull(final.ErrorMessage);
    }

    private static WorkflowDefinition BuildChainedDefinition(DateTimeOffset now)
    {
        var stepA = new WorkflowStepDefinition
        {
            StepId = "a",
            Plan = BuildPlan("plan-a"),
        };
        var stepB = new WorkflowStepDefinition
        {
            StepId = "b",
            DependsOn = new[] { "a" },
            Plan = BuildPlan("plan-b"),
            InputBindings = new[]
            {
                new StepInputBinding
                {
                    SourceStepId = "a",
                    SourceArtifactSelector = "artifact:primary",
                    TargetInputKey = "upstream_uri"
                }
            }
        };

        return new WorkflowDefinition
        {
            WorkflowId = "wf-chain",
            Name = "chained",
            Steps = new[] { stepA, stepB },
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static WorkflowDefinition BuildSingleStepDefinition(
        DateTimeOffset now,
        StepRetryPolicy? retryPolicy,
        WorkflowStepFailurePolicy failurePolicy)
        => new()
        {
            WorkflowId = "wf-single",
            Name = "single",
            Steps = new[]
            {
                new WorkflowStepDefinition
                {
                    StepId = "a",
                    Plan = BuildPlan("plan-a"),
                    RetryPolicy = retryPolicy,
                    FailurePolicy = failurePolicy
                }
            },
            CreatedAt = now,
            UpdatedAt = now
        };

    private static WorkflowDefinition BuildTwoStepDefinition(
        DateTimeOffset now,
        WorkflowStepFailurePolicy firstFailurePolicy,
        StepRetryPolicy? firstRetryPolicy)
        => new()
        {
            WorkflowId = "wf-cascade",
            Name = "cascade",
            Steps = new[]
            {
                new WorkflowStepDefinition
                {
                    StepId = "a",
                    Plan = BuildPlan("plan-a"),
                    RetryPolicy = firstRetryPolicy,
                    FailurePolicy = firstFailurePolicy
                },
                new WorkflowStepDefinition
                {
                    StepId = "b",
                    DependsOn = new[] { "a" },
                    Plan = BuildPlan("plan-b")
                }
            },
            CreatedAt = now,
            UpdatedAt = now
        };

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

    private sealed class OrchestrationTestHarness
    {
        public OrchestrationTestHarness()
        {
            RunStore = new FakeWorkflowRunStore();
            Definitions = new FakeWorkflowDefinitionStore();
            Progress = new FakeProgressStore();
            JobService = new FakeGeoprocessingJobService();
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
        public FakeGeoprocessingJobService JobService { get; }
        public TestClock Clock { get; }
        public WorkflowOrchestrationEngine Engine { get; }
    }
}

internal sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now;

    public TestClock(DateTimeOffset start)
    {
        _now = start;
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
