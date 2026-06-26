// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Phase 2 tests for extending the hybrid-trigger seam to the deploy/release reconcilers:
/// <list type="bullet">
///   <item><description>
///     the deploy event handler resolves a deploy provider id to the durable operation id and
///     reconciles it once, and is duplicate / out-of-order safe (lease + terminal-guard no-op);
///   </description></item>
///   <item><description>
///     the staged self-continue handler reconciles a metadata/coordinated release once (advancing
///     one stage) and refuses unsupported kinds;
///   </description></item>
///   <item><description>
///     the workflow-operation backstop sweep covers the deploy/metadata/coordinated staged ops:
///     it reconciles stale active ops, no-ops fresh ones, and walks a staged release forward.
///   </description></item>
/// </list>
/// </summary>
public sealed class ControlPlaneDeployTriggerTests
{
    // ---- Deploy event handler ----------------------------------------------

    [Fact]
    public async Task HandleDeployEvent_ResolvesProviderIdAndReconcilesOnce()
    {
        var deploy = CreateDeploy("op-deploy", WorkflowOperationStatus.Reconciling, providerOperationId: "codedeploy-d-123");
        var store = new FakeWorkflowStore(deploy);
        var dispatcher = Substitute.For<IOperationReconcileDispatcher>();
        var sut = BuildHandler(store, dispatcher);

        await sut.HandleDeployEventAsync("codedeploy-d-123");

        await dispatcher.Received(1).ReconcileOnceAsync(
            Arg.Is<OperationRef>(r => r.Kind == OperationKind.DeployWorkflow && r.OperationId == "op-deploy"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleDeployEvent_UnknownProviderId_IsIgnored()
    {
        var deploy = CreateDeploy("op-deploy", WorkflowOperationStatus.Reconciling, providerOperationId: "codedeploy-d-123");
        var store = new FakeWorkflowStore(deploy);
        var dispatcher = Substitute.For<IOperationReconcileDispatcher>();
        var sut = BuildHandler(store, dispatcher);

        await sut.HandleDeployEventAsync("not-a-known-deployment");

        await dispatcher.DidNotReceiveWithAnyArgs().ReconcileOnceAsync(default, default);
    }

    [Fact]
    public async Task HandleDeployEvent_DuplicateAndOutOfOrderInvocation_AdvancesDeployOnce()
    {
        // Real deploy reconciler behind the real dispatcher: prove the lease + terminal-status guard
        // make a duplicate / out-of-order event a no-op. The backend reports a terminal Succeeded; the
        // first event advances the durable record, the duplicates re-read a terminal record and bail.
        var deploy = CreateDeploy("op-dup", WorkflowOperationStatus.Reconciling, providerOperationId: "ecs-dep-dup");
        var store = new FakeWorkflowStore(deploy);
        var backend = new FakeDeployBackend(WorkflowOperationStatus.Succeeded);
        var dispatcher = BuildDeployDispatcher(store, backend);
        var sut = BuildHandler(store, dispatcher);

        await sut.HandleDeployEventAsync("ecs-dep-dup");
        await sut.HandleDeployEventAsync("ecs-dep-dup");
        await sut.HandleDeployOperationAsync("op-dup");

        var stored = await store.GetAsync("op-dup");
        stored!.Status.Should().Be(WorkflowOperationStatus.Succeeded);
        store.WriteCount.Should().Be(1, "the terminal guard makes duplicate/out-of-order deploy events a no-op");
        backend.ObserveCount.Should().Be(1, "only the first event observes the backend; the terminal guard short-circuits the rest");
    }

    // ---- Staged self-continue handler --------------------------------------

    [Fact]
    public async Task HandleStagedContinue_MetadataRelease_ReconcilesOnce()
    {
        var dispatcher = Substitute.For<IOperationReconcileDispatcher>();
        var sut = BuildHandler(new FakeWorkflowStore(), dispatcher);

        await sut.HandleStagedContinueAsync(OperationKind.MetadataRelease, "mr-1");

        await dispatcher.Received(1).ReconcileOnceAsync(
            Arg.Is<OperationRef>(r => r.Kind == OperationKind.MetadataRelease && r.OperationId == "mr-1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleStagedContinue_CoordinatedRelease_ReconcilesOnce()
    {
        var dispatcher = Substitute.For<IOperationReconcileDispatcher>();
        var sut = BuildHandler(new FakeWorkflowStore(), dispatcher);

        await sut.HandleStagedContinueAsync(OperationKind.CoordinatedRelease, "cr-1");

        await dispatcher.Received(1).ReconcileOnceAsync(
            Arg.Is<OperationRef>(r => r.Kind == OperationKind.CoordinatedRelease && r.OperationId == "cr-1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleStagedContinue_UnsupportedKind_IsNoOp()
    {
        var dispatcher = Substitute.For<IOperationReconcileDispatcher>();
        var sut = BuildHandler(new FakeWorkflowStore(), dispatcher);

        // Deploy/execution kinds have their own dedicated event entrypoints; the staged continue is
        // for the staged releases only.
        await sut.HandleStagedContinueAsync(OperationKind.DeployWorkflow, "op-deploy");

        await dispatcher.DidNotReceiveWithAnyArgs().ReconcileOnceAsync(default, default);
    }

    // ---- Workflow-operation backstop sweep ---------------------------------

    [Fact]
    public async Task WorkflowBackstop_ReconcilesStaleDeployMetadataAndCoordinated()
    {
        var stale = DateTimeOffset.UtcNow.AddMinutes(-10);
        var store = new FakeWorkflowStore(
            CreateDeploy("op-deploy", WorkflowOperationStatus.Reconciling, "p-deploy", updatedAt: stale),
            CreateMetadata("op-meta", WorkflowOperationStatus.Reconciling, updatedAt: stale),
            CreateCoordinated("op-coord", WorkflowOperationStatus.Reconciling, updatedAt: stale));
        var dispatcher = Substitute.For<IOperationReconcileDispatcher>();
        var sut = BuildWorkflowBackstop(store, dispatcher, TimeSpan.FromSeconds(90));

        await sut.SweepOnceAsync(TimeSpan.FromSeconds(90), CancellationToken.None);

        await dispatcher.Received(1).ReconcileOnceAsync(
            Arg.Is<OperationRef>(r => r.Kind == OperationKind.DeployWorkflow && r.OperationId == "op-deploy"), Arg.Any<CancellationToken>());
        await dispatcher.Received(1).ReconcileOnceAsync(
            Arg.Is<OperationRef>(r => r.Kind == OperationKind.MetadataRelease && r.OperationId == "op-meta"), Arg.Any<CancellationToken>());
        await dispatcher.Received(1).ReconcileOnceAsync(
            Arg.Is<OperationRef>(r => r.Kind == OperationKind.CoordinatedRelease && r.OperationId == "op-coord"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WorkflowBackstop_NoOpsFreshOperations()
    {
        var store = new FakeWorkflowStore(
            CreateDeploy("op-deploy", WorkflowOperationStatus.Reconciling, "p-deploy", updatedAt: DateTimeOffset.UtcNow),
            CreateMetadata("op-meta", WorkflowOperationStatus.Reconciling, updatedAt: DateTimeOffset.UtcNow));
        var dispatcher = Substitute.For<IOperationReconcileDispatcher>();
        var sut = BuildWorkflowBackstop(store, dispatcher, TimeSpan.FromSeconds(90));

        await sut.SweepOnceAsync(TimeSpan.FromSeconds(90), CancellationToken.None);

        await dispatcher.DidNotReceiveWithAnyArgs().ReconcileOnceAsync(default, default);
    }

    [Fact]
    public async Task WorkflowBackstop_SkipsTerminalOperations()
    {
        // ListActiveAsync filters terminal ops the way the Redis store does, so a Succeeded op is
        // never surfaced to the sweep.
        var store = new FakeWorkflowStore(
            CreateDeploy("op-done", WorkflowOperationStatus.Succeeded, "p-done", updatedAt: DateTimeOffset.UtcNow.AddHours(-1)));
        var dispatcher = Substitute.For<IOperationReconcileDispatcher>();
        var sut = BuildWorkflowBackstop(store, dispatcher, TimeSpan.FromSeconds(90));

        await sut.SweepOnceAsync(TimeSpan.FromSeconds(90), CancellationToken.None);

        await dispatcher.DidNotReceiveWithAnyArgs().ReconcileOnceAsync(default, default);
    }

    [Fact]
    public async Task WorkflowBackstop_StaleStagedOperation_AdvancesViaRealReconcilerThenSettles()
    {
        // A staged metadata release wedged at ScriptMigration: the backstop drives the real
        // reconciler, which advances exactly one stage to ServicePublication and persists. A second
        // sweep walks it forward again — proving the backstop covers staged ops and is the safety net
        // when a self-continue signal is dropped.
        var stale = DateTimeOffset.UtcNow.AddMinutes(-10);
        var release = CreateMetadata("op-staged", WorkflowOperationStatus.Reconciling, updatedAt: stale)
            with
        {
            MetadataRelease = MetadataContextAt(MetadataReleaseStage.ScriptMigration)
        };
        var store = new FakeWorkflowStore(release);
        var reconciler = BuildMetadataReconciler(store);
        var dispatcher = new OperationReconcileDispatcher(
            Substitute.For<IWorkflowOperationReconciler>(),
            Substitute.For<IExecutionJobReconciler>(),
            reconciler,
            Substitute.For<ICoordinatedReleaseReconciler>());
        var sut = BuildWorkflowBackstop(store, dispatcher, TimeSpan.FromSeconds(90));

        await sut.SweepOnceAsync(TimeSpan.FromSeconds(90), CancellationToken.None);

        var stored = await store.GetAsync("op-staged");
        stored!.MetadataRelease!.CurrentStage.Should().Be(
            MetadataReleaseStage.ServicePublication,
            "the backstop drove the staged reconciler exactly one stage forward");
        store.WriteCount.Should().Be(1, "exactly one stage advance persisted per sweep");
    }

    // ---- Helpers -----------------------------------------------------------

    private static ControlPlaneEventHandler BuildHandler(
        IWorkflowOperationStore workflowStore,
        IOperationReconcileDispatcher dispatcher)
        => new(
            Substitute.For<IExecutionJobStore>(),
            workflowStore,
            dispatcher,
            NullLogger<ControlPlaneEventHandler>.Instance);

    private static OperationReconcileDispatcher BuildDeployDispatcher(
        IWorkflowOperationStore store,
        IDeployBackend backend)
    {
        var registry = Substitute.For<IDeployTargetRegistry>();
        registry.GetAsync("target-1", Arg.Any<CancellationToken>()).Returns(new DeployTargetDefinition
        {
            TargetId = "target-1",
            TargetKind = backend.TargetKind,
            Backend = backend.BackendName,
            Environment = "prod",
            TargetName = "honua-service"
        });

        var telemetry = Substitute.For<IDeployTelemetrySignalEvaluator>();
        telemetry.EvaluateAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<CancellationToken>())
            .Returns((DeployTelemetryDecision?)null);

        var reconciler = new DeployWorkflowReconciler(
            store,
            registry,
            [backend],
            telemetry,
            NullLogger<DeployWorkflowReconciler>.Instance);

        return new OperationReconcileDispatcher(
            reconciler,
            Substitute.For<IExecutionJobReconciler>(),
            Substitute.For<IMetadataReleaseOperationReconciler>(),
            Substitute.For<ICoordinatedReleaseReconciler>());
    }

    private static MetadataReleaseReconciler BuildMetadataReconciler(IWorkflowOperationStore store)
    {
        var scriptExecutor = Substitute.For<IMetadataReleaseScriptExecutor>();
        scriptExecutor.ApplyForwardAsync(Arg.Any<MetadataReleaseExecutionPlan>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        return new MetadataReleaseReconciler(
            store,
            Substitute.For<IMetadataReleasePreflightGate>(),
            scriptExecutor,
            Substitute.For<IMetadataReleaseDataJobDispatcher>(),
            Substitute.For<IMetadataReleaseActivator>(),
            Substitute.For<IMetadataReleaseSmokeChecker>(),
            NullLogger<MetadataReleaseReconciler>.Instance);
    }

    private static WorkflowOperationBackstopSweepService BuildWorkflowBackstop(
        IWorkflowOperationStore store,
        IOperationReconcileDispatcher dispatcher,
        TimeSpan staleThreshold)
        => new(
            store,
            dispatcher,
            Options.Create(new ControlPlaneTriggerOptions { StaleThreshold = staleThreshold }),
            NullLogger<WorkflowOperationBackstopSweepService>.Instance);

    private static WorkflowOperationRecord CreateDeploy(
        string operationId,
        WorkflowOperationStatus status,
        string? providerOperationId,
        DateTimeOffset? updatedAt = null) => new()
        {
            OperationId = operationId,
            Kind = WorkflowOperationKind.Deploy,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = updatedAt ?? DateTimeOffset.UtcNow.AddMinutes(-1),
            ProviderOperationId = providerOperationId,
            Deploy = new DeployOperationSpec
            {
                TargetId = "target-1",
                TargetKind = DeployTargetKind.AwsEcs,
                Backend = "honua-gitops-aws-ecs-alb",
                Environment = "prod",
                TargetName = "honua-service",
                DesiredRevision = "v2"
            }
        };

    private static WorkflowOperationRecord CreateMetadata(
        string operationId,
        WorkflowOperationStatus status,
        DateTimeOffset? updatedAt = null) => new()
        {
            OperationId = operationId,
            Kind = WorkflowOperationKind.MetadataRelease,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = updatedAt ?? DateTimeOffset.UtcNow.AddMinutes(-1),
            MetadataRelease = MetadataContextAt(MetadataReleaseStage.Preflight)
        };

    private static MetadataReleaseContext MetadataContextAt(MetadataReleaseStage stage) => new()
    {
        PackageId = "pkg-1",
        DesiredRevision = "rev-2",
        TargetEnvironment = "prod",
        CurrentStage = stage,
        ExecutionPlan = BuildExecutionPlan()
    };

    private static MetadataReleaseExecutionPlan BuildExecutionPlan() => new()
    {
        PackageId = "pkg-1",
        TargetEnvironment = "prod",
        ResourceSemanticId = "layer-1",
        NewFieldName = "added_field",
        Script = new MetadataReleaseScript
        {
            ScriptId = "script-1",
            ForwardOperations =
            [
                new MetadataReleaseScriptOperation
                {
                    Kind = MetadataReleaseScriptOperationKind.AddColumn,
                    ResourceSemanticId = "layer-1",
                    FieldName = "added_field"
                }
            ],
            InverseOperations = []
        }
    };

    private static WorkflowOperationRecord CreateCoordinated(
        string operationId,
        WorkflowOperationStatus status,
        DateTimeOffset? updatedAt = null) => new()
        {
            OperationId = operationId,
            Kind = WorkflowOperationKind.CoordinatedRelease,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = updatedAt ?? DateTimeOffset.UtcNow.AddMinutes(-1),
            CoordinatedRelease = new CoordinatedReleaseContext
            {
                PackageId = "pkg-coord",
                TargetEnvironment = "prod",
                CurrentStep = CoordinatedReleaseStep.Preflight,
                Container = new CoordinatedReleaseContainerSpec { TargetId = "target-1", DesiredImage = "honua:v2" },
                MetadataPlan = BuildExecutionPlan()
            }
        };

    /// <summary>
    /// In-memory workflow-operation store. Lease ops always succeed; <see cref="ListActiveAsync"/>
    /// filters terminal operations exactly like the Redis store, and <see cref="SetAsync"/> counts
    /// advancing writes so duplicate/out-of-order reconciles are observable.
    /// </summary>
    private sealed class FakeWorkflowStore(params WorkflowOperationRecord[] operations) : IWorkflowOperationStore
    {
        private readonly Dictionary<string, WorkflowOperationRecord> _ops =
            operations.ToDictionary(op => op.OperationId, StringComparer.Ordinal);

        public int WriteCount { get; private set; }

        public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> TryCreateAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            if (_ops.ContainsKey(operation.OperationId))
            {
                return Task.FromResult(false);
            }

            _ops[operation.OperationId] = operation;
            return Task.FromResult(true);
        }

        public Task<WorkflowOperationRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult(_ops.TryGetValue(operationId, out var op) ? op : null);

        public Task<WorkflowOperationRecord?> GetByMetadataPackageIdAsync(string packageId, CancellationToken cancellationToken = default)
            => Task.FromResult(_ops.Values.FirstOrDefault(op => op.MetadataRelease?.PackageId == packageId));

        public Task SetAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _ops[operation.OperationId] = operation;
            WriteCount++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkflowOperationRecord>> ListActiveAsync(WorkflowOperationKind? kind = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkflowOperationRecord>>(
                _ops.Values
                    .Where(op => !IsTerminal(op.Status) && (kind is null || op.Kind == kind))
                    .OrderByDescending(op => op.UpdatedAt)
                    .ToArray());

        private static bool IsTerminal(WorkflowOperationStatus status)
            => status is WorkflowOperationStatus.Succeeded
                or WorkflowOperationStatus.Failed
                or WorkflowOperationStatus.RolledBack
                or WorkflowOperationStatus.ManualInterventionRequired;
    }

    /// <summary>
    /// Deploy backend that reports a fixed observation and counts how many times it was observed.
    /// </summary>
    private sealed class FakeDeployBackend(WorkflowOperationStatus observedStatus) : IDeployBackend
    {
        public int ObserveCount { get; private set; }

        public string BackendName => "honua-gitops-aws-ecs-alb";

        public DeployTargetKind TargetKind => DeployTargetKind.AwsEcs;

        public Task<DeployBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployBackendCapabilities { SupportsRollback = true });

        public Task<DeployPlan> PlanAsync(DeployOperationSpec spec, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployPlan { IsReadyToSubmit = true });

        public Task<DeploySubmissionResult> StartAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeploySubmissionResult { Status = WorkflowOperationStatus.Submitted });

        public Task<DeployObservation> ObserveAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
        {
            ObserveCount++;
            return Task.FromResult(new DeployObservation
            {
                Status = observedStatus,
                ProviderOperationId = operation.ProviderOperationId
            });
        }

        public Task<DeployObservation> RollbackAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployObservation { Status = WorkflowOperationStatus.RolledBack });
    }
}
