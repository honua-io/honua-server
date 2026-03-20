// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.RegularExpressions;
using Amazon.Lambda.Model;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Infrastructure.ControlPlane;

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

/// <summary>
/// Built-in GitOps deploy backend for AWS Lambda targets managed by Honua.
/// </summary>
internal sealed partial class AwsLambdaGitOpsDeployBackend(
    IAwsLambdaAliasClient aliasClient,
    ILogger<AwsLambdaGitOpsDeployBackend> logger) : IDeployBackend
{
    private static readonly Regex LambdaArnRegionPattern = new("^arn:(aws[a-zA-Z-]*)?:lambda:(?<region>[^:]+):", RegexOptions.Compiled);

    public string BackendName => "honua-gitops-aws-lambda";

    public DeployTargetKind TargetKind => DeployTargetKind.AwsLambda;

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
        else if (string.Equals(spec.DesiredRevision, "$LATEST", StringComparison.Ordinal))
        {
            blockingReasons.Add("AWS Lambda deploy workflows require a published function version, not $LATEST.");
        }
        else if (!long.TryParse(spec.DesiredRevision, out var desiredVersion) || desiredVersion <= 0)
        {
            blockingReasons.Add("AWS Lambda deploy workflows require desiredRevision to be a published numeric version.");
        }

        if (string.IsNullOrWhiteSpace(ResolveFunctionName(spec)))
        {
            blockingReasons.Add("A Lambda function name is required for this deploy target.");
        }

        if (string.IsNullOrWhiteSpace(ResolveAliasName(spec.Parameters)))
        {
            blockingReasons.Add("A Lambda alias name is required for this deploy target.");
        }

        if (string.IsNullOrWhiteSpace(spec.ArtifactReference))
        {
            warnings.Add("No artifact reference is configured for this target.");
        }

        if (!TryResolveCanaryWeightFraction(spec.Parameters, out var canaryWeightFraction, out var canaryWeightError))
        {
            blockingReasons.Add(canaryWeightError ?? "AWS Lambda canary weight is invalid.");
        }
        else if (canaryWeightFraction.HasValue &&
            (!spec.Parameters.TryGetValue("telemetry.connection", out var telemetryConnection) ||
             string.IsNullOrWhiteSpace(telemetryConnection)))
        {
            blockingReasons.Add("AWS Lambda canary traffic shifting requires telemetry.connection so the rollout can be promoted or rolled back automatically.");
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

    public async Task<DeploySubmissionResult> StartAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var spec = operation.Deploy ?? throw new InvalidOperationException("Deploy workflow operation is missing deploy metadata.");
        cancellationToken.ThrowIfCancellationRequested();

        var functionName = ResolveFunctionName(spec);
        var aliasName = ResolveAliasName(spec.Parameters);
        var region = ResolveRegion(spec.Parameters);
        var aliasState = await aliasClient.GetAliasAsync(functionName, aliasName, region, cancellationToken).ConfigureAwait(false);
        _ = TryResolveCanaryWeightFraction(spec.Parameters, out var canaryWeightFraction, out _);
        var currentStableVersion = aliasState.FunctionVersion;

        if (canaryWeightFraction.HasValue &&
            !string.IsNullOrWhiteSpace(currentStableVersion) &&
            !string.Equals(currentStableVersion, spec.DesiredRevision, StringComparison.Ordinal))
        {
            await aliasClient.UpdateAliasAsync(
                    functionName,
                    aliasName,
                    currentStableVersion,
                    new Dictionary<string, double>(StringComparer.Ordinal)
                    {
                        [spec.DesiredRevision] = canaryWeightFraction.Value
                    },
                    region,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else if (!string.Equals(aliasState.FunctionVersion, spec.DesiredRevision, StringComparison.Ordinal) ||
                 aliasState.AdditionalVersionWeights.Count > 0)
        {
            await aliasClient.UpdateAliasAsync(
                    functionName,
                    aliasName,
                    spec.DesiredRevision,
                    null,
                    region,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        Log.OperationSubmitted(logger, operation.OperationId, spec.TargetId, functionName, aliasName, spec.DesiredRevision);
        return new DeploySubmissionResult
        {
            Status = WorkflowOperationStatus.Submitted,
            ProviderOperationId = aliasState.AliasArn ?? $"{functionName}:{aliasName}",
            ObservedRevision = aliasState.FunctionVersion,
            Message = canaryWeightFraction.HasValue && !string.IsNullOrWhiteSpace(currentStableVersion) && !string.Equals(currentStableVersion, spec.DesiredRevision, StringComparison.Ordinal)
                ? $"Lambda alias '{aliasName}' is routing {Math.Round(canaryWeightFraction.Value * 100, 3):0.###}% of traffic to published version '{spec.DesiredRevision}'."
                : $"Lambda alias '{aliasName}' is moving to published version '{spec.DesiredRevision}'."
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
            var functionName = ResolveFunctionName(spec);
            var aliasName = ResolveAliasName(spec.Parameters);
            var region = ResolveRegion(spec.Parameters);
            var aliasState = await aliasClient.GetAliasAsync(functionName, aliasName, region, cancellationToken).ConfigureAwait(false);
            var hasWeightedTraffic = aliasState.AdditionalVersionWeights.Count > 0;
            _ = TryResolveCanaryWeightFraction(spec.Parameters, out var desiredWeightFraction, out _);
            var routesDesiredRevision = aliasState.AdditionalVersionWeights.TryGetValue(spec.DesiredRevision, out var currentCanaryWeight);

            if (operation.Status == WorkflowOperationStatus.RollbackRequested)
            {
                var rollbackVersion = spec.CurrentRevision;
                if (!string.IsNullOrWhiteSpace(rollbackVersion) &&
                    string.Equals(aliasState.FunctionVersion, rollbackVersion, StringComparison.Ordinal) &&
                    !hasWeightedTraffic)
                {
                    return new DeployObservation
                    {
                        Status = WorkflowOperationStatus.RolledBack,
                        ProviderOperationId = aliasState.AliasArn ?? operation.ProviderOperationId,
                        ObservedRevision = aliasState.FunctionVersion,
                        Message = $"Lambda alias '{aliasName}' now points to rollback version '{rollbackVersion}'."
                    };
                }

                return new DeployObservation
                {
                    Status = WorkflowOperationStatus.RollbackRequested,
                    ProviderOperationId = aliasState.AliasArn ?? operation.ProviderOperationId,
                    ObservedRevision = aliasState.FunctionVersion,
                    Message = $"Lambda alias '{aliasName}' is still converging on rollback version '{rollbackVersion ?? "unknown"}'."
                };
            }

            if (string.Equals(aliasState.FunctionVersion, spec.DesiredRevision, StringComparison.Ordinal) && !hasWeightedTraffic)
            {
                return new DeployObservation
                {
                    Status = WorkflowOperationStatus.Succeeded,
                    ProviderOperationId = aliasState.AliasArn ?? operation.ProviderOperationId,
                    ObservedRevision = aliasState.FunctionVersion,
                    Message = $"Lambda alias '{aliasName}' now points to published version '{spec.DesiredRevision}'."
                };
            }

            if (desiredWeightFraction.HasValue &&
                !string.IsNullOrWhiteSpace(spec.CurrentRevision) &&
                string.Equals(aliasState.FunctionVersion, spec.CurrentRevision, StringComparison.Ordinal) &&
                routesDesiredRevision &&
                Math.Abs(currentCanaryWeight - desiredWeightFraction.Value) <= 0.0001d)
            {
                return new DeployObservation
                {
                    Status = WorkflowOperationStatus.Reconciling,
                    ProviderOperationId = aliasState.AliasArn ?? operation.ProviderOperationId,
                    ObservedRevision = aliasState.FunctionVersion,
                    PromotionRecommended = true,
                    Message = $"Lambda alias '{aliasName}' is holding stable version '{spec.CurrentRevision}' while routing {Math.Round(currentCanaryWeight * 100, 3):0.###}% of traffic to canary version '{spec.DesiredRevision}'."
                };
            }

            return new DeployObservation
            {
                Status = WorkflowOperationStatus.Reconciling,
                ProviderOperationId = aliasState.AliasArn ?? operation.ProviderOperationId,
                ObservedRevision = aliasState.FunctionVersion,
                Message = hasWeightedTraffic
                    ? $"Lambda alias '{aliasName}' still has weighted traffic configured while converging to version '{spec.DesiredRevision}'."
                    : $"Lambda alias '{aliasName}' is still converging to version '{spec.DesiredRevision}'."
            };
        }
        catch (ResourceNotFoundException ex)
        {
            Log.AliasLookupFailed(logger, operation.OperationId, spec.TargetId, ex.Message);
            return new DeployObservation
            {
                Status = WorkflowOperationStatus.Failed,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = spec.CurrentRevision,
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

        var functionName = ResolveFunctionName(spec);
        var aliasName = ResolveAliasName(spec.Parameters);
        var region = ResolveRegion(spec.Parameters);
        var updatedAlias = await aliasClient.UpdateAliasAsync(
                functionName,
                aliasName,
                spec.DesiredRevision,
                null,
                region,
                cancellationToken)
            .ConfigureAwait(false);

        var hasWeightedTraffic = updatedAlias.AdditionalVersionWeights.Count > 0;
        return new DeployObservation
        {
            Status = string.Equals(updatedAlias.FunctionVersion, spec.DesiredRevision, StringComparison.Ordinal) && !hasWeightedTraffic
                ? WorkflowOperationStatus.Succeeded
                : WorkflowOperationStatus.Reconciling,
            ProviderOperationId = updatedAlias.AliasArn ?? operation.ProviderOperationId,
            ObservedRevision = updatedAlias.FunctionVersion,
            Message = hasWeightedTraffic
                ? $"Lambda alias '{aliasName}' is still draining weighted canary traffic before full promotion to version '{spec.DesiredRevision}'."
                : $"Lambda alias '{aliasName}' has been promoted to published version '{spec.DesiredRevision}'."
        };
    }

    public async Task<DeployObservation> RollbackAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var spec = operation.Deploy ?? throw new InvalidOperationException("Deploy workflow operation is missing deploy metadata.");
        cancellationToken.ThrowIfCancellationRequested();

        var rollbackVersion = spec.CurrentRevision;
        if (string.IsNullOrWhiteSpace(rollbackVersion))
        {
            return new DeployObservation
            {
                Status = WorkflowOperationStatus.ManualInterventionRequired,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = spec.CurrentRevision,
                Message = "Rollback requires a previously observed Lambda version, but none was captured for this operation."
            };
        }

        var functionName = ResolveFunctionName(spec);
        var aliasName = ResolveAliasName(spec.Parameters);
        var region = ResolveRegion(spec.Parameters);
        var updatedAlias = await aliasClient.UpdateAliasAsync(
                functionName,
                aliasName,
                rollbackVersion,
                null,
                region,
                cancellationToken)
            .ConfigureAwait(false);

        Log.RollbackRequested(logger, operation.OperationId, spec.TargetId, functionName, aliasName, rollbackVersion);
        return new DeployObservation
        {
            Status = WorkflowOperationStatus.RollbackRequested,
            ProviderOperationId = updatedAlias.AliasArn ?? operation.ProviderOperationId,
            ObservedRevision = updatedAlias.FunctionVersion,
            Message = $"Lambda alias '{aliasName}' is moving back to published version '{rollbackVersion}'."
        };
    }

    private static string ResolveFunctionName(DeployOperationSpec spec)
        => GetParameter(spec.Parameters, "aws.lambda.function_name")
           ?? GetParameter(spec.Parameters, "lambda.function_name")
           ?? GetParameter(spec.Parameters, "lambda.alias_function_name")
           ?? spec.TargetName;

    private static string ResolveAliasName(IReadOnlyDictionary<string, string> parameters)
        => GetParameter(parameters, "aws.lambda.alias_name")
           ?? GetParameter(parameters, "lambda.alias_name")
           ?? GetParameter(parameters, "lambda.alias")
           ?? "live";

    private static string? ResolveRegion(IReadOnlyDictionary<string, string> parameters)
    {
        var explicitRegion = GetParameter(parameters, "aws.region")
            ?? GetParameter(parameters, "lambda.region");
        if (!string.IsNullOrWhiteSpace(explicitRegion))
        {
            return explicitRegion;
        }

        var resourceId = GetParameter(parameters, "target.resource_id")
            ?? GetParameter(parameters, "lambda.function_arn")
            ?? GetParameter(parameters, "lambda.alias_arn");
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            return null;
        }

        var match = LambdaArnRegionPattern.Match(resourceId);
        return match.Success ? match.Groups["region"].Value : null;
    }

    private static string? GetParameter(IReadOnlyDictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static bool TryResolveCanaryWeightFraction(
        IReadOnlyDictionary<string, string> parameters,
        out double? fraction,
        out string? error)
    {
        var rawValue = GetParameter(parameters, "aws.lambda.canary_weight_percentage")
            ?? GetParameter(parameters, "lambda.canary_weight_percentage")
            ?? GetParameter(parameters, "deployment.canary_weight_percentage");
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            fraction = null;
            error = null;
            return true;
        }

        if (!double.TryParse(rawValue, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsedPercentage))
        {
            fraction = null;
            error = "AWS Lambda canary weight must be a numeric percentage between 0 and 100.";
            return false;
        }

        if (parsedPercentage <= 0 || parsedPercentage >= 100)
        {
            fraction = null;
            error = "AWS Lambda canary weight must be greater than 0 and less than 100 percent.";
            return false;
        }

        fraction = parsedPercentage / 100d;
        error = null;
        return true;
    }

    private static partial class Log
    {
        [LoggerMessage(9030, LogLevel.Information, "Submitted Lambda deploy workflow operation {OperationId} for target {TargetId} ({FunctionName}:{AliasName}) to version {DesiredVersion}")]
        public static partial void OperationSubmitted(ILogger logger, string operationId, string targetId, string functionName, string aliasName, string desiredVersion);

        [LoggerMessage(9031, LogLevel.Warning, "Rollback requested for Lambda workflow operation {OperationId} targeting {TargetId} ({FunctionName}:{AliasName}) to version {RollbackVersion}")]
        public static partial void RollbackRequested(ILogger logger, string operationId, string targetId, string functionName, string aliasName, string rollbackVersion);

        [LoggerMessage(9032, LogLevel.Warning, "Lambda alias lookup failed for workflow operation {OperationId} targeting {TargetId}: {ErrorMessage}")]
        public static partial void AliasLookupFailed(ILogger logger, string operationId, string targetId, string errorMessage);
    }
}

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
