// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.ControlPlane.Executors;

internal sealed partial class OpsActionAdminConfigChangeApplier
{
    private const string DesiredCountField = "ecs.desired_count";
    private const int MinDesiredCount = 0;
    private const int MaxDesiredCount = 10;
    private const string KubernetesDeploymentBackend = "honua-kubernetes-deployment";

    private static async Task<OpsActionOutcome> RestartRegisteredEcsAsync(
        IServiceProvider services,
        OpsActionRequest request,
        CancellationToken cancellationToken)
    {
        RejectUnexpectedParameters(request);
        var target = await RequireTargetAsync(services, request, cancellationToken).ConfigureAwait(false);
        if (target.TargetKind != DeployTargetKind.AwsEcs)
        {
            throw new OpsActionException(WorkloadExecutorCodes.TargetKindMismatch);
        }

        var (cluster, serviceName, region) = RequireEcsAddress(target);
        var executor = RequireExecutor(services);
        await executor.RestartEcsAsync(cluster, serviceName, region, cancellationToken).ConfigureAwait(false);
        return new OpsActionOutcome("ecs-service", serviceName, $"Restarted ECS service '{serviceName}'.");
    }

    private static async Task<OpsActionOutcome> RestartRegisteredEksAsync(
        IServiceProvider services,
        OpsActionRequest request,
        DateTimeOffset restartedAt,
        CancellationToken cancellationToken)
    {
        RejectUnexpectedParameters(request);
        var target = await RequireTargetAsync(services, request, cancellationToken).ConfigureAwait(false);
        // GitOps and Argo Rollouts do not name a Deployment this executor can restart.
        // honua-kubernetes-deployment is the product backend registered with the release-image executor.
        if (target.TargetKind != DeployTargetKind.Kubernetes ||
            !string.Equals(target.Backend, KubernetesDeploymentBackend, StringComparison.Ordinal))
        {
            throw new OpsActionException(WorkloadExecutorCodes.TargetKindMismatch);
        }

        var namespaceName = RequireParameter(target, "kubernetes.namespace");
        var deploymentName = RequireParameter(target, "kubernetes.deployment_name");
        var restart = services.GetService<IKubernetesWorkloadRestart>()
            ?? throw new OpsActionException(WorkloadExecutorCodes.Unconfigured);
        try
        {
            await restart.RestartDeploymentAsync(namespaceName, deploymentName, restartedAt, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex.Message == "Kubernetes deployment was not found.")
        {
            throw new OpsActionException(WorkloadExecutorCodes.TargetMissing);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OpsActionException)
        {
            throw new OpsActionException(WorkloadExecutorCodes.ProviderFailed);
        }

        return new OpsActionOutcome("kubernetes-deployment", $"{namespaceName}/{deploymentName}", $"Restarted Kubernetes deployment '{deploymentName}'.");
    }

    private static async Task<OpsActionOutcome> RestartRegisteredLambdaAsync(
        IServiceProvider services,
        OpsActionRequest request,
        CancellationToken cancellationToken)
    {
        RejectUnexpectedParameters(request);
        var target = await RequireTargetAsync(services, request, cancellationToken).ConfigureAwait(false);
        if (target.TargetKind != DeployTargetKind.AwsLambda)
        {
            throw new OpsActionException(WorkloadExecutorCodes.TargetKindMismatch);
        }

        var functionName = LambdaTargetAddress.ResolveFunctionName(target.TargetName, target.Parameters);
        var aliasName = LambdaTargetAddress.ResolveAliasName(target.Parameters);
        if (string.IsNullOrWhiteSpace(functionName))
        {
            throw new OpsActionException(WorkloadExecutorCodes.TargetMissing);
        }

        var region = LambdaTargetAddress.ResolveRegion(target.Parameters);
        var serviceUrl = LambdaTargetAddress.ResolveServiceUrl(target.Parameters);
        var executor = RequireExecutor(services);
        await executor.RestartLambdaAliasAsync(functionName, aliasName, region, serviceUrl, cancellationToken)
            .ConfigureAwait(false);
        return new OpsActionOutcome("lambda-alias", $"{functionName}:{aliasName}", $"Restarted Lambda alias '{aliasName}'.");
    }

    private static async Task<OpsActionOutcome> SetRegisteredConfigAsync(
        IServiceProvider services,
        OpsActionRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ParameterNames.Count != 2 ||
            !request.ParameterNames.Contains("field") ||
            !request.ParameterNames.Contains("value") ||
            !string.Equals(request.ConfigField, DesiredCountField, StringComparison.Ordinal))
        {
            throw new OpsActionException(WorkloadExecutorCodes.ConfigFieldRejected);
        }

        if (request.ConfigValue is not int desiredCount || desiredCount < MinDesiredCount || desiredCount > MaxDesiredCount)
        {
            throw new OpsActionException(WorkloadExecutorCodes.ConfigValueRejected);
        }

        var target = await RequireTargetAsync(services, request, cancellationToken).ConfigureAwait(false);
        if (target.TargetKind != DeployTargetKind.AwsEcs)
        {
            throw new OpsActionException(WorkloadExecutorCodes.TargetKindMismatch);
        }

        var (cluster, serviceName, region) = RequireEcsAddress(target);
        var executor = RequireExecutor(services);
        await executor.SetEcsDesiredCountAsync(cluster, serviceName, desiredCount, region, cancellationToken)
            .ConfigureAwait(false);
        return new OpsActionOutcome(
            "ecs-service",
            serviceName,
            $"Set ECS desired count for '{serviceName}' to {desiredCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");
    }

    private static void RejectUnexpectedParameters(OpsActionRequest request)
    {
        if (request.ParameterNames.Count > 0)
        {
            throw new OpsActionException(WorkloadExecutorCodes.ConfigFieldRejected);
        }
    }

    private static IGovernedWorkloadExecutor RequireExecutor(IServiceProvider services)
        => services.GetService<IGovernedWorkloadExecutor>()
            ?? throw new OpsActionException(WorkloadExecutorCodes.Unconfigured);

    private static async Task<DeployTargetDefinition> RequireTargetAsync(
        IServiceProvider services,
        OpsActionRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Target))
        {
            throw new OpsActionException(WorkloadExecutorCodes.TargetNotRegistered);
        }

        var registry = services.GetService<IDeployTargetRegistry>()
            ?? throw new OpsActionException(WorkloadExecutorCodes.TargetNotRegistered);
        var target = await registry.GetAsync(request.Target, cancellationToken).ConfigureAwait(false);
        return target ?? throw new OpsActionException(WorkloadExecutorCodes.TargetNotRegistered);
    }

    private static (string Cluster, string ServiceName, string? Region) RequireEcsAddress(DeployTargetDefinition target)
    {
        var cluster = Parameter(target, "aws.ecs.cluster");
        var serviceName = Parameter(target, "aws.ecs.canary_service")
            ?? (!string.IsNullOrWhiteSpace(target.TargetName) ? target.TargetName.Trim() : null)
            ?? Parameter(target, "aws.ecs.service");
        if (cluster is null || serviceName is null)
        {
            throw new OpsActionException(WorkloadExecutorCodes.TargetMissing);
        }

        return (cluster, serviceName, Parameter(target, "aws.region"));
    }

    private static string RequireParameter(DeployTargetDefinition target, string key)
        => Parameter(target, key) ?? throw new OpsActionException(WorkloadExecutorCodes.TargetMissing);

    private static string? Parameter(DeployTargetDefinition target, string key)
        => target.Parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
}
