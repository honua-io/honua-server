// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

/// <summary>
/// Built-in GitOps deploy backend for Kubernetes targets managed by Honua.
/// </summary>
internal sealed class KubernetesGitOpsDeployBackend(ILogger<KubernetesGitOpsDeployBackend> logger)
    : GitOpsDeployBackendBase(logger)
{
    public override string BackendName => "honua-gitops-kubernetes";

    public override DeployTargetKind TargetKind => DeployTargetKind.Kubernetes;
}

/// <summary>
/// Built-in GitOps deploy backend for AWS ECS targets managed by Honua.
/// </summary>
internal sealed class AwsEcsGitOpsDeployBackend(ILogger<AwsEcsGitOpsDeployBackend> logger)
    : GitOpsDeployBackendBase(logger)
{
    public override string BackendName => "honua-gitops-aws-ecs";

    public override DeployTargetKind TargetKind => DeployTargetKind.AwsEcs;
}

/// <summary>
/// Built-in GitOps deploy backend for Azure Container Apps targets managed by Honua.
/// </summary>
internal sealed class AzureContainerAppsGitOpsDeployBackend(ILogger<AzureContainerAppsGitOpsDeployBackend> logger)
    : GitOpsDeployBackendBase(logger)
{
    public override string BackendName => "honua-gitops-azure-container-apps";

    public override DeployTargetKind TargetKind => DeployTargetKind.AzureContainerApps;
}

#if !HONUA_EXCLUDE_AZURE
/// <summary>
/// Direct Azure Container Apps revision traffic backend that manages revision traffic splitting
/// through the ARM REST API without requiring an external GitOps controller.
/// </summary>
internal sealed partial class AzureContainerAppsRevisionDeployBackend(
    IAzureContainerAppsRevisionClient revisionClient,
    ILogger<AzureContainerAppsRevisionDeployBackend> logger) : IDeployBackend
{
    public string BackendName => "honua-azure-container-apps-revision";

    public DeployTargetKind TargetKind => DeployTargetKind.AzureContainerApps;

    public Task<DeployBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new DeployBackendCapabilities
        {
            SupportsRollback = true,
            SupportsCancellation = false,
            SupportsTrafficShifting = true,
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

        if (string.IsNullOrWhiteSpace(target.SubscriptionId))
        {
            blockingReasons.Add("Azure Container Apps deploy workflows require a subscription id or target.resource_id.");
        }

        if (string.IsNullOrWhiteSpace(target.ResourceGroupName))
        {
            blockingReasons.Add("Azure Container Apps deploy workflows require azure.resource_group or target.resource_id.");
        }

        if (string.IsNullOrWhiteSpace(target.AppName))
        {
            blockingReasons.Add("Azure Container Apps deploy workflows require the container app name.");
        }

        if (string.IsNullOrWhiteSpace(spec.DesiredRevision))
        {
            blockingReasons.Add("A desired revision name is required.");
        }

        if (string.IsNullOrWhiteSpace(spec.ArtifactReference))
        {
            warnings.Add("No artifact reference is configured for this target.");
        }

        if (!TryResolveCanaryWeightPercentage(spec.Parameters, out var canaryWeight, out var canaryError))
        {
            blockingReasons.Add(canaryError ?? "Azure Container Apps canary weight is invalid.");
        }
        else if (canaryWeight.HasValue &&
            (!spec.Parameters.TryGetValue("telemetry.connection", out var telemetryConnection) ||
             string.IsNullOrWhiteSpace(telemetryConnection)))
        {
            blockingReasons.Add("Azure Container Apps canary traffic shifting requires telemetry.connection so the rollout can be promoted or rolled back automatically.");
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
        _ = TryResolveCanaryWeightPercentage(spec.Parameters, out var canaryWeight, out _);

        var trafficState = await revisionClient.GetTrafficStateAsync(
                target.SubscriptionId!,
                target.ResourceGroupName!,
                target.AppName!,
                cancellationToken)
            .ConfigureAwait(false);

        var currentPrimaryRevision = trafficState.Traffic.Count > 0
            ? trafficState.Traffic.OrderByDescending(static t => t.Weight).First().RevisionName
            : null;

        if (canaryWeight.HasValue &&
            !string.IsNullOrWhiteSpace(currentPrimaryRevision) &&
            !string.Equals(currentPrimaryRevision, spec.DesiredRevision, StringComparison.OrdinalIgnoreCase))
        {
            var desiredRevision = trafficState.Revisions
                .FirstOrDefault(r => string.Equals(r.RevisionName, spec.DesiredRevision, StringComparison.OrdinalIgnoreCase));
            if (desiredRevision is { Active: false })
            {
                await revisionClient.ActivateRevisionAsync(
                        target.SubscriptionId!,
                        target.ResourceGroupName!,
                        target.AppName!,
                        spec.DesiredRevision,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var stableWeight = 100 - canaryWeight.Value;
            await revisionClient.UpdateTrafficAsync(
                    target.SubscriptionId!,
                    target.ResourceGroupName!,
                    target.AppName!,
                    [
                        new AzureContainerAppsTrafficWeight { RevisionName = currentPrimaryRevision, Weight = stableWeight },
                        new AzureContainerAppsTrafficWeight { RevisionName = spec.DesiredRevision, Weight = canaryWeight.Value }
                    ],
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await revisionClient.UpdateTrafficAsync(
                    target.SubscriptionId!,
                    target.ResourceGroupName!,
                    target.AppName!,
                    [new AzureContainerAppsTrafficWeight { RevisionName = spec.DesiredRevision, Weight = 100 }],
                    cancellationToken)
                .ConfigureAwait(false);
        }

        Log.OperationSubmitted(logger, operation.OperationId, spec.TargetId, target.AppName!, spec.DesiredRevision);
        return new DeploySubmissionResult
        {
            Status = WorkflowOperationStatus.Submitted,
            ProviderOperationId = $"{target.AppName}:{spec.DesiredRevision}",
            ObservedRevision = currentPrimaryRevision,
            Message = canaryWeight.HasValue && !string.IsNullOrWhiteSpace(currentPrimaryRevision) && !string.Equals(currentPrimaryRevision, spec.DesiredRevision, StringComparison.OrdinalIgnoreCase)
                ? $"Container App '{target.AppName}' is routing {canaryWeight.Value}% of traffic to revision '{spec.DesiredRevision}'."
                : $"Container App '{target.AppName}' is moving 100% of traffic to revision '{spec.DesiredRevision}'."
        };
    }

    public async Task<DeployObservation> ObserveAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var spec = operation.Deploy ?? throw new InvalidOperationException("Deploy workflow operation is missing deploy metadata.");
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var target = ResolveTarget(spec);
            EnsureValidTarget(target);
            _ = TryResolveCanaryWeightPercentage(spec.Parameters, out var desiredCanaryWeight, out _);

            var trafficState = await revisionClient.GetTrafficStateAsync(
                    target.SubscriptionId!,
                    target.ResourceGroupName!,
                    target.AppName!,
                    cancellationToken)
                .ConfigureAwait(false);

            var desiredTraffic = trafficState.Traffic
                .FirstOrDefault(t => string.Equals(t.RevisionName, spec.DesiredRevision, StringComparison.OrdinalIgnoreCase));
            var currentPrimaryRevision = trafficState.Traffic.Count > 0
                ? trafficState.Traffic.OrderByDescending(static t => t.Weight).First().RevisionName
                : null;

            if (operation.Status == WorkflowOperationStatus.RollbackRequested)
            {
                var rollbackRevision = spec.CurrentRevision;
                if (!string.IsNullOrWhiteSpace(rollbackRevision))
                {
                    var rollbackTraffic = trafficState.Traffic
                        .FirstOrDefault(t => string.Equals(t.RevisionName, rollbackRevision, StringComparison.OrdinalIgnoreCase));
                    if (rollbackTraffic is { Weight: 100 })
                    {
                        return new DeployObservation
                        {
                            Status = WorkflowOperationStatus.RolledBack,
                            ProviderOperationId = operation.ProviderOperationId,
                            ObservedRevision = rollbackRevision,
                            Message = $"Container App '{target.AppName}' traffic is fully restored to revision '{rollbackRevision}'."
                        };
                    }
                }

                return new DeployObservation
                {
                    Status = WorkflowOperationStatus.RollbackRequested,
                    ProviderOperationId = operation.ProviderOperationId,
                    ObservedRevision = currentPrimaryRevision,
                    Message = $"Container App '{target.AppName}' is still converging on rollback revision '{rollbackRevision ?? "unknown"}'."
                };
            }

            if (desiredTraffic is { Weight: 100 })
            {
                return new DeployObservation
                {
                    Status = WorkflowOperationStatus.Succeeded,
                    ProviderOperationId = operation.ProviderOperationId,
                    ObservedRevision = spec.DesiredRevision,
                    Message = $"Container App '{target.AppName}' has 100% of traffic on revision '{spec.DesiredRevision}'."
                };
            }

            if (desiredCanaryWeight.HasValue &&
                desiredTraffic != null &&
                desiredTraffic.Weight == desiredCanaryWeight.Value)
            {
                return new DeployObservation
                {
                    Status = WorkflowOperationStatus.Reconciling,
                    ProviderOperationId = operation.ProviderOperationId,
                    ObservedRevision = currentPrimaryRevision,
                    PromotionRecommended = true,
                    Message = $"Container App '{target.AppName}' has canary revision '{spec.DesiredRevision}' receiving {desiredTraffic.Weight}% of traffic."
                };
            }

            return new DeployObservation
            {
                Status = WorkflowOperationStatus.Reconciling,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = currentPrimaryRevision,
                Message = $"Container App '{target.AppName}' is still converging to revision '{spec.DesiredRevision}'."
            };
        }
        catch (HttpRequestException ex)
        {
            Log.StateLookupFailed(logger, operation.OperationId, spec.TargetId, ex.Message);
            return new DeployObservation
            {
                Status = WorkflowOperationStatus.Failed,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.ObservedState,
                Message = ex.Message
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

        await revisionClient.UpdateTrafficAsync(
                target.SubscriptionId!,
                target.ResourceGroupName!,
                target.AppName!,
                [new AzureContainerAppsTrafficWeight { RevisionName = spec.DesiredRevision, Weight = 100 }],
                cancellationToken)
            .ConfigureAwait(false);

        Log.PromotionCompleted(logger, operation.OperationId, spec.TargetId, target.AppName!, spec.DesiredRevision);
        return new DeployObservation
        {
            Status = WorkflowOperationStatus.Succeeded,
            ProviderOperationId = operation.ProviderOperationId,
            ObservedRevision = spec.DesiredRevision,
            Message = $"Container App '{target.AppName}' has been promoted to 100% traffic on revision '{spec.DesiredRevision}'."
        };
    }

    public async Task<DeployObservation> RollbackAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var spec = operation.Deploy ?? throw new InvalidOperationException("Deploy workflow operation is missing deploy metadata.");
        cancellationToken.ThrowIfCancellationRequested();

        var rollbackRevision = spec.CurrentRevision;
        if (string.IsNullOrWhiteSpace(rollbackRevision))
        {
            return new DeployObservation
            {
                Status = WorkflowOperationStatus.ManualInterventionRequired,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.ObservedState,
                Message = "Rollback requires a previously observed revision, but none was captured for this operation."
            };
        }

        var target = ResolveTarget(spec);
        EnsureValidTarget(target);

        await revisionClient.UpdateTrafficAsync(
                target.SubscriptionId!,
                target.ResourceGroupName!,
                target.AppName!,
                [new AzureContainerAppsTrafficWeight { RevisionName = rollbackRevision, Weight = 100 }],
                cancellationToken)
            .ConfigureAwait(false);

        Log.RollbackRequested(logger, operation.OperationId, spec.TargetId, target.AppName!, rollbackRevision);
        return new DeployObservation
        {
            Status = WorkflowOperationStatus.RollbackRequested,
            ProviderOperationId = operation.ProviderOperationId,
            ObservedRevision = rollbackRevision,
            Message = $"Container App '{target.AppName}' is moving 100% of traffic back to revision '{rollbackRevision}'."
        };
    }

    private static AzureContainerAppsDeployTarget ResolveTarget(DeployOperationSpec spec)
    {
        var parameters = spec.Parameters;
        var resourceId = GetParameter(parameters, "target.resource_id");
        var parsedResource = AzureResourceIdParser.Parse(resourceId);

        return new AzureContainerAppsDeployTarget
        {
            SubscriptionId = GetParameter(parameters, "azure.subscription_id")
                ?? parsedResource.SubscriptionId,
            ResourceGroupName = GetParameter(parameters, "azure.resource_group")
                ?? parsedResource.ResourceGroupName,
            AppName = GetParameter(parameters, "azure.containerapp.app_name")
                ?? GetParameter(parameters, "containerapp.app_name")
                ?? spec.TargetName
        };
    }

    private static void EnsureValidTarget(AzureContainerAppsDeployTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.SubscriptionId) ||
            string.IsNullOrWhiteSpace(target.ResourceGroupName) ||
            string.IsNullOrWhiteSpace(target.AppName))
        {
            throw new InvalidOperationException("Azure Container Apps deploy target is missing subscription, resource group, or app name metadata.");
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
        var rawValue = GetParameter(parameters, "azure.containerapp.canary_weight_percentage")
            ?? GetParameter(parameters, "containerapp.canary_weight_percentage")
            ?? GetParameter(parameters, "deployment.canary_weight_percentage");
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            weight = null;
            error = null;
            return true;
        }

        if (!int.TryParse(rawValue, out var parsedPercentage))
        {
            weight = null;
            error = "Azure Container Apps canary weight must be an integer percentage between 1 and 99.";
            return false;
        }

        if (parsedPercentage <= 0 || parsedPercentage >= 100)
        {
            weight = null;
            error = "Azure Container Apps canary weight must be greater than 0 and less than 100 percent.";
            return false;
        }

        weight = parsedPercentage;
        error = null;
        return true;
    }

    private sealed record AzureContainerAppsDeployTarget
    {
        public string? SubscriptionId { get; init; }

        public string? ResourceGroupName { get; init; }

        public string? AppName { get; init; }
    }

    private static partial class Log
    {
        [LoggerMessage(9036, LogLevel.Information, "Submitted Azure Container Apps deploy workflow operation {OperationId} for target {TargetId} ({AppName}) to revision {DesiredRevision}")]
        public static partial void OperationSubmitted(ILogger logger, string operationId, string targetId, string appName, string desiredRevision);

        [LoggerMessage(9037, LogLevel.Warning, "Rollback requested for Azure Container Apps workflow operation {OperationId} targeting {TargetId} ({AppName}) to revision {RollbackRevision}")]
        public static partial void RollbackRequested(ILogger logger, string operationId, string targetId, string appName, string rollbackRevision);

        [LoggerMessage(9038, LogLevel.Information, "Promotion completed for Azure Container Apps workflow operation {OperationId} targeting {TargetId} ({AppName}) to revision {DesiredRevision}")]
        public static partial void PromotionCompleted(ILogger logger, string operationId, string targetId, string appName, string desiredRevision);

        [LoggerMessage(9039, LogLevel.Warning, "Azure Container Apps state lookup failed for workflow operation {OperationId} targeting {TargetId}: {ErrorMessage}")]
        public static partial void StateLookupFailed(ILogger logger, string operationId, string targetId, string errorMessage);
    }
}
#endif

#if !HONUA_EXCLUDE_AZURE
/// <summary>
/// Built-in GitOps deploy backend for Azure Functions targets managed by Honua.
/// </summary>
internal sealed partial class AzureFunctionsGitOpsDeployBackend(
    IAzureFunctionsSlotClient slotClient,
    ILogger<AzureFunctionsGitOpsDeployBackend> logger) : IDeployBackend
{
    public string BackendName => "honua-gitops-azure-functions";

    public DeployTargetKind TargetKind => DeployTargetKind.AzureFunctions;

    public Task<DeployBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new DeployBackendCapabilities
        {
            SupportsRollback = true,
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
        var warnings = new List<string>();
        var target = ResolveTarget(spec);

        if (string.IsNullOrWhiteSpace(spec.TargetId))
        {
            blockingReasons.Add("A target ID is required.");
        }

        if (string.IsNullOrWhiteSpace(target.SubscriptionId))
        {
            blockingReasons.Add("Azure Functions deploy workflows require a subscription id or target.resource_id.");
        }

        if (string.IsNullOrWhiteSpace(target.ResourceGroupName))
        {
            blockingReasons.Add("Azure Functions deploy workflows require azure.resource_group or target.resource_id.");
        }

        if (string.IsNullOrWhiteSpace(target.FunctionAppName))
        {
            blockingReasons.Add("Azure Functions deploy workflows require the function app name.");
        }

        if (string.IsNullOrWhiteSpace(target.SlotName))
        {
            blockingReasons.Add("Azure Functions deploy workflows require desiredRevision to name the staging deployment slot.");
        }
        else if (string.Equals(target.SlotName, "production", StringComparison.OrdinalIgnoreCase))
        {
            blockingReasons.Add("Azure Functions deploy workflows require desiredRevision to be a non-production deployment slot.");
        }

        if (!string.Equals(target.CurrentRevision, "production", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("Azure Functions slot rollouts currently assume 'production' is the live slot.");
        }

        if (string.IsNullOrWhiteSpace(target.CurrentImage))
        {
            blockingReasons.Add("Azure Functions deploy workflows require functions.current_image metadata from Terraform.");
        }

        if (string.IsNullOrWhiteSpace(target.DesiredImage))
        {
            blockingReasons.Add("Azure Functions deploy workflows require functions.desired_image metadata from Terraform.");
        }

        if (string.IsNullOrWhiteSpace(spec.ArtifactReference))
        {
            warnings.Add("No artifact reference is configured for this target.");
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

        var productionState = await slotClient.GetSiteConfigAsync(
                target.SubscriptionId!,
                target.ResourceGroupName!,
                target.FunctionAppName!,
                null,
                cancellationToken)
            .ConfigureAwait(false);
        var slotState = await slotClient.GetSiteConfigAsync(
                target.SubscriptionId!,
                target.ResourceGroupName!,
                target.FunctionAppName!,
                target.SlotName!,
                cancellationToken)
            .ConfigureAwait(false);

        var productionImage = NormalizeLinuxFxVersion(productionState.LinuxFxVersion);
        var slotImage = NormalizeLinuxFxVersion(slotState.LinuxFxVersion);
        if (ImagesMatch(productionImage, target.DesiredImage))
        {
            return new DeploySubmissionResult
            {
                Status = WorkflowOperationStatus.Submitted,
                ProviderOperationId = $"{target.FunctionAppName}:{target.SlotName}",
                ObservedRevision = productionImage,
                Message = $"Azure Functions production is already serving staged slot '{target.SlotName}'."
            };
        }

        if (!ImagesMatch(productionImage, target.CurrentImage))
        {
            throw new InvalidOperationException(
                $"Azure Functions production is serving '{productionImage ?? "unknown"}' instead of expected image '{target.CurrentImage}'.");
        }

        if (!ImagesMatch(slotImage, target.DesiredImage))
        {
            throw new InvalidOperationException(
                $"Azure Functions slot '{target.SlotName}' is serving '{slotImage ?? "unknown"}' instead of expected image '{target.DesiredImage}'.");
        }

        var swapResult = await slotClient.SwapSlotWithProductionAsync(
                target.SubscriptionId!,
                target.ResourceGroupName!,
                target.FunctionAppName!,
                target.SlotName!,
                target.PreserveVnet,
                cancellationToken)
            .ConfigureAwait(false);

        Log.OperationSubmitted(logger, operation.OperationId, spec.TargetId, target.FunctionAppName!, target.SlotName!);
        return new DeploySubmissionResult
        {
            Status = WorkflowOperationStatus.Submitted,
            ProviderOperationId = swapResult.OperationLocation ?? $"{target.FunctionAppName}:{target.SlotName}",
            ObservedRevision = productionImage,
            Message = $"Azure Functions slot '{target.SlotName}' is swapping with production."
        };
    }

    public async Task<DeployObservation> ObserveAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var spec = operation.Deploy ?? throw new InvalidOperationException("Deploy workflow operation is missing deploy metadata.");
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var target = ResolveTarget(spec);
            EnsureValidTarget(target);

            var productionState = await slotClient.GetSiteConfigAsync(
                    target.SubscriptionId!,
                    target.ResourceGroupName!,
                    target.FunctionAppName!,
                    null,
                    cancellationToken)
                .ConfigureAwait(false);
            var slotState = await slotClient.GetSiteConfigAsync(
                    target.SubscriptionId!,
                    target.ResourceGroupName!,
                    target.FunctionAppName!,
                    target.SlotName!,
                    cancellationToken)
                .ConfigureAwait(false);

            var productionImage = NormalizeLinuxFxVersion(productionState.LinuxFxVersion);
            var slotImage = NormalizeLinuxFxVersion(slotState.LinuxFxVersion);
            var observedRevision = productionImage ?? operation.ObservedState;

            if (operation.Status == WorkflowOperationStatus.RollbackRequested)
            {
                if (ImagesMatch(productionImage, target.CurrentImage))
                {
                    return new DeployObservation
                    {
                        Status = WorkflowOperationStatus.RolledBack,
                        ProviderOperationId = operation.ProviderOperationId,
                        ObservedRevision = observedRevision,
                        Message = $"Azure Functions production is back on image '{target.CurrentImage}'."
                    };
                }

                return new DeployObservation
                {
                    Status = WorkflowOperationStatus.RollbackRequested,
                    ProviderOperationId = operation.ProviderOperationId,
                    ObservedRevision = observedRevision,
                    Message = $"Azure Functions rollback is still converging. Production image is '{productionImage ?? "unknown"}'; slot '{target.SlotName}' image is '{slotImage ?? "unknown"}'."
                };
            }

            if (ImagesMatch(productionImage, target.DesiredImage))
            {
                return new DeployObservation
                {
                    Status = WorkflowOperationStatus.Succeeded,
                    ProviderOperationId = operation.ProviderOperationId,
                    ObservedRevision = observedRevision,
                    Message = $"Azure Functions production is serving staged slot '{target.SlotName}' image '{target.DesiredImage}'."
                };
            }

            return new DeployObservation
            {
                Status = WorkflowOperationStatus.Reconciling,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = observedRevision,
                Message = $"Azure Functions swap is still converging. Production image is '{productionImage ?? "unknown"}'; slot '{target.SlotName}' image is '{slotImage ?? "unknown"}'."
            };
        }
        catch (HttpRequestException ex)
        {
            Log.StateLookupFailed(logger, operation.OperationId, spec.TargetId, ex.Message);
            return new DeployObservation
            {
                Status = WorkflowOperationStatus.Failed,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.ObservedState,
                Message = ex.Message
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

        if (string.IsNullOrWhiteSpace(target.CurrentImage))
        {
            return new DeployObservation
            {
                Status = WorkflowOperationStatus.ManualInterventionRequired,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.ObservedState,
                Message = "Azure Functions rollback requires functions.current_image metadata."
            };
        }

        var productionState = await slotClient.GetSiteConfigAsync(
                target.SubscriptionId!,
                target.ResourceGroupName!,
                target.FunctionAppName!,
                null,
                cancellationToken)
            .ConfigureAwait(false);
        var productionImage = NormalizeLinuxFxVersion(productionState.LinuxFxVersion);

        if (ImagesMatch(productionImage, target.CurrentImage))
        {
            return new DeployObservation
            {
                Status = WorkflowOperationStatus.RollbackRequested,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = productionImage,
                Message = $"Azure Functions production is already serving rollback image '{target.CurrentImage}'."
            };
        }

        var swapResult = await slotClient.SwapSlotWithProductionAsync(
                target.SubscriptionId!,
                target.ResourceGroupName!,
                target.FunctionAppName!,
                target.SlotName!,
                target.PreserveVnet,
                cancellationToken)
            .ConfigureAwait(false);

        Log.RollbackRequested(logger, operation.OperationId, spec.TargetId, target.FunctionAppName!, target.SlotName!);
        return new DeployObservation
        {
            Status = WorkflowOperationStatus.RollbackRequested,
            ProviderOperationId = swapResult.OperationLocation ?? operation.ProviderOperationId ?? $"{target.FunctionAppName}:{target.SlotName}",
            ObservedRevision = productionImage,
            Message = $"Azure Functions slot '{target.SlotName}' is swapping back with production."
        };
    }

    private static AzureFunctionsDeployTarget ResolveTarget(DeployOperationSpec spec)
    {
        var parameters = spec.Parameters;
        var resourceId = GetParameter(parameters, "target.resource_id");
        var parsedResource = AzureResourceIdParser.Parse(resourceId);

        return new AzureFunctionsDeployTarget
        {
            SubscriptionId = GetParameter(parameters, "azure.subscription_id")
                ?? parsedResource.SubscriptionId,
            ResourceGroupName = GetParameter(parameters, "azure.resource_group")
                ?? parsedResource.ResourceGroupName,
            FunctionAppName = GetParameter(parameters, "azure.functions.app_name")
                ?? GetParameter(parameters, "functions.app_name")
                ?? spec.TargetName,
            SlotName = spec.DesiredRevision
                ?? GetParameter(parameters, "azure.functions.slot_name")
                ?? GetParameter(parameters, "functions.slot_name"),
            CurrentRevision = spec.CurrentRevision
                ?? GetParameter(parameters, "deployment.current_revision")
                ?? "production",
            CurrentImage = GetParameter(parameters, "azure.functions.current_image")
                ?? GetParameter(parameters, "functions.current_image"),
            DesiredImage = GetParameter(parameters, "azure.functions.desired_image")
                ?? GetParameter(parameters, "functions.desired_image"),
            PreserveVnet = TryParseBoolean(
                GetParameter(parameters, "azure.functions.preserve_vnet")
                ?? GetParameter(parameters, "functions.preserve_vnet"),
                defaultValue: true)
        };
    }

    private static void EnsureValidTarget(AzureFunctionsDeployTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.SubscriptionId) ||
            string.IsNullOrWhiteSpace(target.ResourceGroupName) ||
            string.IsNullOrWhiteSpace(target.FunctionAppName) ||
            string.IsNullOrWhiteSpace(target.SlotName))
        {
            throw new InvalidOperationException("Azure Functions deploy target is missing subscription, resource group, app name, or slot name metadata.");
        }
    }

    private static string? NormalizeLinuxFxVersion(string? linuxFxVersion)
    {
        if (string.IsNullOrWhiteSpace(linuxFxVersion))
        {
            return null;
        }

        const string dockerPrefix = "DOCKER|";
        var normalized = linuxFxVersion.Trim();
        return normalized.StartsWith(dockerPrefix, StringComparison.OrdinalIgnoreCase)
            ? normalized[dockerPrefix.Length..]
            : normalized;
    }

    private static bool ImagesMatch(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left) &&
           !string.IsNullOrWhiteSpace(right) &&
           string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? GetParameter(IReadOnlyDictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static bool TryParseBoolean(string? value, bool defaultValue)
        => bool.TryParse(value, out var parsed)
            ? parsed
            : defaultValue;

    private sealed record AzureFunctionsDeployTarget
    {
        public string? SubscriptionId { get; init; }

        public string? ResourceGroupName { get; init; }

        public string? FunctionAppName { get; init; }

        public string? SlotName { get; init; }

        public string CurrentRevision { get; init; } = "production";

        public string? CurrentImage { get; init; }

        public string? DesiredImage { get; init; }

        public bool PreserveVnet { get; init; } = true;
    }

    private static partial class Log
    {
        [LoggerMessage(9033, LogLevel.Information, "Submitted Azure Functions deploy workflow operation {OperationId} for target {TargetId} ({FunctionAppName}:{SlotName})")]
        public static partial void OperationSubmitted(ILogger logger, string operationId, string targetId, string functionAppName, string slotName);

        [LoggerMessage(9034, LogLevel.Warning, "Rollback requested for Azure Functions workflow operation {OperationId} targeting {TargetId} ({FunctionAppName}:{SlotName})")]
        public static partial void RollbackRequested(ILogger logger, string operationId, string targetId, string functionAppName, string slotName);

        [LoggerMessage(9035, LogLevel.Warning, "Azure Functions state lookup failed for workflow operation {OperationId} targeting {TargetId}: {ErrorMessage}")]
        public static partial void StateLookupFailed(ILogger logger, string operationId, string targetId, string errorMessage);
    }
}
#endif

/// <summary>
/// Shared Azure resource ID parser for ARM-based deploy backends.
/// </summary>
internal static class AzureResourceIdParser
{
    /// <summary>
    /// Extracts subscription ID and resource group name from an ARM resource ID path.
    /// </summary>
    public static (string? SubscriptionId, string? ResourceGroupName) Parse(string? resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            return (null, null);
        }

        var segments = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? subscriptionId = null;
        string? resourceGroupName = null;

        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (segments[index].Equals("subscriptions", StringComparison.OrdinalIgnoreCase))
            {
                subscriptionId = segments[index + 1];
            }
            else if (segments[index].Equals("resourceGroups", StringComparison.OrdinalIgnoreCase))
            {
                resourceGroupName = segments[index + 1];
            }
        }

        return (subscriptionId, resourceGroupName);
    }
}

internal abstract partial class GitOpsDeployBackendBase(ILogger logger) : IDeployBackend
{
    public abstract string BackendName { get; }

    public abstract DeployTargetKind TargetKind { get; }

    public Task<DeployBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new DeployBackendCapabilities
        {
            SupportsRollback = true,
            SupportsCancellation = false,
            SupportsTrafficShifting = TargetKind is DeployTargetKind.AwsEcs or DeployTargetKind.AzureContainerApps or DeployTargetKind.AwsLambda or DeployTargetKind.AzureFunctions,
            RequiresOutOfBandMigrations = TargetKind is DeployTargetKind.AwsLambda or DeployTargetKind.AzureFunctions,
            SupportsProgressPolling = false,
            SupportsRevisionPinning = true
        });

    public async Task<DeployPlan> PlanAsync(DeployOperationSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        cancellationToken.ThrowIfCancellationRequested();

        var blockingReasons = new List<string>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(spec.TargetId))
        {
            blockingReasons.Add("A target ID is required.");
        }

        if (string.IsNullOrWhiteSpace(spec.DesiredRevision))
        {
            blockingReasons.Add("A desired revision is required.");
        }

        if (string.IsNullOrWhiteSpace(spec.ArtifactReference))
        {
            warnings.Add("No artifact reference is configured for this target.");
        }

        var capabilities = await GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        if (spec.RequiresOutOfBandMigrations && !capabilities.RequiresOutOfBandMigrations)
        {
            warnings.Add("This target requires out-of-band migrations even though the backend does not require them globally.");
        }

        return new DeployPlan
        {
            IsReadyToSubmit = blockingReasons.Count == 0 && !spec.RequiresApproval,
            RequiresApproval = spec.RequiresApproval,
            RequiresOutOfBandMigrations = spec.RequiresOutOfBandMigrations,
            BlockingReasons = blockingReasons,
            Warnings = warnings
        };
    }

    public Task<DeploySubmissionResult> StartAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();

        Log.OperationSubmitted(logger, operation.OperationId, operation.Deploy?.TargetId ?? operation.OperationId);
        return Task.FromResult(new DeploySubmissionResult
        {
            Status = WorkflowOperationStatus.Submitted,
            ProviderOperationId = $"{BackendName}:{operation.OperationId}",
            Message = "Queued for Honua GitOps reconciliation."
        });
    }

    public Task<DeployObservation> ObserveAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(new DeployObservation
        {
            Status = WorkflowOperationStatus.ManualInterventionRequired,
            ProviderOperationId = operation.ProviderOperationId,
            ObservedRevision = operation.Status == WorkflowOperationStatus.RollbackRequested
                ? operation.Deploy?.CurrentRevision
                : operation.Deploy?.DesiredRevision,
            Message = operation.Status == WorkflowOperationStatus.RollbackRequested
                ? "Rollback was handed off to the external GitOps controller. Confirm rollback completion out of band."
                : "Deployment was handed off to the external GitOps controller. Confirm rollout completion out of band."
        });
    }

    public Task<DeployObservation> RollbackAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();

        Log.RollbackRequested(logger, operation.OperationId, operation.Deploy?.TargetId ?? operation.OperationId);
        return Task.FromResult(new DeployObservation
        {
            Status = WorkflowOperationStatus.RollbackRequested,
            ProviderOperationId = operation.ProviderOperationId ?? $"{BackendName}:{operation.OperationId}",
            ObservedRevision = operation.Deploy?.CurrentRevision,
            Message = "Rollback requested through Honua GitOps reconciliation."
        });
    }

    private static partial class Log
    {
        [LoggerMessage(9020, LogLevel.Information, "Submitted deploy workflow operation {OperationId} for target {TargetId}")]
        public static partial void OperationSubmitted(ILogger logger, string operationId, string targetId);

        [LoggerMessage(9021, LogLevel.Warning, "Rollback requested for workflow operation {OperationId} targeting {TargetId}")]
        public static partial void RollbackRequested(ILogger logger, string operationId, string targetId);
    }
}
