// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

#if !HONUA_EXCLUDE_AWS
using Honua.ControlPlane;

namespace Honua.ControlPlane.Executors;

/// <summary>
/// Compares the ECS service fingerprint around a single governed write. A task-definition
/// change, or a desired-count change on restart, fails closed.
/// </summary>
internal sealed class AwsGovernedWorkloadExecutor(IEcsWorkloadControl ecs, ILambdaAliasRecycle lambda) : IGovernedWorkloadExecutor
{
    public async Task RestartEcsAsync(
        string cluster,
        string serviceName,
        string? region,
        CancellationToken cancellationToken = default)
    {
        var before = await DescribeAsync(cluster, serviceName, region, cancellationToken).ConfigureAwait(false);
        try
        {
            await ecs.ForceNewDeploymentAsync(cluster, serviceName, region, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw MapProviderFailure(ex);
        }

        var after = await DescribeAsync(cluster, serviceName, region, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(before.TaskDefinitionArn, after.TaskDefinitionArn, StringComparison.Ordinal) ||
            before.DesiredCount != after.DesiredCount)
        {
            throw new OpsActionException(WorkloadExecutorCodes.FingerprintChanged);
        }
    }

    public async Task SetEcsDesiredCountAsync(
        string cluster,
        string serviceName,
        int desiredCount,
        string? region,
        CancellationToken cancellationToken = default)
    {
        var before = await DescribeAsync(cluster, serviceName, region, cancellationToken).ConfigureAwait(false);
        EcsWorkloadSnapshot after;
        try
        {
            after = await ecs.SetDesiredCountAsync(cluster, serviceName, desiredCount, region, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw MapProviderFailure(ex);
        }

        if (!string.Equals(before.TaskDefinitionArn, after.TaskDefinitionArn, StringComparison.Ordinal))
        {
            throw new OpsActionException(WorkloadExecutorCodes.FingerprintChanged);
        }

        if (after.DesiredCount != desiredCount)
        {
            throw new OpsActionException(WorkloadExecutorCodes.ProviderFailed);
        }
    }

    public async Task RestartLambdaAliasAsync(
        string functionName,
        string aliasName,
        string? region,
        string? serviceUrl,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _ = await lambda.RecycleAsync(functionName, aliasName, region, serviceUrl, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex.Message == LambdaImageGuard.ChangedMessage)
        {
            throw new OpsActionException(WorkloadExecutorCodes.FingerprintChanged);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OpsActionException)
        {
            throw MapProviderFailure(ex);
        }
    }

    private async Task<EcsWorkloadSnapshot> DescribeAsync(
        string cluster,
        string serviceName,
        string? region,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ecs.DescribeAsync(cluster, serviceName, region, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex.Message == "ECS service was not found.")
        {
            throw new OpsActionException(WorkloadExecutorCodes.TargetMissing);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OpsActionException)
        {
            throw MapProviderFailure(ex);
        }
    }

    private static OpsActionException MapProviderFailure(Exception exception)
    {
        if (exception is InvalidOperationException invalid && invalid.Message == "ECS service was not found.")
        {
            return new OpsActionException(WorkloadExecutorCodes.TargetMissing);
        }

        return new OpsActionException(WorkloadExecutorCodes.ProviderFailed);
    }
}
#endif
