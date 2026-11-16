// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

// Child-settlement evidence for coordinated release rollback. The container step is the production
// CoordinatedContainerStepExecutor over the production deploy service and deploy reconciler. The
// provider is a scripted pair of replicas behind one traffic switch. The loopback servers return body
// markers, so the receipt proves which revision is serving when the parent becomes terminal.
public sealed class CoordinatedReleaseRollbackSettlementIntegrationTests(ITestOutputHelper output)
{
    private const string TargetId = "coordinated-local";
    private const string CurrentRevision = "honua/server:v20";
    private const string DesiredRevision = "honua/server:v21";
    private const string ScriptedNoPriorRevisionMessage = "scripted backend has no prior revision";

    [Fact]
    public async Task Reconcile_ProductionContainerChildSettlesAfterRestart_BeforeParentRollback()
    {
        await RunScenarioAsync(priorRevisionAvailable: true);
    }

    [Fact]
    public async Task Reconcile_ProductionContainerChildCannotRestorePriorRevision_RequiresManualIntervention()
    {
        await RunScenarioAsync(priorRevisionAvailable: false);
    }

    [Fact]
    public async Task Reconcile_ProductionContainerChildRecordMissing_RequiresManualIntervention()
    {
        await RunScenarioAsync(priorRevisionAvailable: true, removeChildBeforeRestart: true);
    }

    private async Task RunScenarioAsync(bool priorRevisionAvailable, bool removeChildBeforeRestart = false)
    {
        var ledger = new TransitionLedger
        {
            Scenario = priorRevisionAvailable ? "prior-revision-settles" : "provider-manual-intervention"
        };

        var priorPort = GetFreePort();
        var candidatePort = GetFreePort();
        await using var priorServer = await LocalMarkerServer.StartAsync(priorPort, CurrentRevision);
        await using var candidateServer = await LocalMarkerServer.StartAsync(candidatePort, DesiredRevision);

        var store = new InMemoryWorkflowOperationStore();
        var provider = new ReplicaPairProvider(ReplicaAddress(priorPort), ReplicaAddress(candidatePort))
        {
            PriorRunning = priorRevisionAvailable
        };

        var metadata = new FaultingMetadataStep();
        var operation = await CreateCoordinatedOperationAsync(store, priorRevisionAvailable);
        ledger.ParentOperationId = operation.OperationId;
        await ledger.CaptureAsync(store, operation.OperationId, "created");

        try
        {
            var firstBackend = new ReplicaPairDeployBackend(provider);
            var first = CreateProductionReconciler(store, firstBackend, metadata);

            var pending = await DriveUntilContainerRollbackRequestedAsync(store, operation.OperationId, first, ledger);
            pending.Status.Should().Be(WorkflowOperationStatus.RollbackRequested);
            pending.CoordinatedRelease!.Steps.Single(s => s.Step == CoordinatedReleaseStep.ContainerRollout).Status
                .Should().Be(CoordinatedReleaseStepStatus.RollbackRequested);
            pending.CoordinatedRelease.ContainerOperationId.Should().NotBeNullOrWhiteSpace();

            var childBeforeRestart = await store.GetAsync(pending.CoordinatedRelease.ContainerOperationId!);
            childBeforeRestart.Should().NotBeNull();
            childBeforeRestart!.Status.Should().Be(
                priorRevisionAvailable
                    ? WorkflowOperationStatus.RollbackRequested
                    : WorkflowOperationStatus.ManualInterventionRequired);
            childBeforeRestart.ProviderOperationId.Should().NotBeNullOrWhiteSpace();

            ledger.RestartBoundary = new RestartBoundaryReceipt
            {
                At = DateTimeOffset.UtcNow,
                ParentStatusBeforeRestart = pending.Status.ToString(),
                ChildStatusBeforeRestart = childBeforeRestart.Status.ToString()
            };
            output.WriteLine($"restart-boundary: {ledger.RestartBoundary.At:O}");

            // A new conductor and new child executor must reconstruct the pending provider operation
            // from the durable child record. No rollback request is made by this restart path.
            var restartedBackend = new ReplicaPairDeployBackend(provider);
            var restarted = CreateProductionReconciler(store, restartedBackend, metadata);

            if (removeChildBeforeRestart)
            {
                store.Remove(pending.CoordinatedRelease.ContainerOperationId!);
            }

            await restarted.ReconcileCoordinatedReleaseAsync(operation.OperationId);
            await ledger.CaptureAsync(store, operation.OperationId, "child-observed-after-restart");
            var afterRestartPoll = (await store.GetAsync(operation.OperationId))!;
            var childAfterRestartPoll = await store.GetAsync(afterRestartPoll.CoordinatedRelease!.ContainerOperationId!);

            if (removeChildBeforeRestart)
            {
                afterRestartPoll.Status.Should().Be(WorkflowOperationStatus.ManualInterventionRequired);
                afterRestartPoll.CoordinatedRelease.Steps.Single(s => s.Step == CoordinatedReleaseStep.ContainerRollout).Status
                    .Should().Be(CoordinatedReleaseStepStatus.RollbackRequested);
                if (childAfterRestartPoll is not null)
                {
                    throw new InvalidOperationException(
                        "a missing child record must not be treated as a successful rollback");
                }
                afterRestartPoll.ErrorMessage.Should().Contain("could not be read back");
                // The previous replica is retained through cutover, so the initial rollback settled by
                // switching traffic back. The missing child after restart must not add a provider start.
                provider.CandidateStarts.Should().Be(1, "the retained previous replica let rollback switch traffic back without a relaunch, and the missing child after restart must not trigger a provider start either");
                ledger.FinalSplitStateReason = afterRestartPoll.ErrorMessage;
            }
            else if (priorRevisionAvailable)
            {
                afterRestartPoll.Status.Should().Be(WorkflowOperationStatus.RollbackRequested);
                afterRestartPoll.CoordinatedRelease.Steps.Single(s => s.Step == CoordinatedReleaseStep.ContainerRollout).Status
                    .Should().Be(CoordinatedReleaseStepStatus.RollbackRequested);
                var pendingChildAfterRestart = childAfterRestartPoll ?? throw new InvalidOperationException("The pending child operation could not be read back.");
                pendingChildAfterRestart.Status.Should().Be(WorkflowOperationStatus.RollbackRequested);

                await restarted.ReconcileCoordinatedReleaseAsync(operation.OperationId);
                await ledger.CaptureAsync(store, operation.OperationId, "child-settled-after-pending-poll");
                var afterChildSettlement = (await store.GetAsync(operation.OperationId))!;
                var childAfterSettlement = (await store.GetAsync(afterChildSettlement.CoordinatedRelease!.ContainerOperationId!))!;

                afterChildSettlement.Status.Should().Be(WorkflowOperationStatus.RollbackRequested);
                afterChildSettlement.CoordinatedRelease.Steps.Single(s => s.Step == CoordinatedReleaseStep.ContainerRollout).Status
                    .Should().Be(CoordinatedReleaseStepStatus.RolledBack);
                childAfterSettlement.Status.Should().Be(WorkflowOperationStatus.RolledBack);
                childAfterSettlement.ObservedState.Should().Be(CurrentRevision);
                // The previous replica is retained through cutover, so rollback recovers by switching
                // traffic back to it. The candidate is the only provider start.
                provider.CandidateStarts.Should().Be(1, "the retained previous replica made a relaunch unnecessary");
                restartedBackend.RollbackRequestCalls.Should().Be(0, "restart observes settlement and must not issue a second provider rollback");

                var bodyObservedAt = DateTimeOffset.UtcNow;
                var body = await ReadBodyAsync(provider.ServingAddress);
                body.Should().Be(CurrentRevision);
                ledger.FunctionalBodyReceipt = new FunctionalBodyReceipt
                {
                    At = bodyObservedAt,
                    Uri = new Uri(new Uri(provider.ServingAddress), "body").ToString(),
                    Marker = body,
                    BeforeParentTerminal = true
                };

                await restarted.ReconcileCoordinatedReleaseAsync(operation.OperationId);
                await ledger.CaptureAsync(store, operation.OperationId, "parent-terminal");
                var final = (await store.GetAsync(operation.OperationId))!;
                final.Status.Should().Be(WorkflowOperationStatus.RolledBack);
                final.CompletedAt.Should().NotBeNull();
                var parentCompletedAt = final.CompletedAt ?? throw new InvalidOperationException("The rolled-back parent did not record completion.");
                ledger.FunctionalBodyReceipt.At.Should().BeOnOrBefore(parentCompletedAt);
                ledger.FinalSplitStateReason = null;
            }
            else
            {
                afterRestartPoll.Status.Should().Be(WorkflowOperationStatus.ManualInterventionRequired);
                var manualChildAfterRestart = childAfterRestartPoll ?? throw new InvalidOperationException("The manual child operation could not be read back.");
                manualChildAfterRestart.Status.Should().Be(WorkflowOperationStatus.ManualInterventionRequired);
                manualChildAfterRestart.ObservedState.Should().Be(DesiredRevision, "the ledger must preserve that the failed revision remained observed");
                afterRestartPoll.CoordinatedRelease.Steps.Single(s => s.Step == CoordinatedReleaseStep.ContainerRollout).Status
                    .Should().Be(CoordinatedReleaseStepStatus.RollbackRequested);
                afterRestartPoll.ErrorMessage.Should().Contain(ScriptedNoPriorRevisionMessage);
                provider.CandidateStarts.Should().Be(1, "the provider reported that no prior revision was available");
                ledger.FinalSplitStateReason = afterRestartPoll.ErrorMessage;
            }

            ledger.ProviderRollbackRequests = firstBackend.RollbackRequestCalls;
            ledger.ChildProviderOperationId = childAfterRestartPoll?.ProviderOperationId;
            ledger.ExpectedPriorRevision = priorRevisionAvailable ? CurrentRevision : null;
            ledger.ObservedChildRevision = childAfterRestartPoll?.ObservedState;
            ledger.ParentCompletedAt = (await store.GetAsync(operation.OperationId))!.CompletedAt;
        }
        finally
        {
            var path = await ledger.WriteAsync();
            output.WriteLine($"rollback-transition-ledger: {path}");
        }
    }

    private static CoordinatedReleaseReconciler CreateProductionReconciler(
        InMemoryWorkflowOperationStore store,
        ReplicaPairDeployBackend backend,
        FaultingMetadataStep metadata)
    {
        var registry = new LocalDeployTargetRegistry();
        var deployService = new DeployWorkflowService(
            registry,
            [store],
            [backend],
            new AllowApprovalEvaluator(),
            NullLogger<DeployWorkflowService>.Instance);
        var deployReconciler = new DeployWorkflowReconciler(
            store,
            registry,
            [backend],
            new NoTelemetryEvaluator(),
            NullLogger<DeployWorkflowReconciler>.Instance);
        var container = new CoordinatedContainerStepExecutor(deployService, deployReconciler, [store]);
        return new CoordinatedReleaseReconciler(
            store,
            container,
            metadata,
            NullLogger<CoordinatedReleaseReconciler>.Instance);
    }

    private static async Task<WorkflowOperationRecord> CreateCoordinatedOperationAsync(
        InMemoryWorkflowOperationStore store,
        bool priorRevisionAvailable)
    {
        var service = new CoordinatedReleaseControlService([(IWorkflowOperationStore)store]);
        return await service.CreateAsync(
            new CoordinatedReleaseContainerSpec
            {
                TargetId = TargetId,
                CurrentImage = priorRevisionAvailable ? CurrentRevision : null,
                DesiredImage = DesiredRevision,
                RequiresExplicitApproval = true
            },
            new MetadataReleaseExecutionPlan
            {
                PackageId = $"pkg-{Guid.NewGuid():N}",
                TargetEnvironment = "local",
                ResourceSemanticId = "parcels",
                NewFieldName = "owner_email",
                Script = new MetadataReleaseScript
                {
                    ScriptId = "local-fault",
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
            },
            "local",
            "operator",
            "rollback settlement evidence",
            idempotencyKey: null,
            correlationId: $"corr-{Guid.NewGuid():N}");
    }

    private static async Task<WorkflowOperationRecord> DriveUntilContainerRollbackRequestedAsync(
        InMemoryWorkflowOperationStore store,
        string operationId,
        CoordinatedReleaseReconciler reconciler,
        TransitionLedger ledger)
    {
        for (var cycle = 0; cycle < 40; cycle++)
        {
            await reconciler.ReconcileCoordinatedReleaseAsync(operationId);
            await ledger.CaptureAsync(store, operationId, $"cycle-{cycle}");
            var operation = (await store.GetAsync(operationId))!;
            if (operation.Status == WorkflowOperationStatus.AwaitingApproval)
            {
                await CoordinatedGateTestApproval.ApproveAsync(store, operation);
                await ledger.CaptureAsync(store, operationId, $"approved-{operation.CoordinatedRelease!.CurrentStep}");
                continue;
            }

            if (operation.CoordinatedRelease!.Steps.Single(s => s.Step == CoordinatedReleaseStep.ContainerRollout).Status
                == CoordinatedReleaseStepStatus.RollbackRequested)
            {
                return operation;
            }
        }

        throw new InvalidOperationException("The coordinated release did not reach a pending container rollback within 40 reconciliation cycles.");
    }

    private static string ReplicaAddress(int port)
        => $"http://127.0.0.1:{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}/";

    private static async Task<string> ReadBodyAsync(string baseAddress)
    {
        using var client = new HttpClient();
        return await client.GetStringAsync(new Uri(new Uri(baseAddress), "body"));
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class LocalMarkerServer : IAsyncDisposable
    {
        private readonly WebApplication _application;

        private LocalMarkerServer(WebApplication application)
        {
            _application = application;
        }

        public static async Task<LocalMarkerServer> StartAsync(int port, string marker)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Development",
                ApplicationName = typeof(LocalMarkerServer).Assembly.GetName().Name
            });
            builder.WebHost.UseKestrel().UseUrls($"http://127.0.0.1:{port}");
            var application = builder.Build();
            application.MapGet("/body", () => Results.Text(marker));
            await application.StartAsync();
            return new LocalMarkerServer(application);
        }

        public async ValueTask DisposeAsync()
        {
            await _application.StopAsync();
            await _application.DisposeAsync();
        }
    }

    private sealed class ReplicaPairProvider(string priorAddress, string candidateAddress)
    {
        private readonly string _priorAddress = priorAddress;
        private int _pendingRollbackObservations;

        public bool PriorRunning { get; init; }
        public bool CandidateRunning { get; private set; }
        public int CandidateStarts { get; private set; }
        public string ServingAddress { get; private set; } = priorAddress;

        public void StartCandidate()
        {
            CandidateStarts++;
            CandidateRunning = true;
        }

        public void ServeCandidate() => ServingAddress = candidateAddress;

        public void ServePriorAndStopCandidate()
        {
            ServingAddress = _priorAddress;
            CandidateRunning = false;
            Interlocked.Exchange(ref _pendingRollbackObservations, 1);
        }

        public bool ConsumePendingRollbackObservation()
            => Interlocked.Exchange(ref _pendingRollbackObservations, 0) == 1;
    }

    private sealed class ReplicaPairDeployBackend(ReplicaPairProvider provider) : IDeployBackend
    {
        public const string Name = "test-replica-pair";

        public int RollbackRequestCalls { get; private set; }
        public string BackendName => Name;
        public DeployTargetKind TargetKind => DeployTargetKind.AwsEcs;

        public Task<DeployBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployBackendCapabilities
            {
                SupportsRollback = true,
                RequiresOutOfBandMigrations = true,
                SupportsProgressPolling = true,
                SupportsRevisionPinning = true,
                StagesCandidateWithoutTraffic = true
            });

        public Task<DeployPlan> PlanAsync(DeployOperationSpec spec, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployPlan
            {
                IsReadyToSubmit = !spec.RequiresApproval,
                RequiresApproval = spec.RequiresApproval,
                RequiresOutOfBandMigrations = true
            });

        public Task<DeploySubmissionResult> StartAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
        {
            provider.StartCandidate();
            return Task.FromResult(new DeploySubmissionResult
            {
                Status = WorkflowOperationStatus.Submitted,
                ProviderOperationId = $"replica-pair:{operation.OperationId}",
                ObservedRevision = operation.Deploy!.CurrentRevision
            });
        }

        public async Task<DeployObservation> ObserveAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
        {
            var spec = operation.Deploy!;
            if (operation.Status != WorkflowOperationStatus.RollbackRequested)
            {
                return Observation(operation, WorkflowOperationStatus.Reconciling, spec.CurrentRevision) with { PromotionRecommended = true };
            }

            if (provider.ConsumePendingRollbackObservation() || provider.CandidateRunning)
            {
                return Observation(operation, WorkflowOperationStatus.RollbackRequested, spec.CurrentRevision);
            }

            var served = await ReadBodyAsync(provider.ServingAddress);
            return string.Equals(served, spec.CurrentRevision, StringComparison.Ordinal)
                ? Observation(operation, WorkflowOperationStatus.RolledBack, served)
                : Observation(operation, WorkflowOperationStatus.ManualInterventionRequired, served);
        }

        public Task<DeployObservation> PromoteAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
        {
            provider.ServeCandidate();
            return Task.FromResult(Observation(operation, WorkflowOperationStatus.Succeeded, operation.Deploy!.DesiredRevision));
        }

        public Task<DeployObservation> RollbackAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
        {
            RollbackRequestCalls++;
            var spec = operation.Deploy!;
            if (string.IsNullOrWhiteSpace(spec.CurrentRevision) || !provider.PriorRunning)
            {
                return Task.FromResult(Observation(operation, WorkflowOperationStatus.ManualInterventionRequired, observedRevision: null) with
                {
                    Message = ScriptedNoPriorRevisionMessage
                });
            }

            provider.ServePriorAndStopCandidate();
            return Task.FromResult(Observation(operation, WorkflowOperationStatus.RollbackRequested, spec.CurrentRevision));
        }

        private static DeployObservation Observation(WorkflowOperationRecord operation, WorkflowOperationStatus status, string? observedRevision)
            => new()
            {
                Status = status,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = observedRevision
            };
    }

    private sealed class LocalDeployTargetRegistry : IDeployTargetRegistry
    {
        private readonly DeployTargetDefinition _target = new()
        {
            TargetId = TargetId,
            TargetKind = DeployTargetKind.AwsEcs,
            Backend = ReplicaPairDeployBackend.Name,
            Environment = "local",
            TargetName = "coordinated-local",
            ArtifactReference = DesiredRevision,
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [DeployPromotionPolicy.PromotionGateParameterKey] = "health"
            }
        };

        public Task<IReadOnlyList<DeployTargetDefinition>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DeployTargetDefinition>>([_target]);

        public Task<DeployTargetDefinition?> GetAsync(string targetId, CancellationToken cancellationToken = default)
            => Task.FromResult<DeployTargetDefinition?>(targetId == TargetId ? _target : null);
    }

    private sealed class FaultingMetadataStep : ICoordinatedMetadataStepExecutor
    {
        private bool _rollbackRequested;
        public int RollbackCalls { get; private set; }

        public Task<CoordinatedStepResult> StartAsync(CoordinatedReleaseContext context, WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new CoordinatedStepResult
            {
                Outcome = CoordinatedStepOutcome.Pending,
                ChildOperationId = "metadata-child",
                Detail = "metadata/schema submitted"
            });

        public Task<CoordinatedStepResult> ObserveAsync(string childOperationId, CancellationToken cancellationToken = default)
            => Task.FromResult(_rollbackRequested
                ? new CoordinatedStepResult
                {
                    Outcome = CoordinatedStepOutcome.RolledBack,
                    ChildOperationId = childOperationId,
                    ObservedRevision = "metadata-v1",
                    Detail = "metadata rollback settled"
                }
                : new CoordinatedStepResult
                {
                    Outcome = CoordinatedStepOutcome.Failed,
                    ChildOperationId = childOperationId,
                    Detail = "metadata/schema fault after activation"
                });

        public Task RollbackAsync(string childOperationId, CancellationToken cancellationToken = default)
        {
            RollbackCalls++;
            _rollbackRequested = true;
            return Task.CompletedTask;
        }
    }

    private sealed class AllowApprovalEvaluator : IOperatorApprovalEvaluator
    {
        public ApprovalRequirement Evaluate(System.Security.Claims.ClaimsPrincipal principal, OperatorAuthorizationRequest request)
            => new() { IsRequired = false, PolicyRef = "test" };
    }

    private sealed class NoTelemetryEvaluator : IDeployTelemetrySignalEvaluator
    {
        public Task<DeployTelemetryDecision?> EvaluateAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult<DeployTelemetryDecision?>(null);
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

        public void Remove(string operationId)
            => _operations.TryRemove(operationId, out _);

        public Task<WorkflowOperationRecord?> GetByMetadataPackageIdAsync(string packageId, CancellationToken cancellationToken = default)
            => Task.FromResult<WorkflowOperationRecord?>(null);

        public Task SetAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _operations[operation.OperationId] = operation;
            return Task.CompletedTask;
        }

        public Task<bool> TrySetAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            if (!_operations.TryGetValue(operation.OperationId, out var current) || current.Version != operation.Version)
                return Task.FromResult(false);
            return Task.FromResult(_operations.TryUpdate(operation.OperationId, operation with { Version = operation.Version + 1 }, current));
        }

        public Task<IReadOnlyList<WorkflowOperationRecord>> ListActiveAsync(WorkflowOperationKind? kind = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkflowOperationRecord>>(_operations.Values
                .Where(operation => !kind.HasValue || operation.Kind == kind.Value)
                .ToArray());
    }

    private sealed class TransitionLedger
    {
        public string Scenario { get; init; } = string.Empty;
        public string? ParentOperationId { get; set; }
        public string? ChildProviderOperationId { get; set; }
        public string? ExpectedPriorRevision { get; set; }
        public string? ObservedChildRevision { get; set; }
        public int ProviderRollbackRequests { get; set; }
        public DateTimeOffset? ParentCompletedAt { get; set; }
        public RestartBoundaryReceipt? RestartBoundary { get; set; }
        public FunctionalBodyReceipt? FunctionalBodyReceipt { get; set; }
        public string? FinalSplitStateReason { get; set; }
        public List<Transition> Transitions { get; } = [];

        public async Task CaptureAsync(InMemoryWorkflowOperationStore store, string operationId, string boundary)
        {
            var parent = await store.GetAsync(operationId);
            if (parent is null)
            {
                return;
            }

            var context = parent.CoordinatedRelease;
            WorkflowOperationRecord? child = context?.ContainerOperationId is { } childId
                ? await store.GetAsync(childId)
                : null;
            Transitions.Add(new Transition
            {
                At = DateTimeOffset.UtcNow,
                Boundary = boundary,
                ParentOperationId = parent.OperationId,
                ParentStatus = parent.Status.ToString(),
                ParentStep = context?.CurrentStep.ToString(),
                ParentUpdatedAt = parent.UpdatedAt,
                ParentStepStates = context?.Steps.ToDictionary(s => s.Step.ToString(), s => s.Status.ToString()) ?? [],
                ChildOperationId = context?.ContainerOperationId,
                ChildStatus = child?.Status.ToString(),
                ChildProviderOperationId = child?.ProviderOperationId,
                ExpectedRevision = context?.Container.CurrentImage,
                ObservedRevision = child?.ObservedState,
                ChildUpdatedAt = child?.UpdatedAt,
                SplitStateReason = parent.ErrorMessage
            });
        }

        public async Task<string> WriteAsync()
        {
            var root = Environment.GetEnvironmentVariable("HONUA_SERVER_TEST_RESULTS_DIR");
            if (string.IsNullOrWhiteSpace(root))
            {
                root = Path.Join(FindRepositoryRoot(), "artifacts", "test-results");
            }

            Directory.CreateDirectory(root);
            var path = Path.Join(root, $"coordinated-release-rollback-{Scenario}-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            return path;
        }

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (directory is not null && !File.Exists(Path.Join(directory.FullName, "Honua.sln")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? Directory.GetCurrentDirectory();
        }
    }

    private sealed record RestartBoundaryReceipt
    {
        public required DateTimeOffset At { get; init; }
        public required string ParentStatusBeforeRestart { get; init; }
        public required string ChildStatusBeforeRestart { get; init; }
    }

    private sealed record FunctionalBodyReceipt
    {
        public required DateTimeOffset At { get; init; }
        public required string Uri { get; init; }
        public required string Marker { get; init; }
        public required bool BeforeParentTerminal { get; init; }
    }

    private sealed record Transition
    {
        public required DateTimeOffset At { get; init; }
        public required string Boundary { get; init; }
        public required string ParentOperationId { get; init; }
        public required string? ParentStatus { get; init; }
        public required string? ParentStep { get; init; }
        public required DateTimeOffset ParentUpdatedAt { get; init; }
        public required IReadOnlyDictionary<string, string> ParentStepStates { get; init; }
        public string? ChildOperationId { get; init; }
        public string? ChildStatus { get; init; }
        public string? ChildProviderOperationId { get; init; }
        public string? ExpectedRevision { get; init; }
        public string? ObservedRevision { get; init; }
        public DateTimeOffset? ChildUpdatedAt { get; init; }
        public string? SplitStateReason { get; init; }
    }
}
