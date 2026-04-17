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
    public async Task ReconcileWorkflowRun_CascadesSkipOnlyToBoundDependentsWhenFailurePolicyIsSkip()
    {
        var harness = new OrchestrationTestHarness();
        var now = harness.Clock.GetUtcNow();
        var definition = BuildTwoStepDefinition(
            now,
            firstFailurePolicy: WorkflowStepFailurePolicy.Skip,
            firstRetryPolicy: null,
            bindDependentOnUpstream: true);
        await harness.Definitions.TryCreateAsync(definition);

        var run = await harness.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Operator);

        // Submit A.
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var afterSubmit = await harness.RunStore.GetAsync(run.RunId);
        var stepA = afterSubmit!.StepStates.Single(s => s.StepId == "a");

        // Fail A (no retry policy ⇒ immediately terminal by skip policy).
        harness.JobService.Fail(stepA.JobId!, "unrecoverable");
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);

        // Next reconcile propagates Skipped to the bound dependent step B and finalises run.
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);

        var final = await harness.RunStore.GetAsync(run.RunId);
        Assert.NotNull(final);
        Assert.Equal(WorkflowStepStatus.Skipped, final.StepStates.Single(s => s.StepId == "a").Status);
        Assert.Equal(WorkflowStepStatus.Skipped, final.StepStates.Single(s => s.StepId == "b").Status);
        Assert.Equal(WorkflowRunStatus.Succeeded, final.Status);
        Assert.Single(harness.JobService.Submitted);
    }

    [Fact]
    public async Task ReconcileWorkflowRun_SkippedUpstreamWithoutBindingDoesNotCascadeSkip()
    {
        // A is Skip-policy; B only has structural DependsOn (no input binding), so per the
        // design contract B must proceed normally once A reaches its terminal skipped state.
        var harness = new OrchestrationTestHarness();
        var now = harness.Clock.GetUtcNow();
        var definition = BuildTwoStepDefinition(
            now,
            firstFailurePolicy: WorkflowStepFailurePolicy.Skip,
            firstRetryPolicy: null,
            bindDependentOnUpstream: false);
        await harness.Definitions.TryCreateAsync(definition);

        var run = await harness.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Operator);

        // Submit A.
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var afterSubmit = await harness.RunStore.GetAsync(run.RunId);
        var stepA = afterSubmit!.StepStates.Single(s => s.StepId == "a");

        // Fail A ⇒ Skipped under the skip policy.
        harness.JobService.Fail(stepA.JobId!, "unrecoverable");
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);

        // Reconcile again: B should now be submitted because it has no artifact binding to A.
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var afterSubmitB = await harness.RunStore.GetAsync(run.RunId);
        var stepBState = afterSubmitB!.StepStates.Single(s => s.StepId == "b");
        Assert.Equal(WorkflowStepStatus.Queued, stepBState.Status);
        Assert.NotNull(stepBState.JobId);

        // Complete B and finalise.
        harness.JobService.Complete(stepBState.JobId!);
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);

        var final = await harness.RunStore.GetAsync(run.RunId);
        Assert.Equal(WorkflowStepStatus.Skipped, final!.StepStates.Single(s => s.StepId == "a").Status);
        Assert.Equal(WorkflowStepStatus.Succeeded, final.StepStates.Single(s => s.StepId == "b").Status);
        Assert.Equal(WorkflowRunStatus.Succeeded, final.Status);
        Assert.Equal(2, harness.JobService.Submitted.Count);
    }

    [Fact]
    public async Task ReconcileWorkflowRun_FailsRunAndCancelsDependentsWhenFailurePolicyIsFail()
    {
        var harness = new OrchestrationTestHarness();
        var definition = BuildTwoStepDefinition(
            harness.Clock.GetUtcNow(),
            firstFailurePolicy: WorkflowStepFailurePolicy.Fail,
            firstRetryPolicy: null,
            bindDependentOnUpstream: false);
        await harness.Definitions.TryCreateAsync(definition);

        var run = await harness.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Operator);

        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var stepA = (await harness.RunStore.GetAsync(run.RunId))!.StepStates.Single(s => s.StepId == "a");
        harness.JobService.Fail(stepA.JobId!, "hard fail");

        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);

        var final = await harness.RunStore.GetAsync(run.RunId);
        Assert.Equal(WorkflowStepStatus.Failed, final!.StepStates.Single(s => s.StepId == "a").Status);
        Assert.Equal(WorkflowStepStatus.Cancelled, final.StepStates.Single(s => s.StepId == "b").Status);
        Assert.Equal(WorkflowRunStatus.Failed, final.Status);
        Assert.Contains("hard fail", final.ErrorMessage);
    }

    [Fact]
    public async Task ReconcileWorkflowRun_SubmitsJobWithOrchestrationMetadata()
    {
        // Acceptance criterion: downstream cost/rate controls can consume orchestration metadata
        // without reopening the orchestration contract. Each submitted job must carry the run
        // and step identifiers so distributed consumers can correlate parent and child jobs.
        var harness = new OrchestrationTestHarness();
        var definition = BuildSingleStepDefinition(
            harness.Clock.GetUtcNow(),
            retryPolicy: null,
            WorkflowStepFailurePolicy.Fail);
        await harness.Definitions.TryCreateAsync(definition);

        var run = await harness.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Operator);

        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);

        var metadata = Assert.Single(harness.JobService.SubmittedMetadata);
        Assert.NotNull(metadata);
        Assert.Equal(run.RunId, metadata["orchestration.runId"]);
        Assert.Equal(definition.WorkflowId, metadata["orchestration.workflowId"]);
        Assert.Equal("a", metadata["orchestration.stepId"]);
        Assert.Equal("1", metadata["orchestration.attempt"]);
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

        // Sibling-path regression: the previous implementation left step states Pending,
        // which kept the run in the active reconcile set forever and blocked the persist-
        // time terminal telemetry gate. Step states must also be terminal so the run
        // leaves the active set exactly once.
        Assert.All(final.StepStates, s => Assert.True(
            s.Status is WorkflowStepStatus.Cancelled
                or WorkflowStepStatus.Failed
                or WorkflowStepStatus.Succeeded
                or WorkflowStepStatus.Skipped,
            $"Step {s.StepId} was left in non-terminal state {s.Status} despite missing definition."));
        var active = await harness.RunStore.ListActiveAsync();
        Assert.DoesNotContain(active, r => r.RunId == run.RunId);
    }

    [Fact]
    public async Task ReconcileWorkflowRun_FailsStepWhenArtifactsUnavailable_AndDownstreamBindingsExist()
    {
        // HIGH regression: when a step's underlying job succeeds but results retrieval fails
        // (e.g. transient result-package store outage) AND downstream steps bind artifacts
        // from it, the engine must fail the step fast so the run surfaces the real cause
        // rather than later failing the dependent with a confusing selector error.
        var harness = new OrchestrationTestHarness();
        var definition = BuildChainedDefinition(harness.Clock.GetUtcNow());
        await harness.Definitions.TryCreateAsync(definition);

        var run = await harness.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Operator);

        // Tick 1: submit A.
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var afterSubmit = await harness.RunStore.GetAsync(run.RunId);
        var stepA = afterSubmit!.StepStates.Single(s => s.StepId == "a");

        // Job itself succeeds, but the next results-fetch fails transiently; step A has a
        // downstream binding in step B, so the engine must surface the failure at A.
        harness.JobService.Complete(stepA.JobId!, artifacts: Array.Empty<ArtifactRef>());
        harness.JobService.NextGetJobResultsFailure = new InvalidOperationException("result package not yet available");

        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);

        var final = await harness.RunStore.GetAsync(run.RunId);
        Assert.Equal(WorkflowStepStatus.Failed, final!.StepStates.Single(s => s.StepId == "a").Status);
        Assert.Contains("result package not yet available", final.StepStates.Single(s => s.StepId == "a").ErrorMessage);
        Assert.Equal(WorkflowRunStatus.Failed, final.Status);

        // Step B was gated on A's artifacts and must be cancelled, not submitted to the substrate.
        Assert.Single(harness.JobService.Submitted);
        Assert.Equal(WorkflowStepStatus.Cancelled, final.StepStates.Single(s => s.StepId == "b").Status);
    }

    [Fact]
    public async Task ReconcileWorkflowRun_SucceedsStepWhenArtifactsUnavailable_AndNoDownstreamBindings()
    {
        // Mirror of the HIGH regression: when NO downstream step binds the successful step's
        // artifacts, a transient result-fetch failure must not terminalise the step. Artifact
        // retrieval is best-effort for steps whose outputs are not consumed downstream.
        var harness = new OrchestrationTestHarness();
        var definition = BuildSingleStepDefinition(harness.Clock.GetUtcNow(), retryPolicy: null, WorkflowStepFailurePolicy.Fail);
        await harness.Definitions.TryCreateAsync(definition);

        var run = await harness.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Operator);

        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var submitted = (await harness.RunStore.GetAsync(run.RunId))!.StepStates[0];

        harness.JobService.Complete(submitted.JobId!, artifacts: Array.Empty<ArtifactRef>());
        harness.JobService.NextGetJobResultsFailure = new InvalidOperationException("result package blip");

        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);

        var final = await harness.RunStore.GetAsync(run.RunId);
        Assert.Equal(WorkflowStepStatus.Succeeded, final!.StepStates[0].Status);
        Assert.Equal(WorkflowRunStatus.Succeeded, final.Status);
    }

    [Fact]
    public async Task CancelRunAsync_ReturnsLeaseContention_WhenLeaseUnavailableWithinRetryWindow()
    {
        // MEDIUM regression: before the fix, a lease-contention scenario on the admin cancel
        // path surfaced as a TimeoutException / 500. The outcome is now shaped as
        // LeaseContention, which the admin endpoint translates into a retriable 409 instead
        // of leaking an internal exception to the caller.
        var harness = new OrchestrationTestHarness();
        var definition = BuildSingleStepDefinition(harness.Clock.GetUtcNow(), retryPolicy: null, WorkflowStepFailurePolicy.Fail);
        await harness.Definitions.TryCreateAsync(definition);
        var run = await harness.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Operator);

        // Hold the lease for longer than the cancel retry window (30 attempts × 100 ms = 3 s).
        const string leaseHolder = "blocking-reconciler";
        Assert.True(await harness.RunStore.TryAcquireLeaseAsync(
            run.RunId,
            leaseHolder,
            TimeSpan.FromMinutes(1)));

        try
        {
            var outcome = await harness.Engine.CancelRunAsync(run.RunId);
            Assert.Equal(WorkflowCancellationOutcome.LeaseContention, outcome);

            // Run must not have been mutated — a retry with the lease released should succeed.
            var unchanged = await harness.RunStore.GetAsync(run.RunId);
            Assert.NotEqual(WorkflowRunStatus.Cancelled, unchanged!.Status);
        }
        finally
        {
            await harness.RunStore.ReleaseLeaseAsync(run.RunId, leaseHolder);
        }

        var retry = await harness.Engine.CancelRunAsync(run.RunId);
        Assert.Equal(WorkflowCancellationOutcome.CancellationRequested, retry);
    }

    [Fact]
    public async Task CancelRunAsync_CascadesCancelToUnderlyingJobAndMarksStepCancelled()
    {
        // Admin cancel must cascade through the substrate so a running child job does not
        // outlive the parent workflow. The engine should call CancelJobAsync on every
        // non-terminal step and transition the run to Cancelled.
        var harness = new OrchestrationTestHarness();
        var definition = BuildSingleStepDefinition(harness.Clock.GetUtcNow(), retryPolicy: null, WorkflowStepFailurePolicy.Fail);
        await harness.Definitions.TryCreateAsync(definition);
        var run = await harness.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Operator);

        // Tick to submit the single step and get the job id.
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var submitted = await harness.RunStore.GetAsync(run.RunId);
        var jobId = submitted!.StepStates[0].JobId;
        Assert.False(string.IsNullOrEmpty(jobId));

        // Admin cancels the run.
        var outcome = await harness.Engine.CancelRunAsync(run.RunId);
        Assert.Equal(WorkflowCancellationOutcome.CancellationRequested, outcome);

        // Reconcile must cascade cancel to the substrate and finalise the step.
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);

        Assert.Contains(jobId!, harness.JobService.Cancelled);
        var final = await harness.RunStore.GetAsync(run.RunId);
        Assert.Equal(WorkflowRunStatus.Cancelled, final!.Status);
        Assert.Equal(WorkflowStepStatus.Cancelled, final.StepStates[0].Status);
    }

    [Fact]
    public async Task CancelRunAsync_IsIdempotentAndReportsAlreadyCancelled()
    {
        var harness = new OrchestrationTestHarness();
        var definition = BuildSingleStepDefinition(harness.Clock.GetUtcNow(), retryPolicy: null, WorkflowStepFailurePolicy.Fail);
        await harness.Definitions.TryCreateAsync(definition);
        var run = await harness.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Operator);

        Assert.Equal(WorkflowCancellationOutcome.CancellationRequested, await harness.Engine.CancelRunAsync(run.RunId));
        Assert.Equal(WorkflowCancellationOutcome.AlreadyCancelled, await harness.Engine.CancelRunAsync(run.RunId));
    }

    [Fact]
    public async Task CancelRunAsync_ReturnsNotFoundForUnknownRun()
    {
        var harness = new OrchestrationTestHarness();
        Assert.Equal(WorkflowCancellationOutcome.NotFound, await harness.Engine.CancelRunAsync("missing"));
    }

    [Fact]
    public async Task CancelRunAsync_ReturnsAlreadyTerminalForSucceededRun()
    {
        var harness = new OrchestrationTestHarness();
        var definition = BuildSingleStepDefinition(harness.Clock.GetUtcNow(), retryPolicy: null, WorkflowStepFailurePolicy.Fail);
        await harness.Definitions.TryCreateAsync(definition);
        var run = await harness.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Operator);

        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var step = (await harness.RunStore.GetAsync(run.RunId))!.StepStates[0];
        harness.JobService.Complete(step.JobId!);
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);

        Assert.Equal(WorkflowCancellationOutcome.AlreadyTerminal, await harness.Engine.CancelRunAsync(run.RunId));
    }

    [Fact]
    public async Task ReconcileWorkflowRun_TransientObservationFailureDoesNotTerminaliseStep()
    {
        // Regression: a transient GetJobAsync exception must not surface as a permanent step
        // failure. The state should be preserved so the next reconcile observes the actual
        // job outcome. After the transport heals, the step completes normally.
        var harness = new OrchestrationTestHarness();
        var definition = BuildSingleStepDefinition(harness.Clock.GetUtcNow(), retryPolicy: null, WorkflowStepFailurePolicy.Fail);
        await harness.Definitions.TryCreateAsync(definition);
        var run = await harness.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Operator);

        // Tick 1: submit.
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var submitted = (await harness.RunStore.GetAsync(run.RunId))!.StepStates[0];
        Assert.Equal(WorkflowStepStatus.Queued, submitted.Status);

        // Tick 2: inject a transient observation failure. Step must stay Queued with no error.
        harness.JobService.NextGetJobFailure = new InvalidOperationException("transient network blip");
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);

        var afterTransient = await harness.RunStore.GetAsync(run.RunId);
        var transientStep = afterTransient!.StepStates[0];
        Assert.Equal(WorkflowStepStatus.Queued, transientStep.Status);
        Assert.Null(transientStep.CompletedAt);
        Assert.Null(transientStep.ErrorMessage);
        Assert.NotEqual(WorkflowRunStatus.Failed, afterTransient.Status);

        // Tick 3: transport recovers, job actually completed upstream; run finalises.
        harness.JobService.Complete(transientStep.JobId!);
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);

        var final = await harness.RunStore.GetAsync(run.RunId);
        Assert.Equal(WorkflowStepStatus.Succeeded, final!.StepStates[0].Status);
        Assert.Equal(WorkflowRunStatus.Succeeded, final.Status);
    }

    [Fact]
    public async Task CancelRunAsync_KeepsRunInActiveReconcileSetUntilStepsTerminate()
    {
        // Regression: before the fix, CancelRunAsync marked the run Cancelled which made
        // the run-store treat it as terminal and drop it from the active reconcile index.
        // The reconcile loop then never cascaded cancellation to the queued child job.
        // After the fix, a run stays in the active set until every step has also reached
        // a terminal state, so the background reconciler still sees it.
        var harness = new OrchestrationTestHarness();
        var definition = BuildSingleStepDefinition(harness.Clock.GetUtcNow(), retryPolicy: null, WorkflowStepFailurePolicy.Fail);
        await harness.Definitions.TryCreateAsync(definition);
        var run = await harness.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Operator);

        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var submitted = (await harness.RunStore.GetAsync(run.RunId))!;
        Assert.Equal(WorkflowStepStatus.Queued, submitted.StepStates[0].Status);

        var cancelled = await harness.Engine.CancelRunAsync(run.RunId);
        Assert.Equal(WorkflowCancellationOutcome.CancellationRequested, cancelled);

        // Run is Cancelled but its single step is still Queued. Because the step is not
        // terminal, the reconciler MUST still receive this run from ListActiveAsync.
        var activeBeforeCascade = await harness.RunStore.ListActiveAsync();
        Assert.Contains(activeBeforeCascade, r => r.RunId == run.RunId);

        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);

        var final = (await harness.RunStore.GetAsync(run.RunId))!;
        Assert.Equal(WorkflowRunStatus.Cancelled, final.Status);
        Assert.Equal(WorkflowStepStatus.Cancelled, final.StepStates[0].Status);

        var activeAfterCascade = await harness.RunStore.ListActiveAsync();
        Assert.DoesNotContain(activeAfterCascade, r => r.RunId == run.RunId);
    }

    [Fact]
    public async Task CancelRunAsync_SerialisesAgainstReconcileLease()
    {
        // Regression: CancelRunAsync previously did a read-modify-write without taking
        // the reconcile lease, so a concurrent reconcile could clobber the Cancelled
        // status back to Running/Succeeded after cancel had already returned success.
        // Verify the cancel path now blocks on the lease and resumes cleanly after the
        // lease is released.
        var harness = new OrchestrationTestHarness();
        var definition = BuildSingleStepDefinition(harness.Clock.GetUtcNow(), retryPolicy: null, WorkflowStepFailurePolicy.Fail);
        await harness.Definitions.TryCreateAsync(definition);
        var run = await harness.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Operator);

        // Simulate an in-flight reconcile by taking the lease out-of-band.
        const string leaseHolder = "test-reconciler";
        Assert.True(await harness.RunStore.TryAcquireLeaseAsync(
            run.RunId,
            leaseHolder,
            TimeSpan.FromSeconds(30)));

        var cancelTask = Task.Run(() => harness.Engine.CancelRunAsync(run.RunId));
        await Assert.ThrowsAsync<TimeoutException>(
            () => cancelTask.WaitAsync(TimeSpan.FromMilliseconds(200)));

        await harness.RunStore.ReleaseLeaseAsync(run.RunId, leaseHolder);

        var outcome = await cancelTask;
        Assert.Equal(WorkflowCancellationOutcome.CancellationRequested, outcome);

        var final = await harness.RunStore.GetAsync(run.RunId);
        Assert.Equal(WorkflowRunStatus.Cancelled, final!.Status);
    }

    [Fact]
    public async Task ReconcileWorkflowRun_SubmitsJobWithTimeoutSecondsMetadataWhenConfigured()
    {
        // Regression: WorkflowStepDefinition.TimeoutSeconds was documented as surfaced to
        // the underlying job substrate but was not plumbed into the protocol metadata.
        // Verify it is now published so substrate adapters and cost/quota controls can
        // consume it.
        var harness = new OrchestrationTestHarness();
        var definition = BuildSingleStepDefinitionWithTimeout(
            harness.Clock.GetUtcNow(),
            timeoutSeconds: 600);
        await harness.Definitions.TryCreateAsync(definition);
        var run = await harness.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Operator);

        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);

        var metadata = Assert.Single(harness.JobService.SubmittedMetadata);
        Assert.NotNull(metadata);
        Assert.Equal("600", metadata["orchestration.timeoutSeconds"]);
    }

    [Fact]
    public async Task ReconcileWorkflowRun_OmitsTimeoutSecondsMetadataWhenNotConfigured()
    {
        // Null or non-positive TimeoutSeconds must not stamp a bogus key — the contract
        // is "omitted when not set" so adapters can use key presence to short-circuit.
        var harness = new OrchestrationTestHarness();
        var definition = BuildSingleStepDefinition(harness.Clock.GetUtcNow(), retryPolicy: null, WorkflowStepFailurePolicy.Fail);
        await harness.Definitions.TryCreateAsync(definition);
        var run = await harness.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Operator);

        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);

        var metadata = Assert.Single(harness.JobService.SubmittedMetadata);
        Assert.NotNull(metadata);
        Assert.False(metadata.ContainsKey("orchestration.timeoutSeconds"));
    }

    [Fact]
    public async Task CancelRunAsync_RecordsWarningWhenCascadeCancelThrows()
    {
        // When the substrate rejects the cascade cancel, the step must NOT be marked Cancelled
        // because the underlying job may still be running. The next reconcile tick retries the
        // cancel; only once it succeeds does the step transition to Cancelled.
        var harness = new OrchestrationTestHarness();
        var definition = BuildSingleStepDefinition(harness.Clock.GetUtcNow(), retryPolicy: null, WorkflowStepFailurePolicy.Fail);
        await harness.Definitions.TryCreateAsync(definition);
        var run = await harness.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Operator);
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);

        await harness.Engine.CancelRunAsync(run.RunId);
        harness.JobService.NextCancelJobFailure = new InvalidOperationException("substrate down");
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);

        var afterFailedCancel = await harness.RunStore.GetAsync(run.RunId);
        Assert.Equal(WorkflowRunStatus.Cancelled, afterFailedCancel!.Status);
        Assert.Equal(WorkflowStepStatus.Queued, afterFailedCancel.StepStates[0].Status);
        Assert.Contains(afterFailedCancel.Warnings, w => w.Contains("failed to cancel underlying job", StringComparison.OrdinalIgnoreCase));

        // Retry: cancel succeeds; step transitions to Cancelled.
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);

        var final = await harness.RunStore.GetAsync(run.RunId);
        Assert.Equal(WorkflowRunStatus.Cancelled, final!.Status);
        Assert.Equal(WorkflowStepStatus.Cancelled, final.StepStates[0].Status);
    }

    [Fact]
    public async Task ReconcileWorkflowRun_FinalisesLiveSteps_WhenCancelledRunDefinitionDeleted()
    {
        var harness = new OrchestrationTestHarness();
        var definition = BuildSingleStepDefinition(harness.Clock.GetUtcNow(), retryPolicy: null, WorkflowStepFailurePolicy.Fail);
        await harness.Definitions.TryCreateAsync(definition);
        var run = await harness.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Operator);

        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var submitted = (await harness.RunStore.GetAsync(run.RunId))!;
        Assert.Equal(WorkflowStepStatus.Queued, submitted.StepStates[0].Status);

        await harness.Engine.CancelRunAsync(run.RunId);
        await harness.Definitions.DeleteAsync(definition.WorkflowId);

        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);

        var final = await harness.RunStore.GetAsync(run.RunId);
        Assert.Equal(WorkflowRunStatus.Cancelled, final!.Status);
        Assert.All(final.StepStates, s => Assert.True(
            s.Status is WorkflowStepStatus.Cancelled
                or WorkflowStepStatus.Failed
                or WorkflowStepStatus.Succeeded
                or WorkflowStepStatus.Skipped,
            $"Step {s.StepId} left in non-terminal state {s.Status}."));
        var active = await harness.RunStore.ListActiveAsync();
        Assert.DoesNotContain(active, r => r.RunId == run.RunId);
    }

    [Fact]
    public void OrchestrationSystemPrincipal_IncludesAdminRole()
    {
        // The orchestration engine is a trusted internal component. Its synthetic principal
        // must carry the admin role so child-job cancel bypasses per-job approval gates that
        // protect interactive callers.
        var principal = OrchestrationSystemPrincipal.Create("some-operator");
        Assert.True(principal.IsInRole("admin"));
        Assert.True(principal.IsInRole("orchestrator"));
    }

    [Fact]
    public void OrchestrationSystemPrincipal_FallsBackToSystemIdentityWhenRequestedByIsNull()
    {
        var principal = OrchestrationSystemPrincipal.Create(null);
        Assert.Equal(OrchestrationSystemPrincipal.SystemIdentityName, principal.Identity!.Name);
        Assert.True(principal.IsInRole("admin"));
    }

    [Fact]
    public async Task ReconcileWorkflowRun_ClearsStartedAtOnRetry_SoNextAttemptStampsFresh()
    {
        var harness = new OrchestrationTestHarness();
        var retryPolicy = new StepRetryPolicy
        {
            MaxAttempts = 2,
            InitialDelaySeconds = 5,
            BackoffMultiplier = 1.0,
            MaxDelaySeconds = 60
        };
        var definition = BuildSingleStepDefinition(harness.Clock.GetUtcNow(), retryPolicy, WorkflowStepFailurePolicy.Fail);
        await harness.Definitions.TryCreateAsync(definition);
        var run = await harness.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Operator);

        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var afterSubmit = await harness.RunStore.GetAsync(run.RunId);
        var step = afterSubmit!.StepStates[0];
        var firstStartedAt = step.StartedAt;
        Assert.NotNull(firstStartedAt);

        harness.JobService.Fail(step.JobId!, "transient");
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);

        var afterRetryScheduled = await harness.RunStore.GetAsync(run.RunId);
        var retryStep = afterRetryScheduled!.StepStates[0];
        Assert.Equal(WorkflowStepStatus.Pending, retryStep.Status);
        Assert.Null(retryStep.StartedAt);
        Assert.Null(retryStep.JobId);

        harness.Clock.Advance(TimeSpan.FromSeconds(10));
        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);

        var afterResubmit = await harness.RunStore.GetAsync(run.RunId);
        var resubmittedStep = afterResubmit!.StepStates[0];
        Assert.NotNull(resubmittedStep.StartedAt);
        Assert.NotEqual(firstStartedAt, resubmittedStep.StartedAt);
    }

    [Fact]
    public async Task ReconcileWorkflowRun_IdempotentReplay_SucceededJobFetchesArtifacts()
    {
        var harness = new OrchestrationTestHarness();
        var definition = BuildChainedDefinition(harness.Clock.GetUtcNow());
        await harness.Definitions.TryCreateAsync(definition);
        var run = await harness.Engine.CreateRunAsync(definition, WorkflowTriggerKind.Manual, Operator);

        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var afterSubmit = await harness.RunStore.GetAsync(run.RunId);
        var stepA = afterSubmit!.StepStates.Single(s => s.StepId == "a");
        Assert.Equal(WorkflowStepStatus.Queued, stepA.Status);

        harness.JobService.Complete(stepA.JobId!, new[]
        {
            new ArtifactRef
            {
                ArtifactId = "art-1",
                Kind = ArtifactKind.FeatureLayer,
                Label = "primary",
                Uri = "s3://bucket/a.parquet"
            }
        });

        var crashedRun = afterSubmit with
        {
            StepStates = afterSubmit.StepStates
                .Select(s => s.StepId == "a"
                    ? s with { Status = WorkflowStepStatus.Pending, AttemptCount = 0, JobId = null, StartedAt = null }
                    : s)
                .ToArray()
        };
        await harness.RunStore.SetAsync(crashedRun);

        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);

        var afterReplay = await harness.RunStore.GetAsync(run.RunId);
        var replayedA = afterReplay!.StepStates.Single(s => s.StepId == "a");
        Assert.Equal(WorkflowStepStatus.Succeeded, replayedA.Status);
        Assert.NotNull(replayedA.CompletedAt);
        Assert.NotNull(replayedA.OutputArtifacts);
        Assert.Single(replayedA.OutputArtifacts!);
    }

    [Fact]
    public async Task ReconcileWorkflowRun_IdempotentReplay_FailedJobAppliesRetryPolicy()
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

        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);
        var afterSubmit = await harness.RunStore.GetAsync(run.RunId);
        var step = afterSubmit!.StepStates[0];

        harness.JobService.Fail(step.JobId!, "transient");

        var crashedRun = afterSubmit with
        {
            StepStates = afterSubmit.StepStates
                .Select(s => s with { Status = WorkflowStepStatus.Pending, AttemptCount = 0, JobId = null, StartedAt = null })
                .ToArray()
        };
        await harness.RunStore.SetAsync(crashedRun);

        await harness.Engine.ReconcileWorkflowRunAsync(run.RunId);

        var afterReplay = await harness.RunStore.GetAsync(run.RunId);
        var replayedStep = afterReplay!.StepStates[0];
        Assert.Equal(WorkflowStepStatus.Pending, replayedStep.Status);
        Assert.NotNull(replayedStep.NextAttemptAt);
        Assert.Null(replayedStep.CompletedAt);
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

    private static WorkflowDefinition BuildSingleStepDefinitionWithTimeout(
        DateTimeOffset now,
        int timeoutSeconds)
        => new()
        {
            WorkflowId = "wf-single-timeout",
            Name = "single-timeout",
            Steps = new[]
            {
                new WorkflowStepDefinition
                {
                    StepId = "a",
                    Plan = BuildPlan("plan-a"),
                    TimeoutSeconds = timeoutSeconds
                }
            },
            CreatedAt = now,
            UpdatedAt = now
        };

    private static WorkflowDefinition BuildTwoStepDefinition(
        DateTimeOffset now,
        WorkflowStepFailurePolicy firstFailurePolicy,
        StepRetryPolicy? firstRetryPolicy,
        bool bindDependentOnUpstream = false)
    {
        var bindings = bindDependentOnUpstream
            ? new[]
            {
                new StepInputBinding
                {
                    SourceStepId = "a",
                    SourceArtifactSelector = "artifact:0",
                    TargetInputKey = "upstream_uri"
                }
            }
            : Array.Empty<StepInputBinding>();

        return new()
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
                    Plan = BuildPlan("plan-b"),
                    InputBindings = bindings
                }
            },
            CreatedAt = now,
            UpdatedAt = now
        };
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

    private sealed class OrchestrationTestHarness
    {
        public OrchestrationTestHarness()
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
