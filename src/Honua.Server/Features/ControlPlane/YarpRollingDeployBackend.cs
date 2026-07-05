// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

/// <summary>
/// Parameter keys read from a self-hosted rolling deploy operation's parameters. Every value has an
/// options-level default (<see cref="SelfHostedDeployOptions"/>); per-operation overrides keep the
/// backend stateless because each operation carries its own port/image assignment.
/// </summary>
internal static class SelfHostedDeployParameterKeys
{
    /// <summary>Container image to launch for the standby replica. Falls back to the deploy spec's desired revision.</summary>
    public const string Image = "selfhosted.image";

    /// <summary>Host port the standby replica listens on (the new revision).</summary>
    public const string StandbyPort = "selfhosted.standby_port";

    /// <summary>Host port the currently active replica listens on (the live revision fronted by the proxy).</summary>
    public const string ActivePort = "selfhosted.active_port";

    /// <summary>Container port the replica image exposes.</summary>
    public const string ContainerPort = "selfhosted.container_port";

    /// <summary>Prefix for <c>env.&lt;NAME&gt;</c> environment variables passed into the replica container.</summary>
    public const string EnvironmentPrefix = "env.";
}

/// <summary>
/// Container-runtime seam so the backend can drive <c>docker</c>/<c>podman</c> without taking a hard
/// dependency on a process launcher, and so unit tests can fake replica lifecycle without a daemon.
/// </summary>
internal interface IContainerRuntimeClient
{
    /// <summary>Whether the configured container runtime executable is invocable on this host.</summary>
    Task<bool> IsAvailableAsync(string executable, CancellationToken cancellationToken);

    /// <summary>Runs a detached container and returns its id (or name when the id is unavailable).</summary>
    Task<string> RunAsync(ContainerRunRequest request, CancellationToken cancellationToken);

    /// <summary>Stops and removes a container by name or id. Best-effort; a missing container is not an error.</summary>
    Task StopAsync(string executable, string containerNameOrId, CancellationToken cancellationToken);

    /// <summary>Lists containers matching all supplied labels, including their labels and running state.</summary>
    Task<IReadOnlyList<ContainerSummary>> ListAsync(
        string executable,
        IReadOnlyDictionary<string, string> labelSelectors,
        CancellationToken cancellationToken);
}

/// <summary>Request describing a detached container to run for a rolling replica.</summary>
internal sealed record ContainerRunRequest
{
    /// <summary>Container runtime executable (<c>docker</c> or <c>podman</c>).</summary>
    public required string Executable { get; init; }

    /// <summary>Container image reference.</summary>
    public required string Image { get; init; }

    /// <summary>Deterministic container name so state can be reconstructed from <c>docker ps</c>.</summary>
    public required string ContainerName { get; init; }

    /// <summary>Host port to publish.</summary>
    public required int HostPort { get; init; }

    /// <summary>Container port to publish from.</summary>
    public required int ContainerPort { get; init; }

    /// <summary>Labels stamped onto the container so observation can reconstruct role/revision statelessly.</summary>
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Environment variables passed into the container.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>Read-only snapshot of a container discovered through the runtime.</summary>
internal sealed record ContainerSummary
{
    /// <summary>Container id.</summary>
    public required string Id { get; init; }

    /// <summary>Container name.</summary>
    public required string Name { get; init; }

    /// <summary>Container labels.</summary>
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Whether the container is currently running.</summary>
    public bool Running { get; init; }
}

/// <summary>
/// Atomic reverse-proxy destination swap seam. The embedded-YARP implementation rewrites the cluster
/// destination through <c>InMemoryConfigProvider</c> so the cutover is a single, atomic switch.
/// </summary>
internal interface IProxyStateSwapper
{
    /// <summary>Whether the embedded reverse proxy is configured/enabled on this host.</summary>
    bool IsConfigured { get; }

    /// <summary>The destination address the proxy currently forwards to, or null when unknown/unconfigured.</summary>
    string? ActiveDestinationAddress { get; }

    /// <summary>Atomically points the proxy cluster at <paramref name="destinationAddress"/>.</summary>
    Task SwapAsync(string destinationAddress, CancellationToken cancellationToken);
}

/// <summary>Result of probing a co-located replica's health endpoint.</summary>
internal sealed record LocalReplicaHealthResult
{
    /// <summary>Number of probe requests issued.</summary>
    public int Attempts { get; init; }

    /// <summary>Number of probe requests that did not return the expected status (or threw).</summary>
    public int Failures { get; init; }

    /// <summary>Whether every issued probe returned the expected status.</summary>
    public bool Healthy => Attempts > 0 && Failures == 0;

    /// <summary>Human-readable detail for the operation phase message.</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// Health-probe seam for a co-located replica. Unlike <c>HttpDeployHealthProbe</c>, the target here is
/// a replica on this same host (loopback), which the shared <c>OutboundHttpUrlValidator</c> deliberately
/// blocks as an SSRF destination. This local-probe path therefore does not run the outbound SSRF guard:
/// probing a sibling replica on localhost is the intended behavior, not an outbound call to an untrusted
/// host. The URL is built by the backend from validated numeric ports, never from client input.
/// </summary>
internal interface ILocalReplicaHealthProbe
{
    /// <summary>Issues <paramref name="samples"/> sequential GETs and counts unhealthy responses.</summary>
    Task<LocalReplicaHealthResult> ProbeAsync(
        string url,
        int samples,
        int timeoutSeconds,
        int expectedStatusCode,
        CancellationToken cancellationToken);
}

/// <summary>
/// Substrate-neutral, single-host rolling-replace deploy backend (ADR-0060). The Honua process fronts
/// traffic with embedded YARP and owns the cutover end-to-end: it launches a standby replica of the
/// desired revision, health-gates it, atomically swaps the reverse-proxy destination, and keeps the old
/// replica serving until the new one is healthy. This delivers the on-prem/air-gapped, zero-cloud
/// zero-downtime upgrade and rollback story — it is not cloud blue/green.
/// </summary>
/// <remarks>
/// <para>
/// Statelessness discipline (the server process may itself be replaced mid-operation): the backend
/// persists no in-memory state beyond what can be reconstructed from container labels/names plus the
/// operation record parameters. <see cref="ObserveAsync"/> rediscovers the standby and active replicas
/// from <c>docker ps</c> labels (<c>honua.role</c>, <c>honua.revision</c>, <c>honua.operation</c>).
/// </para>
/// <para>
/// A bare-process replica mode (launch the revision as a plain OS process instead of a container) is a
/// deliberate future extension and is out of scope here; the container-runtime seam keeps that door open.
/// </para>
/// </remarks>
internal sealed partial class YarpRollingDeployBackend(
    IContainerRuntimeClient containerRuntime,
    IProxyStateSwapper proxySwapper,
    ILocalReplicaHealthProbe healthProbe,
    Microsoft.Extensions.Options.IOptions<SelfHostedDeployOptions> options,
    ILogger<YarpRollingDeployBackend> logger) : IDeployBackend
{
    internal const string AdapterBackendName = "honua-yarp-rolling";

    internal const string LabelOperation = "honua.operation";
    internal const string LabelTarget = "honua.target";
    internal const string LabelRevision = "honua.revision";
    internal const string LabelRole = "honua.role";
    internal const string RoleStandby = "standby";
    internal const string RoleActive = "active";

    private readonly SelfHostedDeployOptions _options = options.Value;

    public string BackendName => AdapterBackendName;

    public DeployTargetKind TargetKind => DeployTargetKind.SelfHostedRolling;

    public Task<DeployBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new DeployBackendCapabilities
        {
            SupportsRollback = true,
            SupportsCancellation = false,
            // Single-switch cutover, not weighted traffic shifting.
            SupportsTrafficShifting = false,
            RequiresOutOfBandMigrations = true,
            SupportsProgressPolling = true,
            SupportsRevisionPinning = true
        });

    public async Task<DeployPlan> PlanAsync(DeployOperationSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        cancellationToken.ThrowIfCancellationRequested();

        var blockingReasons = new List<string>();
        var warnings = new List<string>();
        var target = ResolveTarget(spec);

        if (string.IsNullOrWhiteSpace(spec.TargetId))
        {
            blockingReasons.Add("A target ID is required.");
        }

        if (string.IsNullOrWhiteSpace(target.Image))
        {
            blockingReasons.Add("A container image (selfhosted.image or the deploy desired revision) is required.");
        }

        if (target.StandbyPort <= 0 || target.ActivePort <= 0)
        {
            blockingReasons.Add("Both selfhosted.standby_port and selfhosted.active_port must be configured.");
        }
        else if (target.StandbyPort == target.ActivePort)
        {
            blockingReasons.Add("selfhosted.standby_port and selfhosted.active_port must differ.");
        }

        if (!proxySwapper.IsConfigured)
        {
            blockingReasons.Add("The embedded reverse proxy is not enabled; set ControlPlane:SelfHosted:Enabled=true to use the rolling deploy backend.");
        }

        if (!await containerRuntime.IsAvailableAsync(target.ContainerRuntime, cancellationToken).ConfigureAwait(false))
        {
            blockingReasons.Add($"The container runtime '{target.ContainerRuntime}' is not available on this host.");
        }

        return new DeployPlan
        {
            IsReadyToSubmit = blockingReasons.Count == 0 && !spec.RequiresApproval,
            RequiresApproval = spec.RequiresApproval,
            RequiresOutOfBandMigrations = true,
            BlockingReasons = blockingReasons,
            Warnings = warnings
        };
    }

    public async Task<DeploySubmissionResult> StartAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var spec = operation.Deploy ?? throw new InvalidOperationException("Deploy workflow operation is missing deploy metadata.");
        cancellationToken.ThrowIfCancellationRequested();

        var target = ResolveTarget(spec);
        EnsureValidTarget(target);

        try
        {
            var request = new ContainerRunRequest
            {
                Executable = target.ContainerRuntime,
                Image = target.Image!,
                ContainerName = StandbyContainerName(target),
                HostPort = target.StandbyPort,
                ContainerPort = target.ContainerPort,
                Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [LabelOperation] = operation.OperationId,
                    [LabelTarget] = spec.TargetId,
                    [LabelRevision] = spec.DesiredRevision,
                    [LabelRole] = RoleStandby
                },
                Environment = BuildEnvironment(spec)
            };

            var containerId = await containerRuntime.RunAsync(request, cancellationToken).ConfigureAwait(false);
            Log.StandbyStarted(logger, operation.OperationId, spec.TargetId, request.ContainerName, spec.DesiredRevision);

            return new DeploySubmissionResult
            {
                Status = WorkflowOperationStatus.Submitted,
                ProviderOperationId = containerId,
                ObservedRevision = spec.CurrentRevision,
                Message = $"Started standby replica '{request.ContainerName}' ({spec.DesiredRevision}) on port {target.StandbyPort}; the active replica keeps serving until the new one is healthy."
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.OperationFailed(logger, operation.OperationId, spec.TargetId, ex.Message);
            return new DeploySubmissionResult
            {
                Status = WorkflowOperationStatus.Failed,
                Message = "Failed to start the standby replica for the rolling deploy."
            };
        }
    }

    public async Task<DeployObservation> ObserveAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var spec = operation.Deploy ?? throw new InvalidOperationException("Deploy workflow operation is missing deploy metadata.");
        cancellationToken.ThrowIfCancellationRequested();

        var target = ResolveTarget(spec);

        try
        {
            var replicas = await DiscoverReplicasAsync(operation, target, cancellationToken).ConfigureAwait(false);

            if (operation.Status == WorkflowOperationStatus.RollbackRequested)
            {
                // Rollback settles once the standby replica is gone and the active replica is serving.
                var standbyGone = replicas.Standby is null || !replicas.Standby.Running;
                if (standbyGone)
                {
                    return new DeployObservation
                    {
                        Status = WorkflowOperationStatus.RolledBack,
                        ProviderOperationId = operation.ProviderOperationId,
                        ObservedRevision = spec.CurrentRevision,
                        Message = "Rolling deploy rolled back: the standby replica was stopped and the active replica is serving."
                    };
                }

                return new DeployObservation
                {
                    Status = WorkflowOperationStatus.RollbackRequested,
                    ProviderOperationId = operation.ProviderOperationId,
                    ObservedRevision = spec.CurrentRevision,
                    Message = "Rolling deploy rollback is still settling the standby replica."
                };
            }

            if (replicas.Standby is null || !replicas.Standby.Running)
            {
                return new DeployObservation
                {
                    Status = WorkflowOperationStatus.Reconciling,
                    ProviderOperationId = operation.ProviderOperationId,
                    ObservedRevision = spec.CurrentRevision,
                    Message = "Waiting for the standby replica to appear."
                };
            }

            var health = await ProbeStandbyAsync(target, cancellationToken).ConfigureAwait(false);
            if (!health.Healthy)
            {
                return new DeployObservation
                {
                    Status = WorkflowOperationStatus.Reconciling,
                    ProviderOperationId = operation.ProviderOperationId,
                    ObservedRevision = spec.CurrentRevision,
                    Message = $"Standby replica ({spec.DesiredRevision}) is not healthy yet. {health.Detail}".TrimEnd()
                };
            }

            // Healthy + stable: recommend promotion. The old replica keeps serving until PromoteAsync
            // performs the atomic cutover, so there is zero downtime through this window.
            return new DeployObservation
            {
                Status = WorkflowOperationStatus.Reconciling,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = spec.CurrentRevision,
                PromotionRecommended = true,
                Message = $"Standby replica ({spec.DesiredRevision}) is healthy on port {target.StandbyPort} and ready for cutover."
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.OperationFailed(logger, operation.OperationId, spec.TargetId, ex.Message);
            return new DeployObservation
            {
                Status = WorkflowOperationStatus.Failed,
                ProviderOperationId = operation.ProviderOperationId,
                Message = "Rolling deploy observation failed. Check the deploy controller logs for details."
            };
        }
    }

    public async Task<DeployObservation> PromoteAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var spec = operation.Deploy ?? throw new InvalidOperationException("Deploy workflow operation is missing deploy metadata.");
        cancellationToken.ThrowIfCancellationRequested();

        var target = ResolveTarget(spec);
        EnsureValidTarget(target);

        try
        {
            // Verify the standby is serving before cutover; never point the proxy at an unhealthy target.
            var preHealth = await ProbeStandbyAsync(target, cancellationToken).ConfigureAwait(false);
            if (!preHealth.Healthy)
            {
                return new DeployObservation
                {
                    Status = WorkflowOperationStatus.Reconciling,
                    ProviderOperationId = operation.ProviderOperationId,
                    ObservedRevision = spec.CurrentRevision,
                    Message = $"Standby replica ({spec.DesiredRevision}) is not healthy at cutover; holding. {preHealth.Detail}".TrimEnd()
                };
            }

            var replicas = await DiscoverReplicasAsync(operation, target, cancellationToken).ConfigureAwait(false);

            // Atomic single-switch cutover: point the proxy at the standby destination.
            var standbyAddress = ReplicaAddress(target, target.StandbyPort);
            await proxySwapper.SwapAsync(standbyAddress, cancellationToken).ConfigureAwait(false);
            Log.CutoverCompleted(logger, operation.OperationId, spec.TargetId, standbyAddress);

            // Confirm the newly active replica serves after the swap.
            var postHealth = await ProbeStandbyAsync(target, cancellationToken).ConfigureAwait(false);
            if (!postHealth.Healthy)
            {
                return new DeployObservation
                {
                    Status = WorkflowOperationStatus.Reconciling,
                    ProviderOperationId = operation.ProviderOperationId,
                    ObservedRevision = spec.DesiredRevision,
                    RollbackRecommended = true,
                    Message = $"Cutover completed but the new active replica did not answer health checks. {postHealth.Detail}".TrimEnd()
                };
            }

            // Keep-old-until-healthy: only after the new replica is confirmed serving do we drain and
            // stop the old replica, after a configurable delay so in-flight requests can complete.
            if (replicas.Active is { } active)
            {
                if (_options.DrainDelaySeconds > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(_options.DrainDelaySeconds), cancellationToken).ConfigureAwait(false);
                }

                await containerRuntime.StopAsync(target.ContainerRuntime, active.Name, cancellationToken).ConfigureAwait(false);
                Log.OldReplicaStopped(logger, operation.OperationId, spec.TargetId, active.Name);
            }

            return new DeployObservation
            {
                Status = WorkflowOperationStatus.Succeeded,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = spec.DesiredRevision,
                Message = $"Rolling deploy promoted: traffic now flows to revision '{spec.DesiredRevision}' on port {target.StandbyPort}; the old replica was drained and stopped."
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.OperationFailed(logger, operation.OperationId, spec.TargetId, ex.Message);
            return new DeployObservation
            {
                Status = WorkflowOperationStatus.Failed,
                ProviderOperationId = operation.ProviderOperationId,
                Message = "Rolling deploy promotion failed. Check the deploy controller logs for details."
            };
        }
    }

    public async Task<DeployObservation> RollbackAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var spec = operation.Deploy ?? throw new InvalidOperationException("Deploy workflow operation is missing deploy metadata.");
        cancellationToken.ThrowIfCancellationRequested();

        var target = ResolveTarget(spec);

        try
        {
            var replicas = await DiscoverReplicasAsync(operation, target, cancellationToken).ConfigureAwait(false);
            var standbyAddress = ReplicaAddress(target, target.StandbyPort);
            var activeAddress = ReplicaAddress(target, target.ActivePort);
            var alreadyPromoted = string.Equals(proxySwapper.ActiveDestinationAddress, standbyAddress, StringComparison.OrdinalIgnoreCase);

            if (!alreadyPromoted)
            {
                // Pre-cutover rollback: the old replica was never touched. Just stop the standby.
                if (replicas.Standby is { } standby)
                {
                    await containerRuntime.StopAsync(target.ContainerRuntime, standby.Name, cancellationToken).ConfigureAwait(false);
                }

                Log.RollbackRequested(logger, operation.OperationId, spec.TargetId, promoted: false);
                return new DeployObservation
                {
                    Status = WorkflowOperationStatus.RolledBack,
                    ProviderOperationId = operation.ProviderOperationId,
                    ObservedRevision = spec.CurrentRevision,
                    Message = "Rolling deploy rolled back before cutover: the standby replica was stopped and the old replica was never touched."
                };
            }

            // Post-cutover rollback: repoint the proxy at the old replica if it is still running.
            if (replicas.Active is { Running: true })
            {
                await proxySwapper.SwapAsync(activeAddress, cancellationToken).ConfigureAwait(false);
                // Stop the failed new revision after a drain window so ObserveAsync can settle the
                // rollback (it settles once the standby replica is gone). Without this the operation
                // would spin in RollbackRequested forever.
                await StopStandbyAfterDrainAsync(target, replicas.Standby, cancellationToken).ConfigureAwait(false);
                Log.RollbackRequested(logger, operation.OperationId, spec.TargetId, promoted: true);
                return new DeployObservation
                {
                    Status = WorkflowOperationStatus.RollbackRequested,
                    ProviderOperationId = operation.ProviderOperationId,
                    ObservedRevision = spec.CurrentRevision,
                    Message = "Rolling deploy rollback requested after cutover: the proxy was repointed at the previous replica and the failed revision is draining."
                };
            }

            // The old replica is gone; attempt to relaunch the previous revision if we know it.
            if (!string.IsNullOrWhiteSpace(spec.CurrentRevision))
            {
                var request = new ContainerRunRequest
                {
                    Executable = target.ContainerRuntime,
                    Image = spec.CurrentRevision!,
                    ContainerName = ActiveContainerName(target),
                    HostPort = target.ActivePort,
                    ContainerPort = target.ContainerPort,
                    Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [LabelOperation] = operation.OperationId,
                        [LabelTarget] = spec.TargetId,
                        [LabelRevision] = spec.CurrentRevision!,
                        [LabelRole] = RoleActive
                    },
                    Environment = BuildEnvironment(spec)
                };

                await containerRuntime.RunAsync(request, cancellationToken).ConfigureAwait(false);
                await proxySwapper.SwapAsync(activeAddress, cancellationToken).ConfigureAwait(false);
                await StopStandbyAfterDrainAsync(target, replicas.Standby, cancellationToken).ConfigureAwait(false);
                Log.RollbackRequested(logger, operation.OperationId, spec.TargetId, promoted: true);
                return new DeployObservation
                {
                    Status = WorkflowOperationStatus.RollbackRequested,
                    ProviderOperationId = operation.ProviderOperationId,
                    ObservedRevision = spec.CurrentRevision,
                    Message = "Rolling deploy rollback requested after cutover: relaunched the previous revision, repointed the proxy, and the failed revision is draining."
                };
            }

            Log.OperationFailed(logger, operation.OperationId, spec.TargetId, "post-cutover rollback is unrecoverable (old replica gone, no known previous revision)");
            return new DeployObservation
            {
                Status = WorkflowOperationStatus.ManualInterventionRequired,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = spec.DesiredRevision,
                Message = "Rolling deploy rollback needs operator action: the previous replica is gone and no prior revision is recorded to relaunch."
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.OperationFailed(logger, operation.OperationId, spec.TargetId, ex.Message);
            return new DeployObservation
            {
                Status = WorkflowOperationStatus.Failed,
                ProviderOperationId = operation.ProviderOperationId,
                Message = "Rolling deploy rollback failed. Check the deploy controller logs for details."
            };
        }
    }

    /// <summary>
    /// Stops the standby (new-revision) replica after the configured drain window during a
    /// post-cutover rollback. Traffic has already been repointed at the previous replica, so the
    /// drain only lets the failed revision finish in-flight requests. Best-effort by name when the
    /// label discovery did not surface the standby container.
    /// </summary>
    private async Task StopStandbyAfterDrainAsync(
        SelfHostedDeployTarget target,
        ContainerSummary? standby,
        CancellationToken cancellationToken)
    {
        if (_options.DrainDelaySeconds > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(_options.DrainDelaySeconds), cancellationToken).ConfigureAwait(false);
        }

        var name = standby?.Name ?? StandbyContainerName(target);
        await containerRuntime.StopAsync(target.ContainerRuntime, name, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LocalReplicaHealthResult> ProbeStandbyAsync(SelfHostedDeployTarget target, CancellationToken cancellationToken)
    {
        var url = ReplicaHealthUrl(target, target.StandbyPort);
        return await healthProbe.ProbeAsync(
                url,
                _options.HealthProbeSamples,
                _options.HealthProbeTimeoutSeconds,
                _options.HealthProbeExpectedStatusCode,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ReplicaSet> DiscoverReplicasAsync(
        WorkflowOperationRecord operation,
        SelfHostedDeployTarget target,
        CancellationToken cancellationToken)
    {
        // Statelessly reconstruct role from container labels — the server process could have been
        // replaced since StartAsync, so nothing in memory is trusted.
        var containers = await containerRuntime
            .ListAsync(
                target.ContainerRuntime,
                new Dictionary<string, string>(StringComparer.Ordinal) { [LabelTarget] = target.TargetId },
                cancellationToken)
            .ConfigureAwait(false);

        ContainerSummary? standby = null;
        ContainerSummary? active = null;
        foreach (var container in containers)
        {
            var role = container.Labels.GetValueOrDefault(LabelRole);
            var revision = container.Labels.GetValueOrDefault(LabelRevision);
            if (string.Equals(role, RoleStandby, StringComparison.Ordinal)
                && string.Equals(revision, operation.Deploy?.DesiredRevision, StringComparison.Ordinal))
            {
                standby = container;
            }
            else if (string.Equals(role, RoleActive, StringComparison.Ordinal))
            {
                active = container;
            }
        }

        // Fall back to the deterministic name when the active replica carries no honua role label
        // (for example an operator-managed original replica the rollout is replacing).
        active ??= containers.FirstOrDefault(c =>
            string.Equals(c.Name, ActiveContainerName(target), StringComparison.Ordinal));

        return new ReplicaSet(standby, active);
    }

    private static Dictionary<string, string> BuildEnvironment(DeployOperationSpec spec)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in spec.Parameters)
        {
            if (!entry.Key.StartsWith(SelfHostedDeployParameterKeys.EnvironmentPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var name = entry.Key[SelfHostedDeployParameterKeys.EnvironmentPrefix.Length..];
            if (!string.IsNullOrWhiteSpace(name))
            {
                environment[name] = entry.Value ?? string.Empty;
            }
        }

        return environment;
    }

    private SelfHostedDeployTarget ResolveTarget(DeployOperationSpec spec)
    {
        var parameters = spec.Parameters;
        return new SelfHostedDeployTarget
        {
            TargetId = spec.TargetId,
            Image = GetParameter(parameters, SelfHostedDeployParameterKeys.Image)
                ?? (string.IsNullOrWhiteSpace(spec.ArtifactReference) ? spec.DesiredRevision : spec.ArtifactReference),
            ContainerRuntime = string.IsNullOrWhiteSpace(_options.ContainerRuntime) ? "docker" : _options.ContainerRuntime,
            ContainerNamePrefix = string.IsNullOrWhiteSpace(_options.ContainerNamePrefix) ? "honua-app" : _options.ContainerNamePrefix,
            Host = string.IsNullOrWhiteSpace(_options.Host) ? "127.0.0.1" : _options.Host,
            HealthPath = string.IsNullOrWhiteSpace(_options.HealthPath) ? "/healthz/ready" : _options.HealthPath,
            StandbyPort = GetIntParameter(parameters, SelfHostedDeployParameterKeys.StandbyPort) ?? _options.StandbyPort,
            ActivePort = GetIntParameter(parameters, SelfHostedDeployParameterKeys.ActivePort) ?? _options.ActivePort,
            ContainerPort = GetIntParameter(parameters, SelfHostedDeployParameterKeys.ContainerPort) ?? _options.ContainerPort
        };
    }

    private static void EnsureValidTarget(SelfHostedDeployTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.Image)
            || target.StandbyPort <= 0
            || target.ActivePort <= 0
            || target.StandbyPort == target.ActivePort)
        {
            throw new InvalidOperationException("Self-hosted rolling deploy target is missing an image or a valid standby/active port pair.");
        }
    }

    private static string StandbyContainerName(SelfHostedDeployTarget target)
        => $"{target.ContainerNamePrefix}-{target.StandbyPort.ToString(CultureInfo.InvariantCulture)}";

    private static string ActiveContainerName(SelfHostedDeployTarget target)
        => $"{target.ContainerNamePrefix}-{target.ActivePort.ToString(CultureInfo.InvariantCulture)}";

    private static string ReplicaAddress(SelfHostedDeployTarget target, int port)
        => $"http://{target.Host}:{port.ToString(CultureInfo.InvariantCulture)}/";

    private static string ReplicaHealthUrl(SelfHostedDeployTarget target, int port)
    {
        var path = target.HealthPath.StartsWith('/') ? target.HealthPath : "/" + target.HealthPath;
        return $"http://{target.Host}:{port.ToString(CultureInfo.InvariantCulture)}{path}";
    }

    private static string? GetParameter(IReadOnlyDictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static int? GetIntParameter(IReadOnlyDictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var value)
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private sealed record ReplicaSet(ContainerSummary? Standby, ContainerSummary? Active);

    private sealed record SelfHostedDeployTarget
    {
        public required string TargetId { get; init; }

        public string? Image { get; init; }

        public required string ContainerRuntime { get; init; }

        public required string ContainerNamePrefix { get; init; }

        public required string Host { get; init; }

        public required string HealthPath { get; init; }

        public int StandbyPort { get; init; }

        public int ActivePort { get; init; }

        public int ContainerPort { get; init; }
    }

    private static partial class Log
    {
        [LoggerMessage(9320, LogLevel.Information, "Started standby replica for rolling deploy {OperationId} target {TargetId}: {ContainerName} ({Revision})")]
        public static partial void StandbyStarted(ILogger logger, string operationId, string targetId, string containerName, string revision);

        [LoggerMessage(9321, LogLevel.Information, "Rolling deploy {OperationId} target {TargetId} cutover completed to {DestinationAddress}")]
        public static partial void CutoverCompleted(ILogger logger, string operationId, string targetId, string destinationAddress);

        [LoggerMessage(9322, LogLevel.Information, "Rolling deploy {OperationId} target {TargetId} stopped old replica {ContainerName} after drain")]
        public static partial void OldReplicaStopped(ILogger logger, string operationId, string targetId, string containerName);

        [LoggerMessage(9323, LogLevel.Warning, "Rolling deploy {OperationId} target {TargetId} rollback requested (post-cutover={Promoted})")]
        public static partial void RollbackRequested(ILogger logger, string operationId, string targetId, bool promoted);

        [LoggerMessage(9324, LogLevel.Warning, "Rolling deploy {OperationId} target {TargetId} failed: {Reason}")]
        public static partial void OperationFailed(ILogger logger, string operationId, string targetId, string reason);
    }
}
