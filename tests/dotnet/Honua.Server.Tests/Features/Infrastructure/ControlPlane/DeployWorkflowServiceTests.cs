// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class DeployWorkflowServiceTests
{
    [Fact]
    public async Task CreateAsync_WithDuplicateIdempotencyKey_StartsBackendOnlyOnce()
    {
        var store = new TestWorkflowOperationStore();
        var backend = new BlockingDeployBackend();
        var service = CreateService(store, backend);

        var firstCreate = service.CreateAsync(
            "prod-api",
            "sha256:abc123",
            "sha256:old",
            "alice",
            "Initial submit",
            "release / 2026#03 ? candidate",
            "corr-1",
            OperationPriority.Normal,
            submitImmediately: true,
            parameterOverrides: null);

        await backend.StartEntered.WaitAsync(TimeSpan.FromSeconds(5));

        var secondCreate = await service.CreateAsync(
            "prod-api",
            "sha256:abc123",
            "sha256:old",
            "alice",
            "Duplicate submit",
            "release / 2026#03 ? candidate",
            "corr-2",
            OperationPriority.Normal,
            submitImmediately: true,
            parameterOverrides: null);

        secondCreate.Should().NotBeNull();
        secondCreate!.Status.Should().Be(WorkflowOperationStatus.Planned);

        backend.AllowStart();
        var firstResult = await firstCreate;

        firstResult.Should().NotBeNull();
        firstResult!.Status.Should().Be(WorkflowOperationStatus.Submitted);
        secondCreate.OperationId.Should().Be(firstResult.OperationId);
        backend.StartCount.Should().Be(1);

        var persisted = await store.GetAsync(firstResult.OperationId);
        persisted!.ProviderOperationId.Should().Be($"test-backend:{firstResult.OperationId}");
    }

    [Fact]
    public async Task CreateAsync_WithEquivalentParameterOverridesInDifferentOrder_DeduplicatesRequest()
    {
        var store = new TestWorkflowOperationStore();
        var backend = new BlockingDeployBackend();
        var service = CreateService(store, backend);
        var firstOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["featureFlag"] = "on",
            ["region"] = "us-west-2"
        };
        var secondOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["region"] = "us-west-2",
            ["featureFlag"] = "on"
        };

        var firstCreate = service.CreateAsync(
            "prod-api",
            "sha256:abc123",
            "sha256:old",
            "alice",
            "Initial submit",
            "release-2026-04-15",
            "corr-1",
            OperationPriority.Normal,
            submitImmediately: true,
            parameterOverrides: firstOverrides);

        await backend.StartEntered.WaitAsync(TimeSpan.FromSeconds(5));

        var secondCreate = await service.CreateAsync(
            "prod-api",
            "sha256:abc123",
            "sha256:old",
            "alice",
            "Duplicate submit",
            "release-2026-04-15",
            "corr-2",
            OperationPriority.Normal,
            submitImmediately: true,
            parameterOverrides: secondOverrides);

        backend.AllowStart();
        var firstResult = await firstCreate;

        secondCreate.Should().NotBeNull();
        firstResult.Should().NotBeNull();
        secondCreate!.OperationId.Should().Be(firstResult!.OperationId);
        backend.StartCount.Should().Be(1);
    }

    [Fact]
    public async Task CreateAsync_WhenStoreRejectsCreate_DoesNotStartBackend()
    {
        var store = new TestWorkflowOperationStore
        {
            AllowCreate = false
        };
        var backend = new ImmediateDeployBackend();
        var service = CreateService(store, backend);

        var act = () => service.CreateAsync(
            "prod-api",
            "sha256:abc123",
            null,
            "alice",
            "Initial submit",
            "reject-me",
            null,
            OperationPriority.Normal,
            submitImmediately: true,
            parameterOverrides: null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*durably record*");
        backend.StartCount.Should().Be(0);
    }

    [Fact]
    public async Task CreateAsync_NormalizesUnsafeIdempotencyKeysIntoSafeOperationIds()
    {
        var store = new TestWorkflowOperationStore();
        var backend = new ImmediateDeployBackend();
        var service = CreateService(store, backend);

        var operation = await service.CreateAsync(
            "prod-api",
            "sha256:abc123",
            null,
            "alice",
            "Normalize key",
            " release / 2026#03 ? candidate ",
            null,
            OperationPriority.Normal,
            submitImmediately: false,
            parameterOverrides: null);

        operation.Should().NotBeNull();
        operation!.OperationId.Should().MatchRegex("^deploy-[a-z0-9-]+$");
        operation.OperationId.Should().NotContain("/");
        operation.OperationId.Should().NotContain("?");
        operation.OperationId.Should().NotContain("#");
        operation.OperationId.Should().NotContain(" ");
        operation.OperationId.Length.Should().BeLessThan(80);
    }

    [Fact]
    public async Task CreateAsync_PersistsObservedRevisionReturnedByBackendSubmission()
    {
        var store = new TestWorkflowOperationStore();
        var backend = new ImmediateDeployBackend
        {
            SubmissionObservedRevision = "42"
        };
        var service = CreateService(store, backend);

        var operation = await service.CreateAsync(
            "prod-api",
            "43",
            null,
            "alice",
            "Promote alias",
            "lambda-submit",
            null,
            OperationPriority.Normal,
            submitImmediately: true,
            parameterOverrides: null);

        operation.Should().NotBeNull();
        operation!.Deploy!.CurrentRevision.Should().Be("42");

        var persisted = await store.GetAsync(operation.OperationId);
        persisted!.Deploy!.CurrentRevision.Should().Be("42");
    }

    [Fact]
    public async Task CreateAsync_CanonicalApprovalRequired_PreventsImmediateSubmission()
    {
        // Scenario: static RequiresApproval=false, canonical evaluator says Required.
        // The backend returns IsReadyToSubmit=true because it only sees the spec flag.
        // The bridged approval must also clear IsReadyToSubmit so CreateAsync does not auto-submit.
        var store = new TestWorkflowOperationStore();
        var backend = new ImmediateDeployBackend();
        var approval = ApprovalRequirement.Required("operator.publish", "canonical-policy-gate");
        var service = CreateService(store, backend, new StubApprovalEvaluator(approval));
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "alice")], "Test"));

        var operation = await service.CreateAsync(
            "prod-api",
            "sha256:abc123",
            "sha256:old",
            "alice",
            "Ship it",
            null,
            null,
            OperationPriority.Normal,
            submitImmediately: true,
            parameterOverrides: null,
            principal: principal);

        operation.Should().NotBeNull();
        operation!.Status.Should().Be(WorkflowOperationStatus.AwaitingApproval);
        operation.Audit.ApprovalPolicyRef.Should().Be("operator.publish");
        backend.StartCount.Should().Be(0, "backend should not be called when approval is required");
    }

    [Fact]
    public async Task PlanAsync_CanonicalApprovalRequired_MarksNotReadyToSubmit()
    {
        var store = new TestWorkflowOperationStore();
        var backend = new ImmediateDeployBackend();
        var approval = ApprovalRequirement.Required("operator.publish", "canonical-policy-gate");
        var service = CreateService(store, backend, new StubApprovalEvaluator(approval));
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "alice")], "Test"));

        var plan = await service.PlanAsync(
            "prod-api",
            "sha256:abc123",
            "sha256:old",
            parameterOverrides: null,
            principal: principal);

        plan.Should().NotBeNull();
        plan!.Plan.RequiresApproval.Should().BeTrue();
        plan.Plan.IsReadyToSubmit.Should().BeFalse("canonical approval must clear ready-to-submit");
    }

    [Fact]
    public async Task RequestRollbackAsync_OnSubmittedOperation_UpdatesAuditFields()
    {
        var store = new TestWorkflowOperationStore();
        var backend = new ImmediateDeployBackend();
        var service = CreateService(store, backend);
        var operation = CreateOperationRecord(
            status: WorkflowOperationStatus.Submitted,
            requestedBy: "alice",
            reason: "Ship it");

        await store.TryCreateAsync(operation);

        var rolledBack = await service.RequestRollbackAsync(
            operation.OperationId,
            requestedBy: "bob",
            reason: "Smoke test failed");

        rolledBack.Should().NotBeNull();
        rolledBack!.Status.Should().Be(WorkflowOperationStatus.RollbackRequested);
        rolledBack.Audit.RequestedBy.Should().Be("bob");
        rolledBack.Audit.Reason.Should().Be("Smoke test failed");
    }

    [Fact]
    public async Task Reconciler_TransitionsSubmittedOperation_ToSucceeded()
    {
        var store = new TestWorkflowOperationStore();
        var backend = new ProgressingDeployBackend();
        var operation = CreateOperationRecord(status: WorkflowOperationStatus.Submitted);
        await store.TryCreateAsync(operation);

        var reconciler = CreateReconciler(store, backend);

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var reconciling = await store.GetAsync(operation.OperationId);
        reconciling!.Status.Should().Be(WorkflowOperationStatus.Reconciling);

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var succeeded = await store.GetAsync(operation.OperationId);
        succeeded!.Status.Should().Be(WorkflowOperationStatus.Succeeded);
        succeeded.CompletedAt.Should().NotBeNull();
        succeeded.Deploy!.CurrentRevision.Should().Be("sha256:old");
        succeeded.ObservedState.Should().Be(succeeded.Deploy.DesiredRevision);
    }

    [Fact]
    public async Task Reconciler_TransitionsRollbackRequestedOperation_ToRolledBack()
    {
        var store = new TestWorkflowOperationStore();
        var backend = new ProgressingDeployBackend();
        var operation = CreateOperationRecord(status: WorkflowOperationStatus.RollbackRequested);
        await store.TryCreateAsync(operation);

        var reconciler = CreateReconciler(store, backend);

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var rolledBack = await store.GetAsync(operation.OperationId);
        rolledBack!.Status.Should().Be(WorkflowOperationStatus.RolledBack);
        rolledBack.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Reconciler_GenericGitOpsBackend_RequiresManualConfirmation()
    {
        var store = new TestWorkflowOperationStore();
        var backend = new KubernetesGitOpsDeployBackend(NullLogger<KubernetesGitOpsDeployBackend>.Instance);
        var operation = CreateOperationRecord(status: WorkflowOperationStatus.Submitted);
        await store.TryCreateAsync(operation);

        var reconciler = CreateReconciler(store, backend);

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var updated = await store.GetAsync(operation.OperationId);

        updated.Should().NotBeNull();
        updated!.Status.Should().Be(WorkflowOperationStatus.ManualInterventionRequired);
        updated.CurrentPhase.Should().Contain("external GitOps controller");
    }

    [Fact]
    public async Task Reconciler_WhenObservationThrows_MarksOperationForManualIntervention()
    {
        var store = new TestWorkflowOperationStore();
        var backend = new ThrowingObserveBackend();
        var operation = CreateOperationRecord(status: WorkflowOperationStatus.Submitted);
        await store.TryCreateAsync(operation);

        var reconciler = CreateReconciler(store, backend);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reconciler.ReconcileWorkflowOperationAsync(operation.OperationId));

        var updated = await store.GetAsync(operation.OperationId);
        updated.Should().NotBeNull();
        updated!.Status.Should().Be(WorkflowOperationStatus.ManualInterventionRequired);
        updated.CompletedAt.Should().NotBeNull();
        updated.ErrorMessage.Should().Contain(nameof(InvalidOperationException));
    }

    [Fact]
    public async Task Reconciler_WaitsForTelemetryConfirmation_BeforeSettlingSucceededOperation()
    {
        var store = new TestWorkflowOperationStore();
        var backend = new ProgressingDeployBackend();
        var operation = CreateOperationRecord(
            status: WorkflowOperationStatus.Reconciling,
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-10),
            parameters: new Dictionary<string, string>
            {
                ["namespace"] = "honua",
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.error_rate.query"] = "honua_canary_error_rate",
                ["telemetry.error_rate.threshold"] = "0.05",
                ["telemetry.sample_count.query"] = "honua_canary_sample_count",
                ["telemetry.sample_count.minimum"] = "20",
                ["telemetry.latency_p95.query"] = "honua_canary_latency_p95",
                ["telemetry.latency_p95.threshold_ms"] = "2000"
            });
        await store.TryCreateAsync(operation);

        var evaluator = CreateTelemetryEvaluator("""
            {"status":"success","data":{"resultType":"vector","result":[{"metric":{},"value":[1710000000,"25"]}]}}
            """,
            """
            {"status":"success","data":{"resultType":"vector","result":[{"metric":{},"value":[1710000000,"0.01"]}]}}
            """,
            """
            {"status":"success","data":{"resultType":"vector","result":[{"metric":{},"value":[1710000000,"150"]}]}}
            """);
        var reconciler = CreateReconciler(store, backend, evaluator);

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var updated = await store.GetAsync(operation.OperationId);

        updated.Should().NotBeNull();
        updated!.Status.Should().Be(WorkflowOperationStatus.Succeeded);
        updated.CurrentPhase.Should().Contain("Telemetry gate passed");
    }

    [Fact]
    public async Task Reconciler_RequestsRollback_WhenTelemetryBackendDetectsBreach()
    {
        var store = new TestWorkflowOperationStore();
        var backend = new ProgressingDeployBackend();
        var operation = CreateOperationRecord(
            status: WorkflowOperationStatus.Reconciling,
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-10),
            parameters: new Dictionary<string, string>
            {
                ["namespace"] = "honua",
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.error_rate.query"] = "honua_canary_error_rate",
                ["telemetry.error_rate.threshold"] = "0.05",
                ["telemetry.sample_count.query"] = "honua_canary_sample_count",
                ["telemetry.sample_count.minimum"] = "20",
                ["telemetry.latency_p95.query"] = "honua_canary_latency_p95",
                ["telemetry.latency_p95.threshold_ms"] = "2000"
            });
        await store.TryCreateAsync(operation);

        var evaluator = CreateTelemetryEvaluator("""
            {"status":"success","data":{"resultType":"vector","result":[{"metric":{},"value":[1710000000,"25"]}]}}
            """,
            """
            {"status":"success","data":{"resultType":"vector","result":[{"metric":{},"value":[1710000000,"0.25"]}]}}
            """,
            """
            {"status":"success","data":{"resultType":"vector","result":[{"metric":{},"value":[1710000000,"150"]}]}}
            """);
        var reconciler = CreateReconciler(store, backend, evaluator);

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var rollbackRequested = await store.GetAsync(operation.OperationId);

        rollbackRequested.Should().NotBeNull();
        rollbackRequested!.Status.Should().Be(WorkflowOperationStatus.RollbackRequested);
        rollbackRequested.CurrentPhase.Should().Contain("Automatic rollback requested");
    }

    [Fact]
    public async Task Reconciler_PromotesStagedRollout_WhenTelemetryGatePasses_WithoutOverwritingRollbackBaseline()
    {
        var store = new TestWorkflowOperationStore();
        var backend = new StagedDeployBackend();
        var operation = CreateOperationRecord(
            status: WorkflowOperationStatus.Reconciling,
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-10),
            parameters: new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.error_rate.query"] = "honua_canary_error_rate",
                ["telemetry.error_rate.threshold"] = "0.05",
                ["telemetry.sample_count.query"] = "honua_canary_sample_count",
                ["telemetry.sample_count.minimum"] = "20"
            });
        await store.TryCreateAsync(operation);

        var evaluator = CreateTelemetryEvaluator(
            """
            {"status":"success","data":{"resultType":"vector","result":[{"metric":{},"value":[1710000000,"25"]}]}}
            """,
            """
            {"status":"success","data":{"resultType":"vector","result":[{"metric":{},"value":[1710000000,"0.01"]}]}}
            """);
        var reconciler = CreateReconciler(store, backend, evaluator);

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var promoted = await store.GetAsync(operation.OperationId);

        promoted.Should().NotBeNull();
        promoted!.Status.Should().Be(WorkflowOperationStatus.Succeeded);
        promoted.Deploy!.CurrentRevision.Should().Be("sha256:old");
        promoted.ObservedState.Should().Be("sha256:new");
        backend.PromoteCount.Should().Be(1);
    }

    [Fact]
    public async Task Reconciler_ProgressiveCanaryRamp_AdvancesThroughStepsAndPromotesAtFullWeight()
    {
        var store = new TestWorkflowOperationStore();
        var backend = new RecordingStagedDeployBackend();
        var operation = CreateRampOperationRecord(
            stepWeights: [5, 25, 50, 100],
            stepBake: TimeSpan.FromMilliseconds(1));
        await store.TryCreateAsync(operation);

        var reconciler = CreateReconciler(store, backend, AlwaysHealthyTelemetry());

        // Step 0 (5%): first pass initializes the step bake clock without advancing.
        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var afterInit = await store.GetAsync(operation.OperationId);
        afterInit!.Status.Should().Be(WorkflowOperationStatus.Reconciling);
        afterInit.Deploy!.CanaryRamp!.CurrentStepIndex.Should().Be(0);
        afterInit.Deploy.Parameters["deployment.canary_weight_percentage"].Should().Be("5");

        // Drive the remaining steps; each pass bakes (1ms) then the telemetry gate advances.
        for (var pass = 0; pass < 6; pass++)
        {
            await Task.Delay(10);
            await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
            var snapshot = await store.GetAsync(operation.OperationId);
            if (snapshot!.Status == WorkflowOperationStatus.Succeeded)
            {
                break;
            }
        }

        var promoted = await store.GetAsync(operation.OperationId);
        promoted!.Status.Should().Be(WorkflowOperationStatus.Succeeded);
        promoted.ObservedState.Should().Be("sha256:new");
        // Intermediate steps (25, 50) are applied through StartAsync; the terminal step promotes.
        backend.AppliedCanaryWeights.Should().ContainInOrder("25", "50");
        backend.PromoteCount.Should().Be(1);
    }

    [Fact]
    public async Task Reconciler_ProgressiveCanaryRamp_RollsBackWhenTelemetryBreachesAtIntermediateStep()
    {
        var store = new TestWorkflowOperationStore();
        var backend = new RecordingStagedDeployBackend();
        var operation = CreateRampOperationRecord(
            stepWeights: [5, 25, 50, 100],
            stepBake: TimeSpan.FromMilliseconds(1));
        await store.TryCreateAsync(operation);

        // Healthy until the canary reaches an intermediate weight, then the error rate breaches.
        var evaluator = new SwitchingTelemetrySignalEvaluator(breachWhenWeightAtLeast: 25);
        var reconciler = CreateReconciler(store, backend, evaluator);

        WorkflowOperationRecord? snapshot = null;
        for (var pass = 0; pass < 8; pass++)
        {
            await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
            snapshot = await store.GetAsync(operation.OperationId);
            if (snapshot!.Status is WorkflowOperationStatus.RollbackRequested
                or WorkflowOperationStatus.RolledBack
                or WorkflowOperationStatus.Succeeded)
            {
                break;
            }

            await Task.Delay(10);
        }

        snapshot.Should().NotBeNull();
        snapshot!.Status.Should().Be(WorkflowOperationStatus.RollbackRequested);
        snapshot.CurrentPhase.Should().Contain("Automatic rollback requested");
        // Rollback fired before promotion to full traffic.
        backend.PromoteCount.Should().Be(0);
        snapshot.Deploy!.CanaryRamp!.CurrentStepIndex.Should().BeLessThan(3);
    }

    [Fact]
    public async Task Reconciler_WithoutCanaryRamp_PromotesInSingleStep_NoRegression()
    {
        var store = new TestWorkflowOperationStore();
        var backend = new RecordingStagedDeployBackend();
        var operation = CreateOperationRecord(
            status: WorkflowOperationStatus.Reconciling,
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-10),
            parameters: new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.error_rate.query"] = "honua_canary_error_rate",
                ["telemetry.error_rate.threshold"] = "0.05",
                ["telemetry.sample_count.query"] = "honua_canary_sample_count",
                ["telemetry.sample_count.minimum"] = "20"
            });
        operation.Deploy!.CanaryRamp.Should().BeNull("no ramp parameters configured");
        await store.TryCreateAsync(operation);

        var reconciler = CreateReconciler(store, backend, AlwaysHealthyTelemetry());

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var promoted = await store.GetAsync(operation.OperationId);

        promoted!.Status.Should().Be(WorkflowOperationStatus.Succeeded);
        promoted.ObservedState.Should().Be("sha256:new");
        backend.PromoteCount.Should().Be(1, "single-step deploys promote once with no ramp stepping");
        backend.AppliedCanaryWeights.Should().BeEmpty("no intermediate StartAsync calls without a ramp");
    }

    [Fact]
    public async Task ParseCanaryRamp_FromDeployParameters_ProducesValidRampAndPinsInitialWeight()
    {
        var store = new TestWorkflowOperationStore();
        var backend = new RecordingStagedDeployBackend();
        var service = CreateService(store, backend);

        var plan = await service.PlanAsync(
            "prod-api",
            "sha256:abc123",
            "sha256:old",
            parameterOverrides: new Dictionary<string, string>
            {
                ["deployment.canary_ramp.step_weights"] = "5, 25, 50, 100",
                ["deployment.canary_ramp.step_bake_seconds"] = "180",
                ["telemetry.connection"] = "prod-prom"
            });

        plan.Should().NotBeNull();
        plan!.Spec.CanaryRamp.Should().NotBeNull();
        plan.Spec.CanaryRamp!.IsValid.Should().BeTrue();
        plan.Spec.CanaryRamp.StepWeights.Should().Equal(5, 25, 50, 100);
        plan.Spec.CanaryRamp.StepBakeDuration.Should().Be(TimeSpan.FromSeconds(180));
        plan.Spec.Parameters["deployment.canary_weight_percentage"].Should().Be("5");
    }

    [Fact]
    public async Task PlanAsync_WithInvalidCanaryRamp_BlocksSubmission()
    {
        var store = new TestWorkflowOperationStore();
        var backend = new RecordingStagedDeployBackend();
        var service = CreateService(store, backend);

        var plan = await service.PlanAsync(
            "prod-api",
            "sha256:abc123",
            "sha256:old",
            parameterOverrides: new Dictionary<string, string>
            {
                // Not strictly increasing and missing a terminal 100% weight.
                ["deployment.canary_ramp.step_weights"] = "50, 25",
                ["deployment.canary_ramp.step_bake_seconds"] = "60"
            });

        plan.Should().NotBeNull();
        plan!.Plan.IsReadyToSubmit.Should().BeFalse();
        plan.Plan.BlockingReasons.Should().Contain(reason => reason.Contains("canary ramp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Reconciler_WithLongRunningObservation_RenewsLeaseUntilObservationCompletes()
    {
        var store = new TestWorkflowOperationStore();
        var backend = new BlockingObserveBackend();
        var operation = CreateOperationRecord(status: WorkflowOperationStatus.Submitted);
        await store.TryCreateAsync(operation);

        var reconciler = CreateReconciler(store, backend);
        var reconcileTask = reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);

        await backend.ObserveEntered.WaitAsync(TimeSpan.FromSeconds(5));
        await store.RenewLeaseObserved.Task.WaitAsync(TimeSpan.FromSeconds(15));

        store.RenewLeaseCount.Should().BeGreaterThan(0);

        backend.AllowObserve();
        await reconcileTask;
    }

    private static DeployWorkflowService CreateService(
        TestWorkflowOperationStore store,
        IDeployBackend backend,
        IOperatorApprovalEvaluator? approvalEvaluator = null)
        => new(
            new TestDeployTargetRegistry(),
            [store],
            [backend],
            approvalEvaluator ?? new StubApprovalEvaluator(),
            NullLogger<DeployWorkflowService>.Instance);

    private static DeployWorkflowReconciler CreateReconciler(
        TestWorkflowOperationStore store,
        IDeployBackend backend,
        IDeployTelemetrySignalEvaluator? telemetryEvaluator = null)
        => new(
            store,
            new TestDeployTargetRegistry(),
            [backend],
            telemetryEvaluator ?? new StubDeployTelemetrySignalEvaluator(null),
            NullLogger<DeployWorkflowReconciler>.Instance);

    private static WorkflowOperationRecord CreateOperationRecord(
        WorkflowOperationStatus status,
        string requestedBy = "alice",
        string reason = "Ship it",
        DateTimeOffset? createdAt = null,
        IReadOnlyDictionary<string, string>? parameters = null)
    {
        var now = createdAt ?? DateTimeOffset.UtcNow;
        return new WorkflowOperationRecord
        {
            OperationId = $"deploy-{Guid.NewGuid():N}",
            Kind = WorkflowOperationKind.Deploy,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Submitted to deploy backend.",
            Audit = new OperationAuditInfo
            {
                RequestedBy = requestedBy,
                Reason = reason,
                IdempotencyKey = Guid.NewGuid().ToString("N")
            },
            Concurrency = new OperationConcurrencyPolicy
            {
                PartitionKey = "production:prod-api",
                RequiresExclusiveLease = true
            },
            Deploy = new DeployOperationSpec
            {
                TargetId = "prod-api",
                TargetKind = DeployTargetKind.Kubernetes,
                Backend = "honua-gitops-kubernetes",
                Environment = "production",
                TargetName = "honua-server",
                ArtifactReference = "ghcr.io/honua/server",
                RuntimeProfile = "dotnet-api",
                CurrentRevision = "sha256:old",
                DesiredRevision = "sha256:new",
                Parameters = parameters ?? new Dictionary<string, string>
                {
                    ["namespace"] = "honua"
                }
            }
        };
    }

    private static WorkflowOperationRecord CreateRampOperationRecord(
        IReadOnlyList<int> stepWeights,
        TimeSpan stepBake)
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-10);
        return new WorkflowOperationRecord
        {
            OperationId = $"deploy-{Guid.NewGuid():N}",
            Kind = WorkflowOperationKind.Deploy,
            Status = WorkflowOperationStatus.Reconciling,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Submitted to deploy backend.",
            Audit = new OperationAuditInfo
            {
                RequestedBy = "alice",
                Reason = "Ship it",
                IdempotencyKey = Guid.NewGuid().ToString("N")
            },
            Concurrency = new OperationConcurrencyPolicy
            {
                PartitionKey = "production:prod-api",
                RequiresExclusiveLease = true
            },
            Deploy = new DeployOperationSpec
            {
                TargetId = "prod-api",
                TargetKind = DeployTargetKind.Kubernetes,
                Backend = "honua-gitops-kubernetes",
                Environment = "production",
                TargetName = "honua-server",
                ArtifactReference = "ghcr.io/honua/server",
                RuntimeProfile = "dotnet-api",
                CurrentRevision = "sha256:old",
                DesiredRevision = "sha256:new",
                Parameters = new Dictionary<string, string>
                {
                    ["deployment.canary_weight_percentage"] = stepWeights[0].ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["telemetry.connection"] = "prod-prom",
                    ["telemetry.error_rate.query"] = "honua_canary_error_rate",
                    ["telemetry.error_rate.threshold"] = "0.05",
                    ["telemetry.sample_count.query"] = "honua_canary_sample_count",
                    ["telemetry.sample_count.minimum"] = "20"
                },
                CanaryRamp = new CanaryRampSpec
                {
                    StepWeights = stepWeights,
                    StepBakeDuration = stepBake
                }
            }
        };
    }

    private static StubDeployTelemetrySignalEvaluator AlwaysHealthyTelemetry()
        => new(new DeployTelemetryDecision { Message = "Telemetry gate passed: error-rate signal is within threshold." });

    private static PrometheusDeployTelemetrySignalEvaluator CreateTelemetryEvaluator(params string[] responses)
    {
        var responseQueue = new ConcurrentQueue<string>(responses);
        var optionsMonitor = new TestControlPlaneOptionsMonitor(new ControlPlaneOptions
        {
            TelemetryConnections =
            [
                new DeployTelemetryConnectionOptions
                {
                    ConnectionId = "prod-prom",
                    Provider = "prometheus",
                    BaseUrl = "https://example.com",
                    TimeoutSeconds = 2
                }
            ]
        });

        return new PrometheusDeployTelemetrySignalEvaluator(
            optionsMonitor,
            new StubHttpClientFactory(new HttpClient(new DelegateHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        responseQueue.TryDequeue(out var responseJson)
                            ? responseJson
                            : """{"status":"success","data":{"resultType":"vector","result":[]}}""",
                        Encoding.UTF8,
                        "application/json")
                }))),
            NullLogger<PrometheusDeployTelemetrySignalEvaluator>.Instance);
    }

    private sealed class TestDeployTargetRegistry : IDeployTargetRegistry
    {
        private static readonly DeployTargetDefinition Target = new()
        {
            TargetId = "prod-api",
            TargetKind = DeployTargetKind.Kubernetes,
            Backend = "honua-gitops-kubernetes",
            Environment = "production",
            TargetName = "honua-server",
            ArtifactReference = "ghcr.io/honua/server",
            RuntimeProfile = "dotnet-api",
            Parameters = new Dictionary<string, string>
            {
                ["namespace"] = "honua"
            }
        };

        public Task<IReadOnlyList<DeployTargetDefinition>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DeployTargetDefinition>>([Target]);

        public Task<DeployTargetDefinition?> GetAsync(string targetId, CancellationToken cancellationToken = default)
            => Task.FromResult(targetId == Target.TargetId ? Target : null);
    }

    private sealed class StubDeployTelemetrySignalEvaluator(DeployTelemetryDecision? decision) : IDeployTelemetrySignalEvaluator
    {
        public Task<DeployTelemetryDecision?> EvaluateAsync(
            WorkflowOperationRecord operation,
            CancellationToken cancellationToken = default)
            => Task.FromResult(decision);
    }

    private sealed class TestControlPlaneOptionsMonitor(ControlPlaneOptions currentValue) : IOptionsMonitor<ControlPlaneOptions>
    {
        public ControlPlaneOptions CurrentValue => currentValue;

        public ControlPlaneOptions Get(string? name) => currentValue;

        public IDisposable? OnChange(Action<ControlPlaneOptions, string?> listener) => null;
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }

    private sealed class TestWorkflowOperationStore : IWorkflowOperationStore
    {
        private readonly ConcurrentDictionary<string, WorkflowOperationRecord> _operations = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _metadataPackageIndex = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _leases = new(StringComparer.Ordinal);

        public int RenewLeaseCount => _renewLeaseCount;

        public TaskCompletionSource<bool> RenewLeaseObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool AllowCreate { get; set; } = true;

        private int _renewLeaseCount;

        public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(_leases.TryAdd(operationId, ownerId));

        public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        {
            var renewed = _leases.TryGetValue(operationId, out var currentOwner) && currentOwner == ownerId;
            if (renewed)
            {
                Interlocked.Increment(ref _renewLeaseCount);
                RenewLeaseObserved.TrySetResult(true);
            }

            return Task.FromResult(renewed);
        }

        public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
        {
            _leases.TryRemove(new KeyValuePair<string, string>(operationId, ownerId));
            return Task.CompletedTask;
        }

        public Task<bool> TryCreateAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            if (!AllowCreate)
            {
                return Task.FromResult(false);
            }

            var created = _operations.TryAdd(operation.OperationId, operation);
            if (created)
            {
                IndexMetadataReleaseOperation(operation, createOnly: true);
            }

            return Task.FromResult(created);
        }

        public Task<WorkflowOperationRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult(_operations.TryGetValue(operationId, out var operation) ? operation : null);

        public Task<WorkflowOperationRecord?> GetByMetadataPackageIdAsync(string packageId, CancellationToken cancellationToken = default)
            => Task.FromResult(
                _metadataPackageIndex.TryGetValue(packageId, out var operationId) &&
                _operations.TryGetValue(operationId, out var operation)
                    ? operation
                    : null);

        public Task SetAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _operations[operation.OperationId] = operation;
            IndexMetadataReleaseOperation(operation, createOnly: false);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkflowOperationRecord>> ListActiveAsync(WorkflowOperationKind? kind = null, CancellationToken cancellationToken = default)
        {
            var operations = _operations.Values
                .Where(operation => !kind.HasValue || operation.Kind == kind.Value)
                .ToArray();
            return Task.FromResult<IReadOnlyList<WorkflowOperationRecord>>(operations);
        }

        private void IndexMetadataReleaseOperation(WorkflowOperationRecord operation, bool createOnly)
        {
            if (operation.Kind == WorkflowOperationKind.MetadataRelease &&
                !string.IsNullOrWhiteSpace(operation.MetadataRelease?.PackageId))
            {
                var packageId = operation.MetadataRelease.PackageId;
                if (createOnly ||
                    (_metadataPackageIndex.TryGetValue(packageId, out var currentOperationId) &&
                     string.Equals(currentOperationId, operation.OperationId, StringComparison.Ordinal)))
                {
                    _metadataPackageIndex[packageId] = operation.OperationId;
                }
            }
        }
    }

    private sealed class ImmediateDeployBackend : IDeployBackend
    {
        private int _startCount;

        public int StartCount => _startCount;

        public string? SubmissionObservedRevision { get; init; }

        public string BackendName => "honua-gitops-kubernetes";

        public DeployTargetKind TargetKind => DeployTargetKind.Kubernetes;

        public Task<DeployBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployBackendCapabilities
            {
                SupportsRollback = true,
                SupportsProgressPolling = true,
                SupportsRevisionPinning = true
            });

        public Task<DeployPlan> PlanAsync(DeployOperationSpec spec, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployPlan
            {
                IsReadyToSubmit = true
            });

        public Task<DeploySubmissionResult> StartAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _startCount);
            return Task.FromResult(new DeploySubmissionResult
            {
                Status = WorkflowOperationStatus.Submitted,
                ProviderOperationId = $"test-backend:{operation.OperationId}",
                ObservedRevision = SubmissionObservedRevision,
                Message = "Submitted"
            });
        }

        public Task<DeployObservation> ObserveAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployObservation
            {
                Status = operation.Status,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.Deploy?.DesiredRevision,
                Message = operation.CurrentPhase
            });

        public Task<DeployObservation> RollbackAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployObservation
            {
                Status = WorkflowOperationStatus.RollbackRequested,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.Deploy?.CurrentRevision,
                Message = "Rollback requested"
            });
    }

    private sealed class BlockingDeployBackend : IDeployBackend
    {
        private readonly TaskCompletionSource<bool> _startEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _allowStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _startCount;

        public int StartCount => _startCount;

        public Task StartEntered => _startEntered.Task;

        public string BackendName => "honua-gitops-kubernetes";

        public DeployTargetKind TargetKind => DeployTargetKind.Kubernetes;

        public void AllowStart() => _allowStart.TrySetResult(true);

        public Task<DeployBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployBackendCapabilities
            {
                SupportsRollback = true,
                SupportsProgressPolling = true,
                SupportsRevisionPinning = true
            });

        public Task<DeployPlan> PlanAsync(DeployOperationSpec spec, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployPlan
            {
                IsReadyToSubmit = true
            });

        public async Task<DeploySubmissionResult> StartAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _startCount);
            _startEntered.TrySetResult(true);
            await _allowStart.Task.WaitAsync(cancellationToken);
            return new DeploySubmissionResult
            {
                Status = WorkflowOperationStatus.Submitted,
                ProviderOperationId = $"test-backend:{operation.OperationId}",
                Message = "Submitted"
            };
        }

        public Task<DeployObservation> ObserveAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployObservation
            {
                Status = operation.Status,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.Deploy?.DesiredRevision,
                Message = operation.CurrentPhase
            });

        public Task<DeployObservation> RollbackAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployObservation
            {
                Status = WorkflowOperationStatus.RollbackRequested,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.Deploy?.CurrentRevision,
                Message = "Rollback requested"
            });
    }

    private sealed class ThrowingObserveBackend : IDeployBackend
    {
        public string BackendName => "honua-gitops-kubernetes";

        public DeployTargetKind TargetKind => DeployTargetKind.Kubernetes;

        public Task<DeployBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployBackendCapabilities
            {
                SupportsRollback = true,
                SupportsProgressPolling = true,
                SupportsRevisionPinning = true
            });

        public Task<DeployPlan> PlanAsync(DeployOperationSpec spec, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployPlan
            {
                IsReadyToSubmit = true
            });

        public Task<DeploySubmissionResult> StartAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeploySubmissionResult
            {
                Status = WorkflowOperationStatus.Submitted,
                ProviderOperationId = $"throwing-backend:{operation.OperationId}",
                Message = "Submitted"
            });

        public Task<DeployObservation> ObserveAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom");

        public Task<DeployObservation> RollbackAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployObservation
            {
                Status = WorkflowOperationStatus.RollbackRequested,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.Deploy?.CurrentRevision,
                Message = "Rollback requested"
            });
    }

    private sealed class BlockingObserveBackend : IDeployBackend
    {
        private readonly TaskCompletionSource<bool> _observeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _allowObserve = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ObserveEntered => _observeEntered.Task;

        public string BackendName => "honua-gitops-kubernetes";

        public DeployTargetKind TargetKind => DeployTargetKind.Kubernetes;

        public void AllowObserve() => _allowObserve.TrySetResult(true);

        public Task<DeployBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployBackendCapabilities
            {
                SupportsRollback = true,
                SupportsProgressPolling = true,
                SupportsRevisionPinning = true
            });

        public Task<DeployPlan> PlanAsync(DeployOperationSpec spec, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployPlan
            {
                IsReadyToSubmit = true
            });

        public Task<DeploySubmissionResult> StartAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeploySubmissionResult
            {
                Status = WorkflowOperationStatus.Submitted,
                ProviderOperationId = $"blocking-observe:{operation.OperationId}",
                Message = "Submitted"
            });

        public async Task<DeployObservation> ObserveAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
        {
            _observeEntered.TrySetResult(true);
            await _allowObserve.Task.WaitAsync(cancellationToken);
            return new DeployObservation
            {
                Status = WorkflowOperationStatus.Reconciling,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.Deploy?.DesiredRevision,
                Message = "Still reconciling"
            };
        }

        public Task<DeployObservation> RollbackAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployObservation
            {
                Status = WorkflowOperationStatus.RollbackRequested,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.Deploy?.CurrentRevision,
                Message = "Rollback requested"
            });
    }

    private sealed class ProgressingDeployBackend : IDeployBackend
    {
        public string BackendName => "honua-gitops-kubernetes";

        public DeployTargetKind TargetKind => DeployTargetKind.Kubernetes;

        public Task<DeployBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployBackendCapabilities
            {
                SupportsRollback = true,
                SupportsProgressPolling = true,
                SupportsRevisionPinning = true
            });

        public Task<DeployPlan> PlanAsync(DeployOperationSpec spec, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployPlan
            {
                IsReadyToSubmit = true
            });

        public Task<DeploySubmissionResult> StartAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeploySubmissionResult
            {
                Status = WorkflowOperationStatus.Submitted,
                ProviderOperationId = $"progressing-backend:{operation.OperationId}",
                Message = "Submitted"
            });

        public Task<DeployObservation> ObserveAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult(operation.Status switch
            {
                WorkflowOperationStatus.Submitted => new DeployObservation
                {
                    Status = WorkflowOperationStatus.Reconciling,
                    ProviderOperationId = operation.ProviderOperationId,
                    ObservedRevision = operation.Deploy?.DesiredRevision,
                    Message = "Reconciling rollout."
                },
                WorkflowOperationStatus.RollbackRequested => new DeployObservation
                {
                    Status = WorkflowOperationStatus.RolledBack,
                    ProviderOperationId = operation.ProviderOperationId,
                    ObservedRevision = operation.Deploy?.CurrentRevision,
                    Message = "Rollback completed."
                },
                _ => new DeployObservation
                {
                    Status = WorkflowOperationStatus.Succeeded,
                    ProviderOperationId = operation.ProviderOperationId,
                    ObservedRevision = operation.Deploy?.DesiredRevision,
                    Message = "Rollout completed."
                }
            });

        public Task<DeployObservation> RollbackAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployObservation
            {
                Status = WorkflowOperationStatus.RollbackRequested,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.Deploy?.CurrentRevision,
                Message = "Rollback requested"
            });
    }

    private sealed class StubApprovalEvaluator(ApprovalRequirement? result = null) : IOperatorApprovalEvaluator
    {
        public ApprovalRequirement Evaluate(ClaimsPrincipal principal, OperatorAuthorizationRequest request)
            => result ?? ApprovalRequirement.NotRequired();
    }

    private sealed class StagedDeployBackend : IDeployBackend
    {
        public int PromoteCount { get; private set; }

        public string BackendName => "honua-gitops-kubernetes";

        public DeployTargetKind TargetKind => DeployTargetKind.Kubernetes;

        public Task<DeployBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployBackendCapabilities
            {
                SupportsRollback = true,
                SupportsTrafficShifting = true,
                SupportsProgressPolling = true,
                SupportsRevisionPinning = true
            });

        public Task<DeployPlan> PlanAsync(DeployOperationSpec spec, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployPlan
            {
                IsReadyToSubmit = true
            });

        public Task<DeploySubmissionResult> StartAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeploySubmissionResult
            {
                Status = WorkflowOperationStatus.Submitted,
                ProviderOperationId = $"staged-backend:{operation.OperationId}",
                ObservedRevision = operation.Deploy?.CurrentRevision,
                Message = "Submitted"
            });

        public Task<DeployObservation> ObserveAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployObservation
            {
                Status = WorkflowOperationStatus.Reconciling,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.Deploy?.CurrentRevision,
                PromotionRecommended = true,
                Message = "Canary is stable and ready for promotion."
            });

        public Task<DeployObservation> PromoteAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
        {
            PromoteCount++;
            return Task.FromResult(new DeployObservation
            {
                Status = WorkflowOperationStatus.Succeeded,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.Deploy?.DesiredRevision,
                Message = "Canary promoted to full traffic."
            });
        }

        public Task<DeployObservation> RollbackAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployObservation
            {
                Status = WorkflowOperationStatus.RollbackRequested,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.Deploy?.CurrentRevision,
                Message = "Rollback requested"
            });
    }

    private sealed class RecordingStagedDeployBackend : IDeployBackend
    {
        private readonly List<string> _appliedCanaryWeights = [];

        public int PromoteCount { get; private set; }

        public IReadOnlyList<string> AppliedCanaryWeights => _appliedCanaryWeights;

        public string BackendName => "honua-gitops-kubernetes";

        public DeployTargetKind TargetKind => DeployTargetKind.Kubernetes;

        public Task<DeployBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployBackendCapabilities
            {
                SupportsRollback = true,
                SupportsTrafficShifting = true,
                SupportsProgressPolling = true,
                SupportsRevisionPinning = true
            });

        public Task<DeployPlan> PlanAsync(DeployOperationSpec spec, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployPlan { IsReadyToSubmit = true });

        public Task<DeploySubmissionResult> StartAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
        {
            // The reconciler advances ramp steps by re-applying the deploy through StartAsync with
            // the next step weight pinned onto the spec parameters.
            if (operation.Deploy != null &&
                operation.Deploy.Parameters.TryGetValue("deployment.canary_weight_percentage", out var weight))
            {
                _appliedCanaryWeights.Add(weight);
            }

            return Task.FromResult(new DeploySubmissionResult
            {
                Status = WorkflowOperationStatus.Submitted,
                ProviderOperationId = $"recording-backend:{operation.OperationId}",
                ObservedRevision = operation.Deploy?.CurrentRevision,
                Message = "Canary weight applied."
            });
        }

        public Task<DeployObservation> ObserveAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployObservation
            {
                Status = WorkflowOperationStatus.Reconciling,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.Deploy?.CurrentRevision,
                PromotionRecommended = true,
                Message = "Canary is stable and ready for the next ramp step."
            });

        public Task<DeployObservation> PromoteAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
        {
            PromoteCount++;
            return Task.FromResult(new DeployObservation
            {
                Status = WorkflowOperationStatus.Succeeded,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.Deploy?.DesiredRevision,
                Message = "Canary promoted to full traffic."
            });
        }

        public Task<DeployObservation> RollbackAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployObservation
            {
                Status = WorkflowOperationStatus.RollbackRequested,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.Deploy?.CurrentRevision,
                Message = "Rollback requested"
            });
    }

    private sealed class SwitchingTelemetrySignalEvaluator(int breachWhenWeightAtLeast) : IDeployTelemetrySignalEvaluator
    {
        public Task<DeployTelemetryDecision?> EvaluateAsync(
            WorkflowOperationRecord operation,
            CancellationToken cancellationToken = default)
        {
            var weight = operation.Deploy?.CanaryRamp?.CurrentStepWeight ?? 0;
            if (weight >= breachWhenWeightAtLeast)
            {
                return Task.FromResult<DeployTelemetryDecision?>(new DeployTelemetryDecision
                {
                    RollbackRecommended = true,
                    Message = "Automatic rollback requested because telemetry detected canary degradation: error rate 0.25 exceeded threshold 0.05."
                });
            }

            return Task.FromResult<DeployTelemetryDecision?>(new DeployTelemetryDecision
            {
                Message = "Telemetry gate passed: error-rate signal is within threshold."
            });
        }
    }
}
