// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Unit coverage for the substrate-neutral single-host rolling-replace deploy backend (#2460). All
/// container-runtime, proxy-swap, and health-probe seams are faked so the full start / health-gate /
/// cutover / rollback lifecycle is verified without Docker or a live proxy.
/// </summary>
public sealed class YarpRollingDeployBackendTests
{
    private const string ActiveAddress = "http://127.0.0.1:18080/";
    private const string StandbyAddress = "http://127.0.0.1:18081/";
    private const string TargetId = "self-host-prod";
    private const string DesiredRevision = "honua/app:2.0";
    private const string CurrentRevision = "honua/app:1.0";

    [Fact]
    public void BackendIdentity_MatchesContract()
    {
        var backend = CreateBackend(out _, out _, out _);
        backend.BackendName.Should().Be(YarpRollingDeployBackend.AdapterBackendName);
        backend.BackendName.Should().Be("honua-yarp-rolling");
        backend.TargetKind.Should().Be(DeployTargetKind.SelfHostedRolling);
    }

    [Fact]
    public async Task GetCapabilities_ReflectsSingleSwitchCutover()
    {
        var backend = CreateBackend(out _, out _, out _);

        var capabilities = await backend.GetCapabilitiesAsync();

        capabilities.SupportsRollback.Should().BeTrue();
        capabilities.SupportsCancellation.Should().BeFalse();
        capabilities.SupportsTrafficShifting.Should().BeFalse();
        capabilities.RequiresOutOfBandMigrations.Should().BeTrue();
        capabilities.SupportsProgressPolling.Should().BeTrue();
        capabilities.SupportsRevisionPinning.Should().BeTrue();
    }

    [Fact]
    public async Task PlanAsync_WhenProxyDisabledAndRuntimeUnavailable_ReportsBlockingReasons()
    {
        var backend = CreateBackend(out var runtime, out var proxy, out _, proxyConfigured: false);
        runtime.Available = false;

        var plan = await backend.PlanAsync(CreateSpec());

        plan.IsReadyToSubmit.Should().BeFalse();
        plan.BlockingReasons.Should().Contain(r => r.Contains("reverse proxy is not enabled"));
        plan.BlockingReasons.Should().Contain(r => r.Contains("container runtime"));
        proxy.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public async Task PlanAsync_WhenReady_IsReadyToSubmit()
    {
        var backend = CreateBackend(out _, out _, out _);

        var plan = await backend.PlanAsync(CreateSpec());

        plan.IsReadyToSubmit.Should().BeTrue();
        plan.BlockingReasons.Should().BeEmpty();
        plan.RequiresOutOfBandMigrations.Should().BeTrue();
    }

    [Fact]
    public async Task StartAsync_LaunchesStandbyReplica_ReturnsSubmitted()
    {
        var backend = CreateBackend(out var runtime, out _, out _);

        var result = await backend.StartAsync(CreateOperation(WorkflowOperationStatus.Submitted));

        result.Status.Should().Be(WorkflowOperationStatus.Submitted);
        runtime.RunRequests.Should().ContainSingle();
        var request = runtime.RunRequests[0];
        request.HostPort.Should().Be(18081);
        request.Labels[YarpRollingDeployBackend.LabelRole].Should().Be(YarpRollingDeployBackend.RoleStandby);
        request.Labels[YarpRollingDeployBackend.LabelRevision].Should().Be(DesiredRevision);
    }

    [Fact]
    public async Task ObserveAsync_StandbyUnhealthy_DoesNotRecommendPromotion()
    {
        var backend = CreateBackend(out var runtime, out _, out var probe);
        probe.Healthy = false;
        await backend.StartAsync(CreateOperation(WorkflowOperationStatus.Submitted));

        var observation = await backend.ObserveAsync(CreateOperation(WorkflowOperationStatus.Reconciling));

        observation.Status.Should().Be(WorkflowOperationStatus.Reconciling);
        observation.PromotionRecommended.Should().BeFalse();
    }

    [Fact]
    public async Task ObserveAsync_StandbyHealthy_RecommendsPromotion()
    {
        var backend = CreateBackend(out _, out _, out var probe);
        probe.Healthy = true;
        await backend.StartAsync(CreateOperation(WorkflowOperationStatus.Submitted));

        var observation = await backend.ObserveAsync(CreateOperation(WorkflowOperationStatus.Reconciling));

        observation.Status.Should().Be(WorkflowOperationStatus.Reconciling);
        observation.PromotionRecommended.Should().BeTrue();
    }

    [Fact]
    public async Task PromoteAsync_SwapsProxyAndStopsOldReplica_ReturnsSucceeded()
    {
        var backend = CreateBackend(out var runtime, out var proxy, out var probe);
        probe.Healthy = true;
        runtime.SeedActive(ActiveContainerName(), CurrentRevision);
        await backend.StartAsync(CreateOperation(WorkflowOperationStatus.Submitted));

        var observation = await backend.PromoteAsync(CreateOperation(WorkflowOperationStatus.Reconciling));

        observation.Status.Should().Be(WorkflowOperationStatus.Succeeded);
        observation.ObservedRevision.Should().Be(DesiredRevision);
        proxy.ActiveDestinationAddress.Should().Be(StandbyAddress);
        runtime.StopRequests.Should().Contain(ActiveContainerName());
    }

    [Fact]
    public async Task RollbackAsync_BeforeCutover_StopsStandbyOnly()
    {
        var backend = CreateBackend(out var runtime, out var proxy, out _);
        runtime.SeedActive(ActiveContainerName(), CurrentRevision);
        await backend.StartAsync(CreateOperation(WorkflowOperationStatus.Submitted));

        var observation = await backend.RollbackAsync(CreateOperation(WorkflowOperationStatus.RollbackRequested));

        observation.Status.Should().Be(WorkflowOperationStatus.RolledBack);
        runtime.StopRequests.Should().Contain(StandbyContainerName());
        runtime.StopRequests.Should().NotContain(ActiveContainerName());
        // Proxy was never repointed because the old replica was never touched.
        proxy.ActiveDestinationAddress.Should().Be(ActiveAddress);
    }

    [Fact]
    public async Task RollbackAsync_AfterCutover_RepointsProxyToOldReplica()
    {
        var backend = CreateBackend(out var runtime, out var proxy, out _);
        runtime.SeedActive(ActiveContainerName(), CurrentRevision);
        await backend.StartAsync(CreateOperation(WorkflowOperationStatus.Submitted));
        // Simulate a completed cutover: the proxy already points at the standby destination.
        await proxy.SwapAsync(StandbyAddress, CancellationToken.None);

        var observation = await backend.RollbackAsync(CreateOperation(WorkflowOperationStatus.RollbackRequested));

        observation.Status.Should().Be(WorkflowOperationStatus.RollbackRequested);
        proxy.ActiveDestinationAddress.Should().Be(ActiveAddress);
        // The failed new revision must be stopped after the drain window, otherwise ObserveAsync
        // (which settles the rollback once the standby is gone) would spin forever.
        runtime.StopRequests.Should().Contain(StandbyContainerName());
    }

    private static string StandbyContainerName() => "honua-app-18081";

    private static string ActiveContainerName() => "honua-app-18080";

    private static YarpRollingDeployBackend CreateBackend(
        out FakeContainerRuntimeClient runtime,
        out FakeProxyStateSwapper proxy,
        out FakeLocalReplicaHealthProbe probe,
        bool proxyConfigured = true)
    {
        runtime = new FakeContainerRuntimeClient();
        proxy = new FakeProxyStateSwapper(proxyConfigured, ActiveAddress);
        probe = new FakeLocalReplicaHealthProbe();
        var options = Options.Create(new SelfHostedDeployOptions
        {
            Enabled = true,
            Host = "127.0.0.1",
            ActivePort = 18080,
            StandbyPort = 18081,
            ContainerPort = 8080,
            ContainerRuntime = "docker",
            ContainerNamePrefix = "honua-app",
            DrainDelaySeconds = 0
        });

        return new YarpRollingDeployBackend(runtime, proxy, probe, options, NullLogger<YarpRollingDeployBackend>.Instance);
    }

    private static DeployOperationSpec CreateSpec()
        => new()
        {
            TargetId = TargetId,
            TargetKind = DeployTargetKind.SelfHostedRolling,
            Backend = YarpRollingDeployBackend.AdapterBackendName,
            Environment = "prod",
            TargetName = "honua",
            CurrentRevision = CurrentRevision,
            DesiredRevision = DesiredRevision
        };

    private static WorkflowOperationRecord CreateOperation(WorkflowOperationStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkflowOperationRecord
        {
            OperationId = "op-selfhost-1",
            Kind = WorkflowOperationKind.Deploy,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
            ProviderOperationId = "container-standby",
            Deploy = CreateSpec()
        };
    }

    private sealed class FakeContainerRuntimeClient : IContainerRuntimeClient
    {
        private readonly Dictionary<string, ContainerSummary> _containers = new(StringComparer.Ordinal);

        public bool Available { get; set; } = true;

        public List<ContainerRunRequest> RunRequests { get; } = [];

        public List<string> StopRequests { get; } = [];

        public void SeedActive(string name, string revision)
            => _containers[name] = new ContainerSummary
            {
                Id = name,
                Name = name,
                Running = true,
                Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [YarpRollingDeployBackend.LabelTarget] = TargetId,
                    [YarpRollingDeployBackend.LabelRole] = YarpRollingDeployBackend.RoleActive,
                    [YarpRollingDeployBackend.LabelRevision] = revision
                }
            };

        public Task<bool> IsAvailableAsync(string executable, CancellationToken cancellationToken)
            => Task.FromResult(Available);

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
            StopRequests.Add(containerNameOrId);
            _containers.Remove(containerNameOrId);
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
                .ToList();
            return Task.FromResult(matches);
        }
    }

    private sealed class FakeProxyStateSwapper(bool configured, string initialAddress) : IProxyStateSwapper
    {
        public bool IsConfigured { get; } = configured;

        public string? ActiveDestinationAddress { get; private set; } = initialAddress;

        public Task SwapAsync(string destinationAddress, CancellationToken cancellationToken)
        {
            ActiveDestinationAddress = destinationAddress;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLocalReplicaHealthProbe : ILocalReplicaHealthProbe
    {
        public bool Healthy { get; set; } = true;

        public Task<LocalReplicaHealthResult> ProbeAsync(
            string url,
            int samples,
            int timeoutSeconds,
            int expectedStatusCode,
            CancellationToken cancellationToken)
            => Task.FromResult(new LocalReplicaHealthResult
            {
                Attempts = samples,
                Failures = Healthy ? 0 : samples,
                Detail = Healthy ? "healthy" : "unhealthy"
            });
    }
}
