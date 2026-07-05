// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.ControlPlane;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Stage-machine coverage for the additive metadata-release layer-evolution lifecycle:
/// forward advance through to Complete, smoke-failure handoff to rollback, reversible
/// rollback execution, and snapshot-required refusal.
/// </summary>
public sealed class MetadataReleaseReconcilerTests
{
    [Fact]
    public async Task ReconcileAsync_AdditiveForwardPath_WalksStagesToComplete()
    {
        var store = new InMemoryWorkflowOperationStore();
        var operation = CreateOperation(WorkflowOperationStatus.Submitted, MetadataReleaseStage.Preflight);
        await store.TryCreateAsync(operation);

        var gate = new FakePreflightGate(MetadataRollbackReadinessClassification.ScriptReversible, canProceed: true);
        var script = new RecordingScriptExecutor();
        var dispatcher = new FakeDataJobDispatcher(dispatched: true);
        var activator = new RecordingActivator(currentRevision: 7);
        var smoke = new FakeSmokeChecker(passed: true);
        var reconciler = CreateReconciler(store, gate, script, dispatcher, activator, smoke);

        // Drive the leased single-advance loop until the operation reaches a terminal state.
        var stages = await DriveToTerminalAsync(reconciler, store, operation.OperationId);
        var final = await store.GetAsync(operation.OperationId);

        final!.Status.Should().Be(WorkflowOperationStatus.Succeeded);
        final.MetadataRelease!.CurrentStage.Should().Be(MetadataReleaseStage.Complete);

        // Every additive stage was visited in order.
        stages.Should().ContainInOrder(
            MetadataReleaseStage.Backup,
            MetadataReleaseStage.ScriptMigration,
            MetadataReleaseStage.ServicePublication,
            MetadataReleaseStage.MetadataApply,
            MetadataReleaseStage.Smoke,
            MetadataReleaseStage.Complete);

        // Forward side effects ran; inverse did not.
        script.ForwardCalls.Should().Be(1);
        script.InverseCalls.Should().Be(0);
        activator.ActivateCalls.Should().Be(1);
        activator.ReactivateCalls.Should().Be(0);
        dispatcher.DispatchCalls.Should().Be(1);

        // Prior revision captured for rollback; smoke evidence recorded.
        final.MetadataRelease.PriorRevision.Should().Be(7);
        final.MetadataRelease.EvidenceRefs.Should().ContainSingle(evidence => evidence.Kind == "smoke");
    }

    [Fact]
    public async Task ReconcileAsync_WhenSmokeFails_RequestsRollback()
    {
        var store = new InMemoryWorkflowOperationStore();
        var operation = CreateOperation(WorkflowOperationStatus.Submitted, MetadataReleaseStage.Preflight);
        await store.TryCreateAsync(operation);

        var smoke = new FakeSmokeChecker(passed: false, message: "no rows returned");
        var reconciler = CreateReconciler(
            store,
            new FakePreflightGate(MetadataRollbackReadinessClassification.ScriptReversible, canProceed: true),
            new RecordingScriptExecutor(),
            new FakeDataJobDispatcher(dispatched: true),
            new RecordingActivator(currentRevision: 3),
            smoke);

        await DriveUntilAsync(
            reconciler,
            store,
            operation.OperationId,
            op => op.Status == WorkflowOperationStatus.RollbackRequested);
        var afterSmoke = await store.GetAsync(operation.OperationId);

        afterSmoke!.Status.Should().Be(WorkflowOperationStatus.RollbackRequested);
        afterSmoke.MetadataRelease!.CurrentStage.Should().Be(MetadataReleaseStage.RollbackRequested);
        afterSmoke.CurrentPhase.Should().Contain("Smoke check failed");
        afterSmoke.MetadataRelease.EvidenceRefs.Should().ContainSingle(evidence => evidence.Kind == "smoke");
    }

    [Fact]
    public async Task ReconcileAsync_WhenSmokeFails_ReversibleRollbackRevertsAndCompletes()
    {
        var store = new InMemoryWorkflowOperationStore();
        var operation = CreateOperation(WorkflowOperationStatus.Submitted, MetadataReleaseStage.Preflight);
        await store.TryCreateAsync(operation);

        var script = new RecordingScriptExecutor();
        var activator = new RecordingActivator(currentRevision: 9);
        var reconciler = CreateReconciler(
            store,
            new FakePreflightGate(MetadataRollbackReadinessClassification.ScriptReversible, canProceed: true),
            script,
            new FakeDataJobDispatcher(dispatched: true),
            activator,
            new FakeSmokeChecker(passed: false, message: "schema missing new field"));

        var final = await DriveToTerminalAsync2(reconciler, store, operation.OperationId);

        final.Status.Should().Be(WorkflowOperationStatus.RolledBack);
        final.CompletedAt.Should().NotBeNull();
        final.CurrentPhase.Should().Contain("Reversible rollback complete");

        // The inverse ran exactly once and the prior revision was reactivated.
        script.ForwardCalls.Should().Be(1);
        script.InverseCalls.Should().Be(1);
        activator.ReactivateCalls.Should().Be(1);
        activator.ReactivatedRevision.Should().Be(9);
    }

    [Fact]
    public async Task ReconcileAsync_InjectedSmokeFailure_DrivesReversibleRollbackClosedLoop()
    {
        // Demo B closed loop: a deterministically injected smoke failure (what the
        // MetadataReleaseSmokeChecker returns when fault injection is enabled for a non-prod target)
        // must drive the same reversible-rollback path — reactivate the prior revision AND run the
        // down-script — proving the metadata + DB-inclusive revert without a naturally broken layer.
        var store = new InMemoryWorkflowOperationStore();
        var operation = CreateOperation(WorkflowOperationStatus.Submitted, MetadataReleaseStage.Preflight);
        await store.TryCreateAsync(operation);

        var script = new RecordingScriptExecutor();
        var activator = new RecordingActivator(currentRevision: 12);
        var injectedSmoke = new FakeSmokeChecker(
            passed: false,
            message: "Injected smoke failure (Demo B safe-rollback fault injection).");
        var reconciler = CreateReconciler(
            store,
            new FakePreflightGate(MetadataRollbackReadinessClassification.ScriptReversible, canProceed: true),
            script,
            new FakeDataJobDispatcher(dispatched: true),
            activator,
            injectedSmoke);

        var final = await DriveToTerminalAsync2(reconciler, store, operation.OperationId);

        // The deploy was health-gated, failed, and rolled back metadata + DB (down-script).
        final.Status.Should().Be(WorkflowOperationStatus.RolledBack);
        final.CurrentPhase.Should().Contain("Reversible rollback complete");
        final.MetadataRelease!.EvidenceRefs.Should().ContainSingle(evidence => evidence.Kind == "smoke");
        script.ForwardCalls.Should().Be(1);
        script.InverseCalls.Should().Be(1, "the down-script (drop-added-column) reverts the schema");
        activator.ReactivateCalls.Should().Be(1, "the prior metadata revision is reactivated");
        activator.ReactivatedRevision.Should().Be(12);
    }

    [Fact]
    public async Task ReconcileAsync_WhenSnapshotRequired_RefusesAtPreflight()
    {
        var store = new InMemoryWorkflowOperationStore();
        var operation = CreateOperation(WorkflowOperationStatus.Submitted, MetadataReleaseStage.Preflight);
        await store.TryCreateAsync(operation);

        var script = new RecordingScriptExecutor();
        var reconciler = CreateReconciler(
            store,
            new FakePreflightGate(MetadataRollbackReadinessClassification.SnapshotRequired, canProceed: true),
            script,
            new FakeDataJobDispatcher(dispatched: false),
            new RecordingActivator(currentRevision: 1),
            new FakeSmokeChecker(passed: true));

        await reconciler.ReconcileMetadataReleaseAsync(operation.OperationId);
        var refused = await store.GetAsync(operation.OperationId);

        refused!.Status.Should().Be(WorkflowOperationStatus.Failed);
        refused.MetadataRelease!.CurrentStage.Should().Be(MetadataReleaseStage.Failed);
        refused.ErrorMessage.Should().Contain("snapshot");
        refused.BlockingReasons.Should().Contain("metadata-release-snapshot-not-implemented");

        // No destructive side effect ran behind the refusal.
        script.ForwardCalls.Should().Be(0);
    }

    [Fact]
    public async Task ReconcileAsync_WhenRollbackRequestedWithSnapshotClass_RefusesExecution()
    {
        var store = new InMemoryWorkflowOperationStore();
        var operation = CreateOperation(WorkflowOperationStatus.RollbackRequested, MetadataReleaseStage.RollbackRequested) with
        {
            MetadataRelease = CreateRelease(MetadataReleaseStage.RollbackRequested) with
            {
                RollbackPlan = new MetadataRollbackPlan { Class = MetadataRollbackClass.SnapshotRestore },
                PriorRevision = 4
            }
        };
        await store.TryCreateAsync(operation);

        var script = new RecordingScriptExecutor();
        var activator = new RecordingActivator(currentRevision: 4);
        var reconciler = CreateReconciler(
            store,
            new FakePreflightGate(MetadataRollbackReadinessClassification.SnapshotRequired, canProceed: true),
            script,
            new FakeDataJobDispatcher(dispatched: false),
            activator,
            new FakeSmokeChecker(passed: true));

        await reconciler.ReconcileMetadataReleaseAsync(operation.OperationId);
        var refused = await store.GetAsync(operation.OperationId);

        refused!.Status.Should().Be(WorkflowOperationStatus.ManualInterventionRequired);
        refused.ErrorMessage.Should().Contain("Snapshot rollback is not yet implemented");
        script.InverseCalls.Should().Be(0);
        activator.ReactivateCalls.Should().Be(0);
    }

    [Fact]
    public async Task ReconcileAsync_WhenStageAlreadyPastSmoke_IsIdempotentAndDoesNotRerunForward()
    {
        var store = new InMemoryWorkflowOperationStore();
        // Already Complete: a redundant reconcile must not re-run any side effect.
        var operation = CreateOperation(WorkflowOperationStatus.Succeeded, MetadataReleaseStage.Complete);
        await store.TryCreateAsync(operation);

        var script = new RecordingScriptExecutor();
        var reconciler = CreateReconciler(
            store,
            new FakePreflightGate(MetadataRollbackReadinessClassification.ScriptReversible, canProceed: true),
            script,
            new FakeDataJobDispatcher(dispatched: true),
            new RecordingActivator(currentRevision: 1),
            new FakeSmokeChecker(passed: true));

        await reconciler.ReconcileMetadataReleaseAsync(operation.OperationId);
        var unchanged = await store.GetAsync(operation.OperationId);

        unchanged!.Status.Should().Be(WorkflowOperationStatus.Succeeded);
        script.ForwardCalls.Should().Be(0);
        script.InverseCalls.Should().Be(0);
    }

    // ---- helpers ---------------------------------------------------------

    private static MetadataReleaseReconciler CreateReconciler(
        IWorkflowOperationStore store,
        IMetadataReleasePreflightGate gate,
        IMetadataReleaseScriptExecutor script,
        IMetadataReleaseDataJobDispatcher dispatcher,
        IMetadataReleaseActivator activator,
        IMetadataReleaseSmokeChecker smoke)
        => new(store, gate, script, dispatcher, activator, smoke, NullLogger<MetadataReleaseReconciler>.Instance);

    private static async Task<List<MetadataReleaseStage>> DriveToTerminalAsync(
        MetadataReleaseReconciler reconciler,
        InMemoryWorkflowOperationStore store,
        string operationId)
    {
        var stages = new List<MetadataReleaseStage>();
        for (var i = 0; i < 20; i++)
        {
            await reconciler.ReconcileMetadataReleaseAsync(operationId);
            var op = await store.GetAsync(operationId);
            stages.Add(op!.MetadataRelease!.CurrentStage);
            if (op.Status is WorkflowOperationStatus.Succeeded
                or WorkflowOperationStatus.Failed
                or WorkflowOperationStatus.RolledBack
                or WorkflowOperationStatus.ManualInterventionRequired
                or WorkflowOperationStatus.RollbackRequested)
            {
                break;
            }
        }

        return stages;
    }

    private static async Task<WorkflowOperationRecord> DriveToTerminalAsync2(
        MetadataReleaseReconciler reconciler,
        InMemoryWorkflowOperationStore store,
        string operationId)
    {
        for (var i = 0; i < 20; i++)
        {
            await reconciler.ReconcileMetadataReleaseAsync(operationId);
            var op = await store.GetAsync(operationId);
            if (op!.Status is WorkflowOperationStatus.Succeeded
                or WorkflowOperationStatus.Failed
                or WorkflowOperationStatus.RolledBack
                or WorkflowOperationStatus.ManualInterventionRequired)
            {
                return op;
            }
        }

        return (await store.GetAsync(operationId))!;
    }

    private static async Task DriveUntilAsync(
        MetadataReleaseReconciler reconciler,
        InMemoryWorkflowOperationStore store,
        string operationId,
        Func<WorkflowOperationRecord, bool> predicate)
    {
        for (var i = 0; i < 20; i++)
        {
            await reconciler.ReconcileMetadataReleaseAsync(operationId);
            var op = await store.GetAsync(operationId);
            if (predicate(op!))
            {
                return;
            }
        }
    }

    private static WorkflowOperationRecord CreateOperation(WorkflowOperationStatus status, MetadataReleaseStage stage)
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        return new WorkflowOperationRecord
        {
            OperationId = $"metadata-release-{Guid.NewGuid():N}",
            Kind = WorkflowOperationKind.MetadataRelease,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "test",
            MetadataRelease = CreateRelease(stage)
        };
    }

    private static MetadataReleaseContext CreateRelease(MetadataReleaseStage stage)
        => new()
        {
            PackageId = "pkg-demo-b",
            DesiredRevision = "pkg-demo-b",
            TargetEnvironment = "production",
            CurrentStage = stage,
            ExecutionPlan = new MetadataReleaseExecutionPlan
            {
                PackageId = "pkg-demo-b",
                TargetEnvironment = "production",
                ResourceSemanticId = "parcels",
                NewFieldName = "owner_email",
                DataPopulateWorkloadId = "populate-owner-email",
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
            }
        };

    // ---- fakes -----------------------------------------------------------

    private sealed class FakePreflightGate(MetadataRollbackReadinessClassification classification, bool canProceed)
        : IMetadataReleasePreflightGate
    {
        public Task<MetadataReleasePreflightResult> EvaluateAsync(MetadataReleaseExecutionPlan plan, CancellationToken cancellationToken = default)
            => Task.FromResult(new MetadataReleasePreflightResult
            {
                CanProceed = canProceed,
                RollbackClassification = classification,
                Reason = "test classification"
            });
    }

    private sealed class RecordingScriptExecutor : IMetadataReleaseScriptExecutor
    {
        public int ForwardCalls { get; private set; }
        public int InverseCalls { get; private set; }

        public Task ApplyForwardAsync(MetadataReleaseExecutionPlan plan, CancellationToken cancellationToken = default)
        {
            ForwardCalls++;
            return Task.CompletedTask;
        }

        public Task ApplyInverseAsync(MetadataReleaseExecutionPlan plan, CancellationToken cancellationToken = default)
        {
            InverseCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDataJobDispatcher(bool dispatched) : IMetadataReleaseDataJobDispatcher
    {
        public int DispatchCalls { get; private set; }

        public Task<bool> DispatchAndAwaitAsync(MetadataReleaseExecutionPlan plan, string operationId, CancellationToken cancellationToken = default)
        {
            DispatchCalls++;
            return Task.FromResult(dispatched);
        }
    }

    private sealed class RecordingActivator(long? currentRevision) : IMetadataReleaseActivator
    {
        public int ActivateCalls { get; private set; }
        public int ReactivateCalls { get; private set; }
        public long? ReactivatedRevision { get; private set; }

        public Task<long?> GetCurrentRevisionAsync(MetadataReleaseExecutionPlan plan, CancellationToken cancellationToken = default)
            => Task.FromResult(currentRevision);

        public Task ActivateNewRevisionAsync(MetadataReleaseExecutionPlan plan, CancellationToken cancellationToken = default)
        {
            ActivateCalls++;
            return Task.CompletedTask;
        }

        public Task ReactivateRevisionAsync(MetadataReleaseExecutionPlan plan, long revision, CancellationToken cancellationToken = default)
        {
            ReactivateCalls++;
            ReactivatedRevision = revision;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSmokeChecker(bool passed, string? message = null) : IMetadataReleaseSmokeChecker
    {
        public Task<MetadataReleaseSmokeResult> RunAsync(MetadataReleaseExecutionPlan plan, CancellationToken cancellationToken = default)
            => Task.FromResult(new MetadataReleaseSmokeResult
            {
                Passed = passed,
                RowCount = passed ? 5 : 0,
                NewFieldPresent = passed,
                Message = message ?? (passed ? "ok" : "failed")
            });
    }

    private sealed class InMemoryWorkflowOperationStore : IWorkflowOperationStore
    {
        private readonly ConcurrentDictionary<string, WorkflowOperationRecord> _operations = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _metadataPackageIndex = new(StringComparer.Ordinal);
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
        {
            var created = _operations.TryAdd(operation.OperationId, operation);
            if (created)
            {
                Index(operation);
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
            Index(operation);
            return Task.CompletedTask;
        }

        public Task<bool> TrySetAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _operations[operation.OperationId] = operation;
            Index(operation);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<WorkflowOperationRecord>> ListActiveAsync(WorkflowOperationKind? kind = null, CancellationToken cancellationToken = default)
        {
            var operations = _operations.Values
                .Where(operation => !kind.HasValue || operation.Kind == kind.Value)
                .ToArray();
            return Task.FromResult<IReadOnlyList<WorkflowOperationRecord>>(operations);
        }

        private void Index(WorkflowOperationRecord operation)
        {
            if (operation.Kind == WorkflowOperationKind.MetadataRelease &&
                !string.IsNullOrWhiteSpace(operation.MetadataRelease?.PackageId))
            {
                _metadataPackageIndex[operation.MetadataRelease.PackageId] = operation.OperationId;
            }
        }
    }
}
