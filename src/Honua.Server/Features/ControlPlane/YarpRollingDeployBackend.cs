// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

internal static class SelfHostedDeployParameterKeys
{
    public const string Image = "selfhosted.image";

    public const string StandbyPort = "selfhosted.standby_port";

    public const string ActivePort = "selfhosted.active_port";

    public const string ContainerPort = "selfhosted.container_port";

    public const string EnvironmentPrefix = "env.";

    // Mounts are read-only, and the container path is the key so a Windows host path is not split on a separator.
    public const string MountPrefix = "mount.";
}

internal sealed record ContainerMount(string HostPath, string ContainerPath);

internal interface IContainerRuntimeClient
{
    Task<bool> IsAvailableAsync(string executable, CancellationToken cancellationToken);

    Task<string> RunAsync(ContainerRunRequest request, CancellationToken cancellationToken);

    Task StopAsync(string executable, string containerNameOrId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ContainerSummary>> ListAsync(
        string executable,
        IReadOnlyDictionary<string, string> labelSelectors,
        CancellationToken cancellationToken);
}

internal sealed record ContainerRunRequest
{
    public required string Executable { get; init; }

    public required string Image { get; init; }

    public required string ContainerName { get; init; }

    public required int HostPort { get; init; }

    public required int ContainerPort { get; init; }

    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyList<ContainerMount> Mounts { get; init; } = [];
}

internal sealed record ContainerSummary
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public bool Running { get; init; }
}

internal interface IProxyStateSwapper
{
    bool IsConfigured { get; }

    string? ActiveDestinationAddress { get; }

    Task SwapAsync(string destinationAddress, CancellationToken cancellationToken);
}

internal sealed record LocalReplicaHealthResult
{
    public int Attempts { get; init; }

    public int Failures { get; init; }

    public bool Healthy => Attempts > 0 && Failures == 0;

    public bool Reached { get; init; }

    public string? Body { get; init; }

    public string? Detail { get; init; }
}

// This local probe skips the outbound address guard because the target is a sibling replica, and the URL is built from validated ports.
internal interface ILocalReplicaHealthProbe
{
    Task<LocalReplicaHealthResult> ProbeAsync(
        string url,
        int samples,
        int timeoutSeconds,
        int expectedStatusCode,
        CancellationToken cancellationToken);
}

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
            SupportsRevisionPinning = true,
            // The standby replica serves no traffic until PromoteAsync swaps the proxy to it.
            StagesCandidateWithoutTraffic = true
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

        // An unsatisfiable mount blocks here rather than at submit, so the operator reads the reason
        // instead of watching the standby exit at startup and the rollout fail at the exposure
        // deadline as "never passed the backend health gate" (#4617).
        try
        {
            BuildMounts(spec);
        }
        catch (InvalidOperationException ex)
        {
            blockingReasons.Add(ex.Message);
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
                Environment = BuildEnvironment(spec),
                Mounts = BuildMounts(spec)
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
                // The standby being gone is routing convergence only. The active replica still has
                // to prove it is the prior revision, ready, and answering the functional query.
                var standbyGone = replicas.Standby is null || !replicas.Standby.Running;
                if (!standbyGone)
                {
                    return new DeployObservation
                    {
                        Status = WorkflowOperationStatus.RollbackRequested,
                        ProviderOperationId = operation.ProviderOperationId,
                        ObservedRevision = spec.CurrentRevision,
                        Message = "Rolling deploy rollback is still settling the standby replica."
                    };
                }

                return await CompleteLocalRollbackAsync(operation, spec, target, replicas.Active, cancellationToken)
                    .ConfigureAwait(false);
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

            // Retain-through-observation (honua-server#4618): the old replica keeps running after
            // cutover instead of being stopped here. Stopping it immediately would disconnect the
            // advertised rollback protection window from reality — a candidate that fails moments
            // after cutover would have nothing left to recover to. The reconciler's post-activation
            // observation window now owns retiring it, through CompleteProtectionAsync below, once the
            // window elapses without a rollback trigger. RollbackAsync repoints traffic straight back
            // to this still-running replica if a trigger fires first.
            return new DeployObservation
            {
                Status = WorkflowOperationStatus.Succeeded,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = spec.DesiredRevision,
                Message = $"Rolling deploy promoted: traffic now flows to revision '{spec.DesiredRevision}' on port {target.StandbyPort}; the old replica is retained for recovery until the post-activation observation window elapses."
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

        if (string.IsNullOrWhiteSpace(spec.CurrentRevision))
        {
            return new DeployObservation
            {
                Status = WorkflowOperationStatus.ManualInterventionRequired,
                ProviderOperationId = operation.ProviderOperationId,
                Message = "Rollback requires a previously observed revision, but none was captured for this operation."
            };
        }

        try
        {
            var replicas = await DiscoverReplicasAsync(operation, target, cancellationToken).ConfigureAwait(false);
            var standbyAddress = ReplicaAddress(target, target.StandbyPort);
            var activeAddress = ReplicaAddress(target, target.ActivePort);

            // Classify from ground truth, not only in-memory proxy state. The in-memory
            // ActiveDestinationAddress is reset to the configured active port when the front process
            // restarts, so after a promote+restart it wrongly reads "not promoted". The durable
            // operation record is the authoritative signal that survives any restart: PromoteAsync's
            // caller (the reconciler) only ever sets Deploy.Protection once cutover has actually
            // completed (honua-server#4618), so its presence proves promotion happened even though the
            // old replica is now retained (not removed) at cutover and would otherwise look
            // indistinguishable from the pre-cutover state. The container-label fallback covers
            // callers/tests that construct an operation without a durable Protection record.
            var proxyPointsAtStandby = string.Equals(
                proxySwapper.ActiveDestinationAddress, standbyAddress, StringComparison.OrdinalIgnoreCase);
            var newRevisionServing = replicas.Standby is { Running: true }
                && (replicas.Active is null || !replicas.Active.Running);
            var alreadyPromoted = proxyPointsAtStandby || spec.Protection != null || newRevisionServing;

            if (replicas.Active is { Running: true } active &&
                (!active.Labels.TryGetValue(LabelRevision, out var activeRevision) ||
                 !string.Equals(activeRevision, spec.CurrentRevision, StringComparison.Ordinal)))
            {
                return new DeployObservation
                {
                    Status = WorkflowOperationStatus.ManualInterventionRequired,
                    ProviderOperationId = operation.ProviderOperationId,
                    ObservedRevision = activeRevision,
                    Message = "Rolling deploy rollback cannot prove the running active replica is the requested prior revision."
                };
            }

            if (!alreadyPromoted)
            {
                // Pre-cutover rollback: the old replica was never touched. Stop the standby, then
                // prove the still-serving replica before the operation may terminate as RolledBack.
                if (replicas.Standby is { } standby)
                {
                    await containerRuntime.StopAsync(target.ContainerRuntime, standby.Name, cancellationToken).ConfigureAwait(false);
                }

                Log.RollbackRequested(logger, operation.OperationId, spec.TargetId, promoted: false);
                return await CompleteLocalRollbackAsync(operation, spec, target, replicas.Active, cancellationToken)
                    .ConfigureAwait(false);
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

            // Fallback path: the retained old replica is unexpectedly gone (for example an operator
            // removed it, or CompleteProtectionAsync already retired it before this trigger arrived).
            // Attempt to relaunch the previous revision if we know it, rather than leaving the target
            // fully on the failed candidate with no recovery route.
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
                    Environment = BuildEnvironment(spec),
                    Mounts = BuildMounts(spec)
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

    public async Task<DeployObservation> CompleteProtectionAsync(
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
            if (replicas.Active is { } active)
            {
                await containerRuntime.StopAsync(target.ContainerRuntime, active.Name, cancellationToken).ConfigureAwait(false);
                Log.OldReplicaStopped(logger, operation.OperationId, spec.TargetId, active.Name);
            }

            return new DeployObservation
            {
                Status = WorkflowOperationStatus.Succeeded,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = spec.DesiredRevision,
                Message = "Post-activation observation window elapsed: the retained old replica was stopped."
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.OperationFailed(logger, operation.OperationId, spec.TargetId, ex.Message);
            return new DeployObservation
            {
                Status = WorkflowOperationStatus.Failed,
                ProviderOperationId = operation.ProviderOperationId,
                Message = "Failed to retire the retained old replica after the observation window elapsed."
            };
        }
    }

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

    private async Task<DeployObservation> CompleteLocalRollbackAsync(
        WorkflowOperationRecord operation,
        DeployOperationSpec spec,
        SelfHostedDeployTarget target,
        ContainerSummary? active,
        CancellationToken cancellationToken)
    {
        string? activeRevision = null;
        var activeRunning = active is { Running: true };
        var hasRevisionLabel = activeRunning &&
            active!.Labels.TryGetValue(LabelRevision, out activeRevision) &&
            !string.IsNullOrWhiteSpace(activeRevision);
        var identityProven = hasRevisionLabel &&
            string.Equals(activeRevision, spec.CurrentRevision, StringComparison.OrdinalIgnoreCase);
        var identityMismatched = hasRevisionLabel && !identityProven;

        var readiness = activeRunning
            ? await ProbeReplicaAsync(target, target.ActivePort, cancellationToken).ConfigureAwait(false)
            : new LocalReplicaHealthResult { Attempts = 0, Failures = 0 };
        var functional = await ClassifyLocalFunctionalQueryAsync(spec, target, readiness, cancellationToken)
            .ConfigureAwait(false);
        var decision = RollbackDataPlaneCompletion.Evaluate(
            new RollbackDataPlaneEvidence
            {
                RoutingConverged = true,
                PriorRevisionIdentityProven = identityProven,
                ServingIdentityMismatched = identityMismatched,
                ServingRevision = hasRevisionLabel ? activeRevision : null,
                HealthyEndpointCount = readiness.Healthy ? 1 : 0,
                RegisteredEndpointCount = activeRunning ? 1 : 0,
                FunctionalQuery = functional,
                RollbackStartedAt = RollbackDataPlaneCompletion.ReadObservationStartedAt(spec.Parameters),
                Window = RollbackDataPlaneCompletion.ReadObservationWindow(spec.Parameters)
            },
            DateTimeOffset.UtcNow);
        return RollbackDataPlaneCompletion.ToDeployObservation(decision, operation.ProviderOperationId);
    }

    private async Task<RollbackFunctionalQueryVerdict> ClassifyLocalFunctionalQueryAsync(
        DeployOperationSpec spec,
        SelfHostedDeployTarget target,
        LocalReplicaHealthResult readiness,
        CancellationToken cancellationToken)
    {
        var expectation = RollbackDataPlaneCompletion.ReadFunctionalExpectation(spec.Parameters);
        if (!expectation.IsConfigured)
        {
            return RollbackFunctionalQueryVerdict.NotProven;
        }

        var sample = readiness;
        if (!string.IsNullOrWhiteSpace(expectation.Path))
        {
            var path = expectation.Path.StartsWith('/') ? expectation.Path : "/" + expectation.Path;
            var url = $"http://{target.Host}:{target.ActivePort.ToString(CultureInfo.InvariantCulture)}{path}";
            sample = await healthProbe.ProbeAsync(
                    url,
                    1,
                    _options.HealthProbeTimeoutSeconds,
                    expectation.ExpectedStatusCode,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!sample.Reached && sample.Body is null)
        {
            return RollbackFunctionalQueryVerdict.Unreachable;
        }

        if (!string.IsNullOrWhiteSpace(sample.Body))
        {
            var bytes = Encoding.UTF8.GetBytes(sample.Body);
            if (DeployProbeBodyInspector.DescribeErrorEnvelope(bytes) != null)
            {
                return RollbackFunctionalQueryVerdict.ServedErrorEnvelope;
            }
        }
        else if (!sample.Healthy)
        {
            return RollbackFunctionalQueryVerdict.Unreachable;
        }

        var text = sample.Body ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(expectation.ForbiddenContains) &&
            text.Contains(expectation.ForbiddenContains, StringComparison.Ordinal))
        {
            return RollbackFunctionalQueryVerdict.ServedOtherMarker;
        }

        if (!string.IsNullOrWhiteSpace(expectation.ExpectedContains) &&
            !text.Contains(expectation.ExpectedContains, StringComparison.Ordinal))
        {
            return RollbackFunctionalQueryVerdict.ServedOtherMarker;
        }

        if (!string.IsNullOrWhiteSpace(expectation.ExpectedSha256))
        {
            var actual = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            if (!string.Equals(actual, expectation.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return RollbackFunctionalQueryVerdict.ServedOtherMarker;
            }
        }

        return sample.Healthy || !string.IsNullOrWhiteSpace(sample.Body)
            ? RollbackFunctionalQueryVerdict.MatchedPriorMarker
            : RollbackFunctionalQueryVerdict.Unreachable;
    }

    private async Task<LocalReplicaHealthResult> ProbeStandbyAsync(SelfHostedDeployTarget target, CancellationToken cancellationToken)
        => await ProbeReplicaAsync(target, target.StandbyPort, cancellationToken).ConfigureAwait(false);

    private async Task<LocalReplicaHealthResult> ProbeReplicaAsync(
        SelfHostedDeployTarget target,
        int port,
        CancellationToken cancellationToken)
    {
        var url = ReplicaHealthUrl(target, port);
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

    // An unsatisfiable mount is rejected before a container exists, so a missing file is not reported as a candidate health failure.
    private static List<ContainerMount> BuildMounts(DeployOperationSpec spec)
    {
        var mounts = new List<ContainerMount>();
        foreach (var entry in spec.Parameters)
        {
            if (!entry.Key.StartsWith(SelfHostedDeployParameterKeys.MountPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var containerPath = entry.Key[SelfHostedDeployParameterKeys.MountPrefix.Length..];
            if (string.IsNullOrWhiteSpace(containerPath))
            {
                throw new InvalidOperationException(
                    $"Self-hosted rolling deploy parameter '{entry.Key}' names no container path; " +
                    $"use '{SelfHostedDeployParameterKeys.MountPrefix}<container-path>=<host-path>'.");
            }

            var hostPath = entry.Value;
            if (string.IsNullOrWhiteSpace(hostPath))
            {
                throw new InvalidOperationException(
                    $"Self-hosted rolling deploy parameter '{entry.Key}' has no host path, so '{containerPath}' " +
                    "would be empty inside the replica.");
            }

            if (!File.Exists(hostPath) && !Directory.Exists(hostPath))
            {
                throw new InvalidOperationException(
                    $"Self-hosted rolling deploy parameter '{entry.Key}' points at '{hostPath}', which does not exist " +
                    "on the container host, so the replica would start without it.");
            }

            mounts.Add(new ContainerMount(hostPath, containerPath));
        }

        return mounts;
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
