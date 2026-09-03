// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Executable child-settlement evidence for coordinated release rollback (#3890). The container step
/// is the production <see cref="CoordinatedContainerStepExecutor"/> over the production deploy service,
/// deploy reconciler, and self-hosted rolling backend. The local loopback servers provide functional
/// body markers so the receipt proves which revision is serving when the parent becomes terminal.
/// </summary>
public sealed class CoordinatedReleaseRollbackSettlementIntegrationTests(ITestOutputHelper output)
{
    private const string TargetId = "coordinated-local";
    private const string CurrentRevision = "honua/server:v20";
    private const string DesiredRevision = "honua/server:v21";

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

        var activePort = GetFreePort();
        var standbyPort = GetFreePort();
        await using var activeServer = await LocalMarkerServer.StartAsync(activePort, CurrentRevision);
        await using var standbyServer = await LocalMarkerServer.StartAsync(standbyPort, DesiredRevision);

        var store = new InMemoryWorkflowOperationStore();
        var options = CreateOptions(activePort, standbyPort);
        var runtime = new LocalContainerRuntime(options.Value.ContainerNamePrefix);
        if (priorRevisionAvailable)
        {
            runtime.SeedActive(TargetId, CurrentRevision, activePort);
        }

        var metadata = new FaultingMetadataStep();
        var operation = await CreateCoordinatedOperationAsync(store, priorRevisionAvailable);
        ledger.ParentOperationId = operation.OperationId;
        await ledger.CaptureAsync(store, operation.OperationId, "created");

        try
        {
            var first = CreateProductionReconciler(
                store,
                runtime,
                options,
                metadata,
                out var firstProxy,
                out var firstBackend);

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
            firstBackend.Should().NotBeNull();

            ledger.RestartBoundary = new RestartBoundaryReceipt
            {
                At = DateTimeOffset.UtcNow,
                ParentStatusBeforeRestart = pending.Status.ToString(),
                ChildStatusBeforeRestart = childBeforeRestart.Status.ToString()
            };
            output.WriteLine($"restart-boundary: {ledger.RestartBoundary.At:O}");

            // A new conductor and new child executor must reconstruct the pending provider operation
            // from the durable child record. No rollback request is made by this restart path.
            var restarted = CreateProductionReconciler(
                store,
                runtime,
                options,
                metadata,
                out var restartedProxy,
                out var restartedBackend);

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
                childAfterRestartPoll.Should().BeNull("a missing child record must not be treated as a successful rollback");
                if (childAfterRestartPoll is not null)
                {
                    throw new InvalidOperationException("The child operation unexpectedly remained in the store.");
                }
                afterRestartPoll.ErrorMessage.Should().Contain("could not be read back");
                runtime.RunRequests.Should().HaveCount(2, "the missing child must not trigger a second provider rollback");
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
                runtime.RunRequests.Should().HaveCount(2, "the initial standby and one prior-revision relaunch are the only provider starts");
                restartedBackend.RollbackRequestCalls.Should().Be(0, "restart observes settlement and must not issue a second provider rollback");

                var bodyObservedAt = DateTimeOffset.UtcNow;
                var body = await ReadBodyAsync(restartedProxy.ActiveDestinationAddress!);
                body.Should().Be(CurrentRevision);
                ledger.FunctionalBodyReceipt = new FunctionalBodyReceipt
                {
                    At = bodyObservedAt,
                    Uri = new Uri(new Uri(restartedProxy.ActiveDestinationAddress!), "body").ToString(),
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
                childAfterRestartPoll.Should().NotBeNull();
                var manualChildAfterRestart = childAfterRestartPoll ?? throw new InvalidOperationException("The manual child operation could not be read back.");
                manualChildAfterRestart.Status.Should().Be(WorkflowOperationStatus.ManualInterventionRequired);
                manualChildAfterRestart.ObservedState.Should().Be(DesiredRevision, "the ledger must preserve that the failed revision remained observed");
                afterRestartPoll.CoordinatedRelease.Steps.Single(s => s.Step == CoordinatedReleaseStep.ContainerRollout).Status
                    .Should().Be(CoordinatedReleaseStepStatus.RollbackRequested);
                afterRestartPoll.ErrorMessage.Should().Contain("operator action");
                runtime.RunRequests.Should().HaveCount(1, "the provider reported that no prior revision was available");
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
        LocalContainerRuntime runtime,
        IOptions<SelfHostedDeployOptions> options,
        FaultingMetadataStep metadata,
        out LocalProxyStateSwapper proxy,
        out LocalSubstrateDeployBackend backend)
    {
        proxy = new LocalProxyStateSwapper(options.Value.Host, options.Value.ActivePort);
        backend = new LocalSubstrateDeployBackend(runtime, proxy, options);
        var registry = new LocalDeployTargetRegistry(options);
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
        var control = new CoordinatedReleaseControlService([(IWorkflowOperationStore)store]);
        for (var cycle = 0; cycle < 40; cycle++)
        {
            await reconciler.ReconcileCoordinatedReleaseAsync(operationId);
            await ledger.CaptureAsync(store, operationId, $"cycle-{cycle}");
            var operation = (await store.GetAsync(operationId))!;
            if (operation.Status == WorkflowOperationStatus.AwaitingApproval)
            {
                await control.ApproveGateAsync(operationId, operation.CoordinatedRelease!.CurrentStep, "operator", "approved", default);
                await ledger.CaptureAsync(store, operationId, $"approved-{operation.CoordinatedRelease.CurrentStep}");
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

    private static IOptions<SelfHostedDeployOptions> CreateOptions(int activePort, int standbyPort)
        => Options.Create(new SelfHostedDeployOptions
        {
            Enabled = true,
            Host = "127.0.0.1",
            ActivePort = activePort,
            StandbyPort = standbyPort,
            ContainerPort = 8080,
            ContainerRuntime = "local-test-runtime",
            ContainerNamePrefix = $"honua-coordinated-{Guid.NewGuid():N}",
            HealthPath = "/healthz/ready",
            HealthProbeSamples = 1,
            HealthProbeTimeoutSeconds = 5,
            DrainDelaySeconds = 0
        });

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
            application.MapGet("/healthz/ready", () => Results.Text("Ready"));
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

    private sealed class LocalSubstrateDeployBackend(
        LocalContainerRuntime runtime,
        LocalProxyStateSwapper proxy,
        IOptions<SelfHostedDeployOptions> options) : IDeployBackend
    {
        private readonly YarpRollingDeployBackend _inner = new(
            runtime,
            proxy,
            new HttpLocalReplicaHealthProbe(),
            options,
            NullLogger<YarpRollingDeployBackend>.Instance);

        public int RollbackRequestCalls { get; private set; }
        public int RollbackObservationCalls { get; private set; }
        public string BackendName => YarpRollingDeployBackend.AdapterBackendName;
        public DeployTargetKind TargetKind => DeployTargetKind.SelfHostedRolling;

        public Task<DeployBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
            => _inner.GetCapabilitiesAsync(cancellationToken);

        public Task<DeployPlan> PlanAsync(DeployOperationSpec spec, CancellationToken cancellationToken = default)
            => _inner.PlanAsync(spec, cancellationToken);

        public Task<DeploySubmissionResult> StartAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => _inner.StartAsync(operation, cancellationToken);

        public async Task<DeployObservation> ObserveAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
        {
            if (operation.Status == WorkflowOperationStatus.RollbackRequested)
            {
                RollbackObservationCalls++;
                if (runtime.ConsumePendingRollbackObservation())
                {
                    return new DeployObservation
                    {
                        Status = WorkflowOperationStatus.RollbackRequested,
                        ProviderOperationId = operation.ProviderOperationId,
                        ObservedRevision = operation.Deploy?.CurrentRevision,
                        Message = "Local provider rollback is still settling after restart."
                    };
                }
            }

            return await _inner.ObserveAsync(operation, cancellationToken);
        }

        public async Task<DeployObservation> PromoteAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => await _inner.PromoteAsync(operation, cancellationToken);

        public async Task<DeployObservation> RollbackAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
        {
            RollbackRequestCalls++;
            var observation = await _inner.RollbackAsync(operation, cancellationToken);
            if (observation.Status == WorkflowOperationStatus.RollbackRequested)
            {
                runtime.DelayOneRollbackObservation();
            }

            return observation;
        }
    }

    private sealed class LocalDeployTargetRegistry(IOptions<SelfHostedDeployOptions> options) : IDeployTargetRegistry
    {
        private readonly DeployTargetDefinition _target = new()
        {
            TargetId = TargetId,
            TargetKind = DeployTargetKind.SelfHostedRolling,
            Backend = YarpRollingDeployBackend.AdapterBackendName,
            Environment = "local",
            TargetName = "coordinated-local",
            ArtifactReference = DesiredRevision,
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SelfHostedDeployParameterKeys.ActivePort] = options.Value.ActivePort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                [SelfHostedDeployParameterKeys.StandbyPort] = options.Value.StandbyPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                [SelfHostedDeployParameterKeys.ContainerPort] = options.Value.ContainerPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                [SelfHostedDeployParameterKeys.Image] = DesiredRevision
            }
        };

        public Task<IReadOnlyList<DeployTargetDefinition>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DeployTargetDefinition>>([_target]);

        public Task<DeployTargetDefinition?> GetAsync(string targetId, CancellationToken cancellationToken = default)
            => Task.FromResult<DeployTargetDefinition?>(targetId == TargetId ? _target : null);
    }

    private sealed class LocalContainerRuntime(string containerNamePrefix) : IContainerRuntimeClient
    {
        private readonly ConcurrentDictionary<string, ContainerSummary> _containers = new(StringComparer.Ordinal);
        private int _pendingRollbackObservations;
        public List<ContainerRunRequest> RunRequests { get; } = [];

        public void DelayOneRollbackObservation()
            => Interlocked.Exchange(ref _pendingRollbackObservations, 1);

        public bool ConsumePendingRollbackObservation()
            => Interlocked.Exchange(ref _pendingRollbackObservations, 0) == 1;

        public void SeedActive(string targetId, string revision, int port)
        {
            var name = $"{containerNamePrefix}-{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            _containers[name] = Summary(name, targetId, YarpRollingDeployBackend.RoleActive, revision);
        }

        public Task<bool> IsAvailableAsync(string executable, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<string> RunAsync(ContainerRunRequest request, CancellationToken cancellationToken)
        {
            RunRequests.Add(request);
            _containers[request.ContainerName] = new ContainerSummary
            {
                Id = request.ContainerName,
                Name = request.ContainerName,
                Running = true,
                Labels = request.Labels
            };
            return Task.FromResult(request.ContainerName);
        }

        public Task StopAsync(string executable, string containerNameOrId, CancellationToken cancellationToken)
        {
            _containers.TryRemove(containerNameOrId, out _);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ContainerSummary>> ListAsync(
            string executable,
            IReadOnlyDictionary<string, string> labelSelectors,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<ContainerSummary> matches = _containers.Values
                .Where(container => labelSelectors.All(selector =>
                    container.Labels.TryGetValue(selector.Key, out var value)
                    && string.Equals(value, selector.Value, StringComparison.Ordinal)))
                .ToArray();
            return Task.FromResult(matches);
        }

        private static ContainerSummary Summary(string name, string targetId, string role, string revision)
            => new()
            {
                Id = name,
                Name = name,
                Running = true,
                Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [YarpRollingDeployBackend.LabelTarget] = targetId,
                    [YarpRollingDeployBackend.LabelRole] = role,
                    [YarpRollingDeployBackend.LabelRevision] = revision
                }
            };
    }

    private sealed class LocalProxyStateSwapper(string host, int activePort) : IProxyStateSwapper
    {
        public bool IsConfigured => true;
        public string? ActiveDestinationAddress { get; private set; } = $"http://{host}:{activePort}/";

        public Task SwapAsync(string destinationAddress, CancellationToken cancellationToken)
        {
            ActiveDestinationAddress = destinationAddress;
            return Task.CompletedTask;
        }
    }

    private sealed class HttpLocalReplicaHealthProbe : ILocalReplicaHealthProbe
    {
        public async Task<LocalReplicaHealthResult> ProbeAsync(
            string url,
            int samples,
            int timeoutSeconds,
            int expectedStatusCode,
            CancellationToken cancellationToken)
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            var failures = 0;
            for (var i = 0; i < samples; i++)
            {
                try
                {
                    using var response = await client.GetAsync(url, cancellationToken);
                    if ((int)response.StatusCode != expectedStatusCode)
                    {
                        failures++;
                    }
                }
                catch (HttpRequestException)
                {
                    failures++;
                }
            }

            return new LocalReplicaHealthResult
            {
                Attempts = samples,
                Failures = failures,
                Detail = failures == 0 ? "local marker server healthy" : "local marker server unhealthy"
            };
        }
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
            _operations[operation.OperationId] = operation;
            return Task.FromResult(true);
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
                root = Path.Join(FindRepositoryRoot(), "tests", "TestResults");
            }

            Directory.CreateDirectory(root);
            var path = Path.Join(root, $"coordinated-release-rollback-{Scenario}-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            return path;
        }

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Honua.sln")))
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
