// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Coverage for the Demo C coordinated platform-upgrade conductor: the ordered forward path
/// (preflight → backup → container rollout (gated) → DB-schema + metadata (gated) → smoke → promote),
/// the per-step approval gates that hold at the container-swap and data-affecting boundaries, and the
/// single coordinated rollback that unwinds the completed reversible steps in reverse order when any
/// step faults.
/// </summary>
public sealed class CoordinatedReleaseReconcilerTests
{
    [Fact]
    public async Task Reconcile_HappyPath_OrdersContainerThenDbMetadataThenPromotes()
    {
        var store = new InMemoryWorkflowOperationStore();
        var operation = await CreateAsync(store);

        var container = new FakeContainerStep();
        var metadata = new FakeMetadataStep();
        var reconciler = CreateReconciler(store, container, metadata);

        // Auto-approve both gates as soon as the conductor parks at them.
        var final = await DriveAsync(store, operation.OperationId, reconciler, autoApprove: true);

        final.Status.Should().Be(WorkflowOperationStatus.Succeeded);
        final.CoordinatedRelease!.CurrentStep.Should().Be(CoordinatedReleaseStep.Complete);

        // Container ran before metadata; neither was rolled back.
        container.StartCalls.Should().Be(1);
        container.RollbackCalls.Should().Be(0);
        metadata.StartCalls.Should().Be(1);
        metadata.RollbackCalls.Should().Be(0);
        container.StartedAtTicks.Should().BeLessThan(metadata.StartedAtTicks);

        var steps = final.CoordinatedRelease.Steps;
        steps.Single(s => s.Step == CoordinatedReleaseStep.ContainerRollout).Status
            .Should().Be(CoordinatedReleaseStepStatus.Succeeded);
        steps.Single(s => s.Step == CoordinatedReleaseStep.MetadataAndSchema).Status
            .Should().Be(CoordinatedReleaseStepStatus.Succeeded);
        steps.Single(s => s.Step == CoordinatedReleaseStep.Promote).Status
            .Should().Be(CoordinatedReleaseStepStatus.Succeeded);
    }

    [Fact]
    public async Task Reconcile_ContainerSwapGate_HoldsUntilApproved()
    {
        var store = new InMemoryWorkflowOperationStore();
        var operation = await CreateAsync(store);

        var container = new FakeContainerStep();
        var reconciler = CreateReconciler(store, container, new FakeMetadataStep());

        // Drive without approving: the conductor must park at the container-swap gate.
        for (var i = 0; i < 6; i++)
        {
            await reconciler.ReconcileCoordinatedReleaseAsync(operation.OperationId);
        }

        var parked = await store.GetAsync(operation.OperationId);
        parked!.Status.Should().Be(WorkflowOperationStatus.AwaitingApproval);
        parked.CoordinatedRelease!.CurrentStep.Should().Be(CoordinatedReleaseStep.ContainerRollout);
        container.StartCalls.Should().Be(0, "the rollout must not submit before the gate is approved");

        parked.CoordinatedRelease.Steps.Single(s => s.Step == CoordinatedReleaseStep.ContainerRollout).Status
            .Should().Be(CoordinatedReleaseStepStatus.AwaitingApproval);
    }

    [Fact]
    public async Task Reconcile_FaultInContainerStep_RollsBackNothingDownstream()
    {
        var store = new InMemoryWorkflowOperationStore();
        var operation = await CreateAsync(store);

        var container = new FakeContainerStep { ObserveOutcome = CoordinatedStepOutcome.Failed };
        var metadata = new FakeMetadataStep();
        var reconciler = CreateReconciler(store, container, metadata);

        var final = await DriveAsync(store, operation.OperationId, reconciler, autoApprove: true);

        final.Status.Should().Be(WorkflowOperationStatus.RolledBack);
        // Container started then failed and was unwound; metadata never started.
        container.StartCalls.Should().Be(1);
        container.RollbackCalls.Should().Be(1);
        metadata.StartCalls.Should().Be(0);
        metadata.RollbackCalls.Should().Be(0);
    }

    [Fact]
    public async Task Reconcile_FaultInMetadataStep_UnwindsMetadataThenContainerInReverse()
    {
        // Demo C fault-injection / rollback proof: a fault at the DB-schema + metadata step (the
        // #1753 safe-rollback path) must trigger ONE coordinated rollback that unwinds BOTH completed
        // reversible steps in reverse order — metadata/schema first (reactivate prior revision + inverse
        // script), then the already-promoted container rollout (deploy rollback).
        var store = new InMemoryWorkflowOperationStore();
        var operation = await CreateAsync(store);

        var container = new FakeContainerStep();
        var metadata = new FakeMetadataStep { ObserveOutcome = CoordinatedStepOutcome.Failed };
        var reconciler = CreateReconciler(store, container, metadata);

        var final = await DriveAsync(store, operation.OperationId, reconciler, autoApprove: true);

        final.Status.Should().Be(WorkflowOperationStatus.RolledBack);
        final.CoordinatedRelease!.CurrentStep.Should().Be(CoordinatedReleaseStep.Failed);

        // Both reversible steps ran and were unwound; the unwind order is metadata-before-container.
        container.StartCalls.Should().Be(1);
        metadata.StartCalls.Should().Be(1);
        metadata.RollbackCalls.Should().Be(1);
        container.RollbackCalls.Should().Be(1);
        metadata.RolledBackAtTicks.Should().BeLessThan(container.RolledBackAtTicks);

        final.CoordinatedRelease.Steps.Single(s => s.Step == CoordinatedReleaseStep.MetadataAndSchema).Status
            .Should().Be(CoordinatedReleaseStepStatus.RolledBack);
        final.CoordinatedRelease.Steps.Single(s => s.Step == CoordinatedReleaseStep.ContainerRollout).Status
            .Should().Be(CoordinatedReleaseStepStatus.RolledBack);
    }

    [Fact]
    public async Task Reconcile_MidUnwind_ContainerRollbackThrowsAfterMetadataReverted_SplitBrainEscalatesToManualIntervention()
    {
        // Failure-of-failure (#2161): the unwind order is metadata-first, container-second. If the
        // METADATA unwind succeeds (prior revision + inverse script applied) but the subsequent
        // CONTAINER rollback THROWS, the coordinated release is split-brained — metadata is reverted
        // but the new container image is still live. The conductor must escalate to
        // ManualInterventionRequired and the ledger must reflect the split state (metadata RolledBack,
        // container NOT RolledBack) so an operator can finish the unwind by hand.
        var store = new InMemoryWorkflowOperationStore();
        var operation = await CreateAsync(store);

        var container = new FakeContainerStep { ThrowOnRollback = true };
        var metadata = new FakeMetadataStep { ObserveOutcome = CoordinatedStepOutcome.Failed };
        var reconciler = CreateReconciler(store, container, metadata);

        // Drive manually: the container rollback throws mid-unwind. The conductor persists a terminal
        // ManualInterventionRequired (mirroring the background service's catch) and then rethrows, so
        // the throw is expected on the cycle that attempts the container rollback. The store still
        // reflects the escalated, split-brain state.
        var final = await DriveUntilTerminalToleratingThrowAsync(store, operation.OperationId, reconciler);

        final.Status.Should().Be(WorkflowOperationStatus.ManualInterventionRequired);
        final.CoordinatedRelease!.CurrentStep.Should().Be(CoordinatedReleaseStep.Failed);

        // Metadata was unwound; the container rollback was attempted but threw.
        metadata.RollbackCalls.Should().Be(1, "the metadata step unwinds first and succeeds");
        container.RollbackCalls.Should().Be(1, "the container rollback is attempted second and throws");

        // Ledger reflects the split: metadata reverted, container still live (not rolled back).
        var metadataState = final.CoordinatedRelease.Steps.Single(s => s.Step == CoordinatedReleaseStep.MetadataAndSchema);
        var containerState = final.CoordinatedRelease.Steps.Single(s => s.Step == CoordinatedReleaseStep.ContainerRollout);
        metadataState.Status.Should().Be(CoordinatedReleaseStepStatus.RolledBack);
        containerState.Status.Should().NotBe(
            CoordinatedReleaseStepStatus.RolledBack,
            "the container rollback threw, so it must not be recorded as rolled back");
    }

    // ---- helpers ---------------------------------------------------------

    private static CoordinatedReleaseReconciler CreateReconciler(
        IWorkflowOperationStore store,
        ICoordinatedContainerStepExecutor container,
        ICoordinatedMetadataStepExecutor metadata)
        => new(store, container, metadata, NullLogger<CoordinatedReleaseReconciler>.Instance);

    private static async Task<WorkflowOperationRecord> CreateAsync(InMemoryWorkflowOperationStore store)
    {
        var service = new CoordinatedReleaseControlService(new[] { (IWorkflowOperationStore)store });
        return await service.CreateAsync(
            new CoordinatedReleaseContainerSpec
            {
                TargetId = "prod-ecs",
                DesiredImage = "honua/server:v21",
                CurrentImage = "honua/server:v20",
                RequiresExplicitApproval = true
            },
            BuildMetadataPlan(),
            targetEnvironment: "production",
            requestedBy: "operator",
            reason: "Demo C coordinated upgrade",
            idempotencyKey: null,
            correlationId: null);
    }

    private static MetadataReleaseExecutionPlan BuildMetadataPlan()
        => new()
        {
            PackageId = $"pkg-democ-{Guid.NewGuid():N}",
            TargetEnvironment = "production",
            ResourceSemanticId = "parcels",
            NewFieldName = "owner_email",
            Script = new MetadataReleaseScript
            {
                ScriptId = "add-owner-email",
                Reversible = true,
                ForwardOperations =
                [
                    new MetadataReleaseScriptOperation
                    {
                        Kind = MetadataReleaseScriptOperationKind.AddColumn,
                        ResourceSemanticId = "parcels",
                        FieldName = "owner_email",
                        FieldType = "String",
                        Nullable = true
                    }
                ]
            }
        };

    private static async Task<WorkflowOperationRecord> DriveAsync(
        InMemoryWorkflowOperationStore store,
        string operationId,
        CoordinatedReleaseReconciler reconciler,
        bool autoApprove)
    {
        var service = new CoordinatedReleaseControlService(new[] { (IWorkflowOperationStore)store });
        for (var i = 0; i < 40; i++)
        {
            await reconciler.ReconcileCoordinatedReleaseAsync(operationId);
            var op = await store.GetAsync(operationId);
            if (op is null)
            {
                break;
            }

            if (autoApprove && op.Status == WorkflowOperationStatus.AwaitingApproval)
            {
                await service.ApproveGateAsync(operationId, op.CoordinatedRelease!.CurrentStep, "operator", "approved", default);
                continue;
            }

            if (op.Status is WorkflowOperationStatus.Succeeded
                or WorkflowOperationStatus.Failed
                or WorkflowOperationStatus.RolledBack
                or WorkflowOperationStatus.ManualInterventionRequired)
            {
                return op;
            }
        }

        return (await store.GetAsync(operationId))!;
    }

    private static async Task<WorkflowOperationRecord> DriveUntilTerminalToleratingThrowAsync(
        InMemoryWorkflowOperationStore store,
        string operationId,
        CoordinatedReleaseReconciler reconciler)
    {
        var service = new CoordinatedReleaseControlService(new[] { (IWorkflowOperationStore)store });
        for (var i = 0; i < 40; i++)
        {
            try
            {
                await reconciler.ReconcileCoordinatedReleaseAsync(operationId);
            }
            catch (InvalidOperationException)
            {
                // The conductor persists the terminal escalation before rethrowing the unwind fault;
                // mirror the background service which logs and continues.
            }

            var op = await store.GetAsync(operationId);
            if (op is null)
            {
                break;
            }

            if (op.Status == WorkflowOperationStatus.AwaitingApproval)
            {
                await service.ApproveGateAsync(operationId, op.CoordinatedRelease!.CurrentStep, "operator", "approved", default);
                continue;
            }

            if (op.Status is WorkflowOperationStatus.Succeeded
                or WorkflowOperationStatus.Failed
                or WorkflowOperationStatus.RolledBack
                or WorkflowOperationStatus.ManualInterventionRequired)
            {
                return op;
            }
        }

        return (await store.GetAsync(operationId))!;
    }

    // ---- fakes -----------------------------------------------------------

    private sealed class FakeContainerStep : ICoordinatedContainerStepExecutor
    {
        public int StartCalls { get; private set; }
        public int RollbackCalls { get; private set; }
        public long StartedAtTicks { get; private set; }
        public long RolledBackAtTicks { get; private set; }
        public CoordinatedStepOutcome ObserveOutcome { get; init; } = CoordinatedStepOutcome.Succeeded;
        public bool ThrowOnRollback { get; init; }

        public Task<CoordinatedStepResult> StartAsync(CoordinatedReleaseContext context, WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
        {
            StartCalls++;
            StartedAtTicks = DateTime.UtcNow.Ticks + StartCalls;
            return Task.FromResult(new CoordinatedStepResult
            {
                Outcome = CoordinatedStepOutcome.Pending,
                ChildOperationId = "deploy-child",
                Detail = "container submitted"
            });
        }

        public Task<CoordinatedStepResult> ObserveAsync(string childOperationId, CancellationToken cancellationToken = default)
            => Task.FromResult(new CoordinatedStepResult { Outcome = ObserveOutcome, ChildOperationId = childOperationId });

        public Task RollbackAsync(string childOperationId, CancellationToken cancellationToken = default)
        {
            RollbackCalls++;
            RolledBackAtTicks = DateTime.UtcNow.Ticks + RollbackCalls;
            if (ThrowOnRollback)
            {
                throw new InvalidOperationException("Container rollback failed: provider deploy-rollback call errored.");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeMetadataStep : ICoordinatedMetadataStepExecutor
    {
        public int StartCalls { get; private set; }
        public int RollbackCalls { get; private set; }
        public long StartedAtTicks { get; private set; }
        public long RolledBackAtTicks { get; private set; }
        public CoordinatedStepOutcome ObserveOutcome { get; init; } = CoordinatedStepOutcome.Succeeded;

        public Task<CoordinatedStepResult> StartAsync(CoordinatedReleaseContext context, WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
        {
            StartCalls++;
            StartedAtTicks = DateTime.UtcNow.Ticks + 1_000_000 + StartCalls;
            return Task.FromResult(new CoordinatedStepResult
            {
                Outcome = CoordinatedStepOutcome.Pending,
                ChildOperationId = "metadata-child",
                Detail = "metadata submitted"
            });
        }

        public Task<CoordinatedStepResult> ObserveAsync(string childOperationId, CancellationToken cancellationToken = default)
            => Task.FromResult(new CoordinatedStepResult { Outcome = ObserveOutcome, ChildOperationId = childOperationId });

        public Task RollbackAsync(string childOperationId, CancellationToken cancellationToken = default)
        {
            RollbackCalls++;
            RolledBackAtTicks = DateTime.UtcNow.Ticks + RollbackCalls;
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryWorkflowOperationStore : IWorkflowOperationStore
    {
        private readonly ConcurrentDictionary<string, WorkflowOperationRecord> _operations = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _leases = new(StringComparer.Ordinal);

        public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(_leases.TryAdd(operationId, ownerId));

        public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(_leases.TryGetValue(operationId, out var currentOwner) && currentOwner == ownerId);

        public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
        {
            _leases.TryRemove(new KeyValuePair<string, string>(operationId, ownerId));
            return Task.CompletedTask;
        }

        public Task<bool> TryCreateAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
            => Task.FromResult(_operations.TryAdd(operation.OperationId, operation));

        public Task<WorkflowOperationRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult(_operations.TryGetValue(operationId, out var operation) ? operation : null);

        public Task<WorkflowOperationRecord?> GetByMetadataPackageIdAsync(string packageId, CancellationToken cancellationToken = default)
            => Task.FromResult<WorkflowOperationRecord?>(null);

        public Task SetAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _operations[operation.OperationId] = operation;
            return Task.CompletedTask;
        }

        public Task<bool> TrySetAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _operations[operation.OperationId] = operation;
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<WorkflowOperationRecord>> ListActiveAsync(WorkflowOperationKind? kind = null, CancellationToken cancellationToken = default)
        {
            var operations = _operations.Values
                .Where(operation => !kind.HasValue || operation.Kind == kind.Value)
                .ToArray();
            return Task.FromResult<IReadOnlyList<WorkflowOperationRecord>>(operations);
        }
    }
}
