// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ServiceDefaults;

namespace Honua.ControlPlane;

/// <summary>
/// Closed-loop Kubernetes deploy backend that drives a weighted canary rollout and
/// automatic rollback through an external <a href="https://argo-rollouts.readthedocs.io/">Argo
/// Rollouts</a> controller. Honua mutates the Rollout custom resource (set the canary
/// image to start, promote to advance a paused step, abort to roll back) and reflects the
/// controller-reported rollout status back into the <see cref="WorkflowOperationRecord"/>
/// lifecycle so <see cref="ObserveAsync"/>, <see cref="PromoteAsync"/>, and
/// <see cref="RollbackAsync"/> drive real state transitions rather than
/// <see cref="WorkflowOperationStatus.ManualInterventionRequired"/>.
///
/// The progressive canary stepping itself (5→25→50→100, pause/analysis) is owned by the
/// Argo Rollouts <c>spec.strategy.canary.steps</c> on the cluster; Honua observes the
/// step weight and gates promotion/rollback through the shared telemetry-gated
/// reconciler. Coexists with the GitOps passthrough backend
/// <c>honua-gitops-kubernetes</c> under <see cref="DeployTargetKind.Kubernetes"/>;
/// targets pick by <see cref="DeployTargetDefinition.Backend"/> name.
/// </summary>
internal sealed partial class KubernetesArgoRolloutsDeployBackend(
    IArgoRolloutsClient rolloutsClient,
    ILogger<KubernetesArgoRolloutsDeployBackend> logger) : IDeployBackend
{
    internal const string AdapterBackendName = "honua-kubernetes-argo-rollouts";

    /// <summary>Parameter key for the Rollout namespace.</summary>
    internal const string NamespaceParameter = "kubernetes.namespace";

    /// <summary>Parameter key for the Argo Rollout resource name.</summary>
    internal const string RolloutNameParameter = "kubernetes.argo.rollout_name";

    /// <summary>Parameter key for the container whose image the rollout shifts.</summary>
    internal const string ContainerNameParameter = "kubernetes.argo.container_name";

    public string BackendName => AdapterBackendName;

    public DeployTargetKind TargetKind => DeployTargetKind.Kubernetes;

    public Task<DeployBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new DeployBackendCapabilities
        {
            SupportsRollback = true,
            SupportsCancellation = false,
            SupportsTrafficShifting = true,
            // Kubernetes schema migrations are managed by Honua's migration runner on
            // boot, but image-level rollouts do not run them inside the deploy workflow.
            RequiresOutOfBandMigrations = true,
            SupportsProgressPolling = true,
            SupportsRevisionPinning = true
        });

    public Task<DeployPlan> PlanAsync(DeployOperationSpec spec, CancellationToken cancellationToken = default)
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

        if (string.IsNullOrWhiteSpace(spec.DesiredRevision))
        {
            blockingReasons.Add("A desired image reference is required for Kubernetes Argo Rollouts deploy workflows.");
        }

        if (string.IsNullOrWhiteSpace(target.Namespace))
        {
            blockingReasons.Add("Kubernetes Argo Rollouts deploy workflows require kubernetes.namespace (or in-cluster namespace auto-detection).");
        }

        if (string.IsNullOrWhiteSpace(target.RolloutName))
        {
            blockingReasons.Add("Kubernetes Argo Rollouts deploy workflows require kubernetes.argo.rollout_name (or targetName).");
        }

        if (string.IsNullOrWhiteSpace(target.ContainerName))
        {
            blockingReasons.Add("Kubernetes Argo Rollouts deploy workflows require kubernetes.argo.container_name to identify the container image to shift.");
        }

        if (string.IsNullOrWhiteSpace(spec.ArtifactReference))
        {
            warnings.Add("No artifact reference is configured for this target.");
        }

        if (!TryResolveCanaryWeightPercentage(spec.Parameters, out var canaryWeight, out var canaryError))
        {
            blockingReasons.Add(canaryError ?? "Kubernetes Argo Rollouts canary weight is invalid.");
        }
        else if (canaryWeight.HasValue &&
            (!spec.Parameters.TryGetValue("telemetry.connection", out var telemetryConnection) ||
             string.IsNullOrWhiteSpace(telemetryConnection)))
        {
            blockingReasons.Add("Kubernetes Argo Rollouts canary traffic shifting requires telemetry.connection so the rollout can be promoted or rolled back automatically.");
        }

        return Task.FromResult(new DeployPlan
        {
            IsReadyToSubmit = blockingReasons.Count == 0 && !spec.RequiresApproval,
            RequiresApproval = spec.RequiresApproval,
            RequiresOutOfBandMigrations = spec.RequiresOutOfBandMigrations,
            BlockingReasons = blockingReasons,
            Warnings = warnings
        });
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

        using var activity = StartActivity(ControlPlaneTelemetry.Activities.BackendStart, operation, target);

        try
        {
            // Capture the currently deployed image so rollback (abort) has a known
            // baseline and the reconciler can persist CurrentRevision.
            var existing = await rolloutsClient.GetRolloutAsync(target.Namespace!, target.RolloutName!, cancellationToken)
                .ConfigureAwait(false);
            if (existing == null)
            {
                Log.RolloutNotFound(logger, operation.OperationId, spec.TargetId, target.Namespace!, target.RolloutName!);
                return new DeploySubmissionResult
                {
                    Status = WorkflowOperationStatus.Failed,
                    Message = $"Argo Rollout '{target.RolloutName}' was not found in namespace '{target.Namespace}'. Provision the Rollout resource before submitting a deploy."
                };
            }

            var observedImage = existing.PodTemplateImage;

            // Setting the image triggers the Argo Rollouts controller to begin the
            // configured canary steps. The controller owns the stepped ramp; Honua
            // observes and gates.
            await rolloutsClient.SetImageAsync(
                    target.Namespace!,
                    target.RolloutName!,
                    target.ContainerName!,
                    spec.DesiredRevision,
                    cancellationToken)
                .ConfigureAwait(false);

            Log.OperationSubmitted(logger, operation.OperationId, spec.TargetId, target.Namespace!, target.RolloutName!, spec.DesiredRevision);
            return new DeploySubmissionResult
            {
                Status = WorkflowOperationStatus.Submitted,
                ProviderOperationId = $"{target.Namespace}/{target.RolloutName}",
                ObservedRevision = observedImage,
                Message = $"Argo Rollout '{target.RolloutName}' is rolling out image '{spec.DesiredRevision}'."
            };
        }
        catch (HttpRequestException ex)
        {
            Log.SubmissionFailed(logger, operation.OperationId, spec.TargetId, ex.Message);
            activity?.SetStatus(ActivityStatusCode.Error);
            return new DeploySubmissionResult
            {
                Status = WorkflowOperationStatus.Failed,
                Message = SanitizedFailureMessage
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

        using var activity = StartActivity(ControlPlaneTelemetry.Activities.BackendObserve, operation, target);

        try
        {
            EnsureValidTarget(target);
            _ = TryResolveCanaryWeightPercentage(spec.Parameters, out var desiredCanaryWeight, out _);

            var rollout = await rolloutsClient.GetRolloutAsync(target.Namespace!, target.RolloutName!, cancellationToken)
                .ConfigureAwait(false);
            if (rollout == null)
            {
                return new DeployObservation
                {
                    Status = WorkflowOperationStatus.Failed,
                    ProviderOperationId = operation.ProviderOperationId,
                    ObservedRevision = operation.ObservedState,
                    Message = $"Argo Rollout '{target.RolloutName}' was not found in namespace '{target.Namespace}'."
                };
            }

            Log.StateObserved(logger, target.RolloutName!, rollout.Phase.ToString(), rollout.CanaryWeight ?? -1, rollout.IsPaused);
            var observedImage = rollout.PodTemplateImage ?? operation.ObservedState;

            if (operation.Status == WorkflowOperationStatus.RollbackRequested)
            {
                return ObserveRollback(operation, spec, target, rollout, observedImage);
            }

            // A degraded rollout (failed analysis, progress-deadline exceeded, or an
            // operator/controller abort) is the auto-rollback trigger. Surface
            // RollbackRecommended so the shared reconciler drives RollbackAsync rather
            // than leaving the operation stalled.
            if (rollout.Phase == ArgoRolloutPhase.Degraded || rollout.IsAborted)
            {
                return new DeployObservation
                {
                    Status = WorkflowOperationStatus.Reconciling,
                    ProviderOperationId = operation.ProviderOperationId,
                    ObservedRevision = observedImage,
                    RollbackRecommended = true,
                    Message = rollout.Message is { Length: > 0 } degradedMessage
                        ? $"Argo Rollout '{target.RolloutName}' is degraded: {degradedMessage}."
                        : $"Argo Rollout '{target.RolloutName}' is degraded and is a rollback candidate."
                };
            }

            // The desired revision is fully promoted once the rollout is Healthy and the
            // desired pod hash has become the stable revision. Argo sets currentPodHash
            // == stableRS only after the final step completes.
            if (rollout.Phase == ArgoRolloutPhase.Healthy && IsFullyPromoted(rollout))
            {
                return new DeployObservation
                {
                    Status = WorkflowOperationStatus.Succeeded,
                    ProviderOperationId = operation.ProviderOperationId,
                    ObservedRevision = observedImage,
                    Message = $"Argo Rollout '{target.RolloutName}' is healthy and fully promoted to image '{spec.DesiredRevision}'."
                };
            }

            // Paused at a canary step that has reached the configured target weight is the
            // promotion-ready signal. The shared reconciler runs the telemetry gate and,
            // on pass, calls PromoteAsync to advance the rollout.
            if (rollout.Phase == ArgoRolloutPhase.Paused &&
                rollout.IsPaused &&
                IsAtExpectedCanaryWeight(rollout, desiredCanaryWeight))
            {
                return new DeployObservation
                {
                    Status = WorkflowOperationStatus.Reconciling,
                    ProviderOperationId = operation.ProviderOperationId,
                    ObservedRevision = observedImage,
                    PromotionRecommended = true,
                    Message = desiredCanaryWeight.HasValue
                        ? $"Argo Rollout '{target.RolloutName}' is paused with {rollout.CanaryWeight ?? desiredCanaryWeight.Value}% canary weight and is ready for promotion."
                        : $"Argo Rollout '{target.RolloutName}' is paused at a canary step and is ready for promotion."
                };
            }

            return new DeployObservation
            {
                Status = WorkflowOperationStatus.Reconciling,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = observedImage,
                Message = $"Argo Rollout '{target.RolloutName}' is progressing (phase={rollout.Phase}, canaryWeight={rollout.CanaryWeight?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}, paused={rollout.IsPaused})."
            };
        }
        catch (HttpRequestException ex)
        {
            Log.StateLookupFailed(logger, operation.OperationId, spec.TargetId, ex.Message);
            activity?.SetStatus(ActivityStatusCode.Error);
            return new DeployObservation
            {
                Status = WorkflowOperationStatus.Failed,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.ObservedState,
                Message = SanitizedFailureMessage
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

        using var activity = StartActivity(ControlPlaneTelemetry.Activities.BackendObserve, operation, target);

        try
        {
            var rollout = await rolloutsClient.PromoteAsync(target.Namespace!, target.RolloutName!, cancellationToken)
                .ConfigureAwait(false);

            Log.PromotionRequested(logger, operation.OperationId, spec.TargetId, target.Namespace!, target.RolloutName!);

            // Promotion advances the rollout to the next canary step (or 100% at the final
            // step). The terminal Succeeded transition is recognised by a later ObserveAsync
            // once the controller reports Healthy with the desired pod hash stable, so a
            // promote that has not yet fully converged stays Reconciling.
            if (rollout.Phase == ArgoRolloutPhase.Healthy && IsFullyPromoted(rollout))
            {
                return new DeployObservation
                {
                    Status = WorkflowOperationStatus.Succeeded,
                    ProviderOperationId = operation.ProviderOperationId,
                    ObservedRevision = rollout.PodTemplateImage ?? spec.DesiredRevision,
                    Message = $"Argo Rollout '{target.RolloutName}' promoted and is healthy on image '{spec.DesiredRevision}'."
                };
            }

            return new DeployObservation
            {
                Status = WorkflowOperationStatus.Reconciling,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = rollout.PodTemplateImage ?? operation.ObservedState,
                Message = $"Argo Rollout '{target.RolloutName}' promotion requested; controller is advancing to the next step."
            };
        }
        catch (HttpRequestException ex)
        {
            Log.StateLookupFailed(logger, operation.OperationId, spec.TargetId, ex.Message);
            activity?.SetStatus(ActivityStatusCode.Error);
            return new DeployObservation
            {
                Status = WorkflowOperationStatus.Failed,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.ObservedState,
                Message = SanitizedFailureMessage
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
        EnsureValidTarget(target);

        using var activity = StartActivity(ControlPlaneTelemetry.Activities.BackendRollback, operation, target);

        try
        {
            await rolloutsClient.AbortAsync(target.Namespace!, target.RolloutName!, cancellationToken)
                .ConfigureAwait(false);

            Log.RollbackRequested(logger, operation.OperationId, spec.TargetId, target.Namespace!, target.RolloutName!);
            return new DeployObservation
            {
                Status = WorkflowOperationStatus.RollbackRequested,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = spec.CurrentRevision ?? operation.ObservedState,
                Message = $"Argo Rollout '{target.RolloutName}' abort requested; the controller is reverting traffic to the stable revision."
            };
        }
        catch (HttpRequestException ex)
        {
            Log.StateLookupFailed(logger, operation.OperationId, spec.TargetId, ex.Message);
            activity?.SetStatus(ActivityStatusCode.Error);
            return new DeployObservation
            {
                Status = WorkflowOperationStatus.Failed,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.ObservedState,
                Message = SanitizedFailureMessage
            };
        }
    }

    private static DeployObservation ObserveRollback(
        WorkflowOperationRecord operation,
        DeployOperationSpec spec,
        ArgoRolloutsDeployTarget target,
        ArgoRolloutState rollout,
        string? observedImage)
    {
        // An aborted rollout is terminal as RolledBack once Argo has reverted to the
        // stable revision: the rollout is Healthy again and the current pod hash has
        // settled back onto the stable ReplicaSet. While the controller is still draining
        // the canary the operation stays RollbackRequested.
        var revertedToStable = rollout.Phase == ArgoRolloutPhase.Healthy && IsFullyPromoted(rollout);
        if (revertedToStable)
        {
            return new DeployObservation
            {
                Status = WorkflowOperationStatus.RolledBack,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = spec.CurrentRevision ?? observedImage,
                Message = $"Argo Rollout '{target.RolloutName}' rolled back to the stable revision."
            };
        }

        return new DeployObservation
        {
            Status = WorkflowOperationStatus.RollbackRequested,
            ProviderOperationId = operation.ProviderOperationId,
            ObservedRevision = observedImage,
            Message = $"Argo Rollout '{target.RolloutName}' rollback is still settling (phase={rollout.Phase}, aborted={rollout.IsAborted})."
        };
    }

    // A rollout is fully promoted when the desired (current) pod hash has become the
    // stable ReplicaSet. Argo only sets stableRS == currentPodHash after the final canary
    // step completes, so this distinguishes a Healthy-but-mid-rollout state (current !=
    // stable) from full convergence. When the controller does not report either hash,
    // treat Healthy as converged so older Argo versions still terminate the operation.
    private static bool IsFullyPromoted(ArgoRolloutState rollout)
    {
        if (string.IsNullOrWhiteSpace(rollout.CurrentPodHash) || string.IsNullOrWhiteSpace(rollout.StableRevisionHash))
        {
            return true;
        }

        return string.Equals(rollout.CurrentPodHash, rollout.StableRevisionHash, StringComparison.Ordinal);
    }

    // When a canary weight is configured the rollout must be holding exactly that share
    // before promotion is recommended; otherwise any paused canary step is treated as
    // promotion-ready (immediate-cutover rollouts with a single manual pause).
    private static bool IsAtExpectedCanaryWeight(ArgoRolloutState rollout, int? desiredCanaryWeight)
    {
        if (!desiredCanaryWeight.HasValue)
        {
            return true;
        }

        return rollout.CanaryWeight == desiredCanaryWeight.Value;
    }

    private static ArgoRolloutsDeployTarget ResolveTarget(DeployOperationSpec spec)
    {
        var parameters = spec.Parameters;
        return new ArgoRolloutsDeployTarget
        {
            Namespace = GetParameter(parameters, NamespaceParameter)
                ?? KubernetesJobClient.TryReadInClusterNamespace(),
            RolloutName = GetParameter(parameters, RolloutNameParameter) ?? spec.TargetName,
            ContainerName = GetParameter(parameters, ContainerNameParameter)
        };
    }

    private static void EnsureValidTarget(ArgoRolloutsDeployTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.Namespace) ||
            string.IsNullOrWhiteSpace(target.RolloutName) ||
            string.IsNullOrWhiteSpace(target.ContainerName))
        {
            throw new InvalidOperationException("Kubernetes Argo Rollouts deploy target is missing namespace, rollout name, or container name metadata.");
        }
    }

    private static string? GetParameter(IReadOnlyDictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static bool TryResolveCanaryWeightPercentage(
        IReadOnlyDictionary<string, string> parameters,
        out int? weight,
        out string? error)
    {
        var rawValue = GetParameter(parameters, "kubernetes.argo.canary_weight_percentage")
            ?? GetParameter(parameters, "deployment.canary_weight_percentage");
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            weight = null;
            error = null;
            return true;
        }

        if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPercentage))
        {
            weight = null;
            error = "Kubernetes Argo Rollouts canary weight must be an integer percentage between 1 and 99.";
            return false;
        }

        if (parsedPercentage <= 0 || parsedPercentage >= 100)
        {
            weight = null;
            error = "Kubernetes Argo Rollouts canary weight must be greater than 0 and less than 100 percent.";
            return false;
        }

        weight = parsedPercentage;
        error = null;
        return true;
    }

    private static Activity? StartActivity(string activityName, WorkflowOperationRecord operation, ArgoRolloutsDeployTarget target)
    {
        var activity = HonuaTelemetry.ActivitySource.StartActivity(activityName, ActivityKind.Internal);
        if (activity == null)
        {
            return null;
        }

        activity.SetTag(HonuaTelemetry.Tags.Operation, AdapterBackendName);
        activity.SetTag(ControlPlaneTelemetry.Tags.Backend, AdapterBackendName);
        activity.SetTag(ControlPlaneTelemetry.Tags.TargetKind, DeployTargetKind.Kubernetes.ToString());
        activity.SetTag("honua.controlplane.operation_id", operation.OperationId);
        if (operation.Deploy is { } deploy)
        {
            activity.SetTag("honua.controlplane.target_id", deploy.TargetId);
        }

        if (!string.IsNullOrWhiteSpace(target.Namespace))
        {
            activity.SetTag("honua.controlplane.kubernetes.namespace", target.Namespace);
        }

        if (!string.IsNullOrWhiteSpace(target.RolloutName))
        {
            activity.SetTag("honua.controlplane.kubernetes.argo.rollout", target.RolloutName);
        }

        return activity;
    }

    // Provider error text from the Kubernetes/Argo API can carry resource names,
    // namespaces, and request detail. The raw error already went to the structured log;
    // the durable operation record gets a stable, generic message.
    private const string SanitizedFailureMessage =
        "Argo Rollouts state lookup failed for this rollout. Check the deploy controller logs for the underlying Kubernetes error.";

    private sealed record ArgoRolloutsDeployTarget
    {
        public string? Namespace { get; init; }

        public string? RolloutName { get; init; }

        public string? ContainerName { get; init; }
    }

    private static partial class Log
    {
        [LoggerMessage(9101, LogLevel.Information, "Submitted Argo Rollouts deploy workflow operation {OperationId} for target {TargetId} ({Namespace}/{RolloutName}) to image {DesiredImage}")]
        public static partial void OperationSubmitted(ILogger logger, string operationId, string targetId, string @namespace, string rolloutName, string desiredImage);

        [LoggerMessage(9102, LogLevel.Information, "Promotion requested for Argo Rollouts workflow operation {OperationId} targeting {TargetId} ({Namespace}/{RolloutName})")]
        public static partial void PromotionRequested(ILogger logger, string operationId, string targetId, string @namespace, string rolloutName);

        [LoggerMessage(9103, LogLevel.Warning, "Rollback (abort) requested for Argo Rollouts workflow operation {OperationId} targeting {TargetId} ({Namespace}/{RolloutName})")]
        public static partial void RollbackRequested(ILogger logger, string operationId, string targetId, string @namespace, string rolloutName);

        [LoggerMessage(9104, LogLevel.Warning, "Argo Rollouts state lookup failed for workflow operation {OperationId} targeting {TargetId}: {ErrorMessage}")]
        public static partial void StateLookupFailed(ILogger logger, string operationId, string targetId, string errorMessage);

        [LoggerMessage(9105, LogLevel.Debug, "Observed Argo Rollout {RolloutName}: phase={Phase} canaryWeight={CanaryWeight} paused={Paused}")]
        public static partial void StateObserved(ILogger logger, string rolloutName, string phase, int canaryWeight, bool paused);

        [LoggerMessage(9106, LogLevel.Warning, "Argo Rollouts submission failed for workflow operation {OperationId} targeting {TargetId}: {ErrorMessage}")]
        public static partial void SubmissionFailed(ILogger logger, string operationId, string targetId, string errorMessage);

        [LoggerMessage(9107, LogLevel.Warning, "Argo Rollout {RolloutName} not found in namespace {Namespace} for workflow operation {OperationId} targeting {TargetId}")]
        public static partial void RolloutNotFound(ILogger logger, string operationId, string targetId, string @namespace, string rolloutName);
    }
}
