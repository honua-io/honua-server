// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.RegularExpressions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

/// <summary>
/// Product executor for a Kubernetes Deployment named by the cell binding
/// <c>honua-kubernetes-deployment</c>. An approved convergence sets one container
/// image to the immutable digest and waits until the updated replicas are ready.
/// An approved rollback is a new deploy of the prior digest. This backend does not
/// advertise in-flight rollback, because that contract is a non-digest revision
/// matrix owned by the automatic backends.
/// </summary>
internal sealed partial class KubernetesDeploymentDeployBackend(
    IKubernetesDeploymentImageClient deployments,
    ILogger<KubernetesDeploymentDeployBackend> logger) : IDeployBackend
{
    internal const string AdapterBackendName = "honua-kubernetes-deployment";
    internal const string NamespaceParameter = "kubernetes.namespace";
    internal const string DeploymentNameParameter = "kubernetes.deployment_name";
    internal const string ContainerNameParameter = "kubernetes.container_name";

    private const string SteadyAttemptsParameter = "kubernetes.steady_attempts";
    private const string SteadyDelayParameter = "kubernetes.steady_delay_ms";

    [GeneratedRegex("^[a-z0-9]([-a-z0-9]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex Dns1123Label();

    public string BackendName => AdapterBackendName;

    public DeployTargetKind TargetKind => DeployTargetKind.Kubernetes;

    public Task<DeployBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new DeployBackendCapabilities
        {
            SupportsRollback = false,
            SupportsCancellation = false,
            SupportsTrafficShifting = false,
            RequiresOutOfBandMigrations = true,
            SupportsProgressPolling = true,
            SupportsRevisionPinning = true
        });

    public Task<DeployPlan> PlanAsync(DeployOperationSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        cancellationToken.ThrowIfCancellationRequested();

        var blockingReasons = new List<string>();
        var warnings = new List<string>
        {
            "Schema migrations are not applied by this Deployment update. A reader-incompatible migration is not implied by the deploy."
        };

        if (string.IsNullOrWhiteSpace(spec.TargetId))
        {
            blockingReasons.Add("A target ID is required.");
        }

        if (!ReleaseImageDigest.IsImmutable(spec.DesiredRevision))
        {
            blockingReasons.Add(
                $"Kubernetes deployment convergence requires an immutable image digest ({ReleaseImageDigest.NotImmutableCode}).");
        }

        var target = ResolveTarget(spec);
        if (!IsDns1123(target.Namespace))
        {
            blockingReasons.Add("Kubernetes deployment convergence requires kubernetes.namespace as a DNS-1123 label.");
        }

        if (!IsDns1123(target.DeploymentName))
        {
            blockingReasons.Add("Kubernetes deployment convergence requires kubernetes.deployment_name as a DNS-1123 label.");
        }

        if (!IsDns1123(target.ContainerName))
        {
            blockingReasons.Add("Kubernetes deployment convergence requires kubernetes.container_name as a DNS-1123 label.");
        }

        return Task.FromResult(new DeployPlan
        {
            IsReadyToSubmit = blockingReasons.Count == 0 && !spec.RequiresApproval,
            RequiresApproval = spec.RequiresApproval,
            RequiresOutOfBandMigrations = true,
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
        if (!ReleaseImageDigest.IsImmutable(spec.DesiredRevision) ||
            !IsDns1123(target.Namespace) ||
            !IsDns1123(target.DeploymentName) ||
            !IsDns1123(target.ContainerName))
        {
            return Failed("Kubernetes deployment convergence refused the requested target before any API call.");
        }

        try
        {
            var current = await deployments.GetAsync(target.Namespace!, target.DeploymentName!, cancellationToken)
                .ConfigureAwait(false);
            if (current is null)
            {
                Log.DeploymentMissing(logger, operation.OperationId, spec.TargetId);
                return Failed(
                    $"Kubernetes deployment '{target.DeploymentName}' was not found in namespace '{target.Namespace}'.");
            }

            if (!current.ContainerImages.ContainsKey(target.ContainerName!))
            {
                return Failed(
                    $"Kubernetes deployment '{target.DeploymentName}' has no container named '{target.ContainerName}'. The deployment was not changed.");
            }

            var digest = spec.DesiredRevision.Trim();
            var alreadySet = current.ContainerImages.TryGetValue(target.ContainerName!, out var image) &&
                string.Equals(image, digest, StringComparison.Ordinal);
            if (!alreadySet)
            {
                await deployments.SetContainerImageAsync(
                        target.Namespace!,
                        target.DeploymentName!,
                        target.ContainerName!,
                        digest,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var priorRevision = alreadySet ? spec.CurrentRevision : image;
            var steady = await WaitUntilSteadyAsync(target, digest, priorRevision, spec.Parameters, cancellationToken)
                .ConfigureAwait(false);
            Log.OperationSubmitted(logger, operation.OperationId, spec.TargetId, target.Namespace!, target.DeploymentName!, digest);
            return steady;
        }
        catch (InvalidOperationException ex)
        {
            Log.SubmissionFailed(logger, operation.OperationId, spec.TargetId, ex.Message);
            return Failed(StableKubernetesFailure(ex));
        }
        catch (HttpRequestException ex)
        {
            Log.SubmissionFailed(logger, operation.OperationId, spec.TargetId, ex.Message);
            return Failed("Kubernetes deployment request failed.");
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
        if (!IsDns1123(target.Namespace) || !IsDns1123(target.DeploymentName) || !IsDns1123(target.ContainerName))
        {
            return new DeployObservation
            {
                Status = WorkflowOperationStatus.Failed,
                ProviderOperationId = operation.ProviderOperationId,
                Message = "Kubernetes deployment convergence refused the requested target before any API call."
            };
        }

        try
        {
            var current = await deployments.GetAsync(target.Namespace!, target.DeploymentName!, cancellationToken)
                .ConfigureAwait(false);
            if (current is null)
            {
                return new DeployObservation
                {
                    Status = WorkflowOperationStatus.Failed,
                    ProviderOperationId = operation.ProviderOperationId,
                    Message = $"Kubernetes deployment '{target.DeploymentName}' was not found in namespace '{target.Namespace}'."
                };
            }

            var digest = spec.DesiredRevision.Trim();
            if (IsSteady(current, target.ContainerName!, digest))
            {
                return new DeployObservation
                {
                    Status = WorkflowOperationStatus.Succeeded,
                    ProviderOperationId = ProviderId(target),
                    ObservedRevision = digest,
                    Message = $"Kubernetes deployment '{target.DeploymentName}' is ready on image digest '{digest}'."
                };
            }

            return new DeployObservation
            {
                Status = WorkflowOperationStatus.Reconciling,
                ProviderOperationId = ProviderId(target),
                ObservedRevision = current.ContainerImages.TryGetValue(target.ContainerName!, out var image) ? image : null,
                Message = $"Kubernetes deployment '{target.DeploymentName}' is still converging on image digest '{digest}'."
            };
        }
        catch (InvalidOperationException ex)
        {
            Log.SubmissionFailed(logger, operation.OperationId, spec.TargetId, ex.Message);
            return new DeployObservation
            {
                Status = WorkflowOperationStatus.Failed,
                ProviderOperationId = operation.ProviderOperationId,
                Message = StableKubernetesFailure(ex)
            };
        }
        catch (HttpRequestException ex)
        {
            Log.SubmissionFailed(logger, operation.OperationId, spec.TargetId, ex.Message);
            return new DeployObservation
            {
                Status = WorkflowOperationStatus.Failed,
                ProviderOperationId = operation.ProviderOperationId,
                Message = "Kubernetes deployment request failed."
            };
        }
    }

    public Task<DeployObservation> PromoteAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new DeployObservation
        {
            Status = WorkflowOperationStatus.ManualInterventionRequired,
            ProviderOperationId = operation.ProviderOperationId,
            Message = "honua-kubernetes-deployment applies the image directly and has no separate promotion step."
        });
    }

    public Task<DeployObservation> RollbackAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var spec = operation.Deploy ?? throw new InvalidOperationException("Deploy workflow operation is missing deploy metadata.");
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(spec.CurrentRevision))
        {
            return Task.FromResult(new DeployObservation
            {
                Status = WorkflowOperationStatus.ManualInterventionRequired,
                ProviderOperationId = operation.ProviderOperationId,
                Message = "Rollback requires a prior product revision, but none was captured for this operation."
            });
        }

        return Task.FromResult(new DeployObservation
        {
            Status = WorkflowOperationStatus.ManualInterventionRequired,
            ProviderOperationId = operation.ProviderOperationId,
            ObservedRevision = spec.CurrentRevision,
            Message = "An in-flight Kubernetes rollback is not a completed rollout. Approve a deploy of the prior image digest."
        });
    }

    private async Task<DeploySubmissionResult> WaitUntilSteadyAsync(
        DeploymentTarget target,
        string digest,
        string? priorRevision,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        var attempts = ReadBoundedInt(parameters, SteadyAttemptsParameter, 15, 60);
        var delayMs = ReadBoundedInt(parameters, SteadyDelayParameter, 2000, 30_000);
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (attempt > 0 && delayMs > 0)
            {
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }

            var current = await deployments.GetAsync(target.Namespace!, target.DeploymentName!, cancellationToken)
                .ConfigureAwait(false);
            if (current is not null && IsSteady(current, target.ContainerName!, digest))
            {
                return new DeploySubmissionResult
                {
                    Status = WorkflowOperationStatus.Submitted,
                    ProviderOperationId = ProviderId(target),
                    ObservedRevision = priorRevision,
                    Message = $"Kubernetes deployment '{target.DeploymentName}' is ready on image digest '{digest}'."
                };
            }
        }

        return new DeploySubmissionResult
        {
            Status = WorkflowOperationStatus.Submitted,
            ProviderOperationId = ProviderId(target),
            ObservedRevision = priorRevision,
            Message = $"Kubernetes deployment '{target.DeploymentName}' is still converging on image digest '{digest}'."
        };
    }

    private static bool IsSteady(KubernetesDeploymentImageState state, string containerName, string digest)
        => state.Generation > 0 &&
           state.ObservedGeneration == state.Generation &&
           state.Replicas > 0 &&
           state.TotalReplicas == state.Replicas &&
           state.ReadyReplicas == state.Replicas &&
           state.UpdatedReplicas == state.Replicas &&
           state.ContainerImages.TryGetValue(containerName, out var image) &&
           string.Equals(image, digest, StringComparison.Ordinal);

    private static string StableKubernetesFailure(InvalidOperationException ex)
        => ex.Message.Contains("API server", StringComparison.Ordinal)
            ? "Kubernetes control plane is not configured for this deployment."
            : ex.Message.Contains("could not be read", StringComparison.Ordinal)
                ? "Kubernetes deployment response could not be read."
                : ex.Message.Contains("was not found", StringComparison.Ordinal)
                    ? "Kubernetes deployment was not found."
                    : "Kubernetes deployment request failed.";

    private static DeploySubmissionResult Failed(string message)
        => new()
        {
            Status = WorkflowOperationStatus.Failed,
            Message = message
        };

    private static string ProviderId(DeploymentTarget target) => $"{target.Namespace}/{target.DeploymentName}";

    private static DeploymentTarget ResolveTarget(DeployOperationSpec spec)
        => new(
            Read(spec.Parameters, NamespaceParameter),
            Read(spec.Parameters, DeploymentNameParameter) ?? spec.TargetName,
            Read(spec.Parameters, ContainerNameParameter));

    private static string? Read(IReadOnlyDictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static bool IsDns1123(string? value)
        => !string.IsNullOrWhiteSpace(value) && Dns1123Label().IsMatch(value);

    private static int ReadBoundedInt(IReadOnlyDictionary<string, string> parameters, string key, int defaultValue, int max)
    {
        var raw = Read(parameters, key);
        if (raw is null || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return defaultValue;
        }

        if (parsed < 1)
        {
            return 1;
        }

        return Math.Min(parsed, max);
    }

    private sealed record DeploymentTarget(string? Namespace, string? DeploymentName, string? ContainerName);

    private static partial class Log
    {
        [LoggerMessage(9111, LogLevel.Information, "Submitted Kubernetes deployment {OperationId} for target {TargetId} ({Namespace}/{DeploymentName}) to image {Digest}")]
        public static partial void OperationSubmitted(
            ILogger logger,
            string operationId,
            string targetId,
            string @namespace,
            string deploymentName,
            string digest);

        [LoggerMessage(9112, LogLevel.Warning, "Kubernetes deployment was missing for workflow operation {OperationId} targeting {TargetId}")]
        public static partial void DeploymentMissing(ILogger logger, string operationId, string targetId);

        [LoggerMessage(9113, LogLevel.Warning, "Kubernetes deployment submission failed for workflow operation {OperationId} targeting {TargetId}: {ErrorMessage}")]
        public static partial void SubmissionFailed(ILogger logger, string operationId, string targetId, string errorMessage);
    }
}
