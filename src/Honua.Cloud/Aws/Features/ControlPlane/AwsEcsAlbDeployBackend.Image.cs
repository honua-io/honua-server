// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Amazon.ECS.Model;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

internal sealed partial class AwsEcsAlbDeployBackend
{
    private const string MigrationTaskParameter = "aws.ecs.migration_task_definition";
    private const string MigrationSubnetsParameter = "aws.ecs.migration_subnets";
    private const string MigrationSecurityGroupsParameter = "aws.ecs.migration_security_groups";
    private const string MigrationContainerParameter = "aws.ecs.migration_container_name";
    private const string ContainerNameParameter = "aws.ecs.container_name";
    private const string SteadyAttemptsParameter = "aws.ecs.steady_attempts";
    private const string SteadyDelayParameter = "aws.ecs.steady_delay_ms";
    private const string MigrationAttemptsParameter = "aws.ecs.migration_attempts";
    private const string MigrationDelayParameter = "aws.ecs.migration_delay_ms";

    private readonly record struct PreparedTaskDefinition(string? TaskDefinitionArn, DeploySubmissionResult? Failure);

    private static void AppendImageConvergencePlan(
        DeployOperationSpec spec,
        List<string> blockingReasons,
        List<string> warnings)
    {
        var desired = spec.DesiredRevision;
        if (string.IsNullOrWhiteSpace(desired))
        {
            return;
        }

        if (desired.Contains("@sha256:", StringComparison.Ordinal) && !ReleaseImageDigest.IsImmutable(desired))
        {
            blockingReasons.Add(
                $"Desired revision is not an immutable image digest ({ReleaseImageDigest.NotImmutableCode}).");
            return;
        }

        if (!ReleaseImageDigest.IsImmutable(desired))
        {
            return;
        }

        var migrationTask = GetParameter(spec.Parameters, MigrationTaskParameter);
        if (migrationTask is null)
        {
            warnings.Add(
                "Schema migrations are not applied by this service update. A reader-incompatible migration is not implied by the deploy.");
            return;
        }

        if (string.IsNullOrWhiteSpace(GetParameter(spec.Parameters, MigrationSubnetsParameter)) ||
            string.IsNullOrWhiteSpace(GetParameter(spec.Parameters, MigrationSecurityGroupsParameter)))
        {
            blockingReasons.Add(
                "ECS image convergence with aws.ecs.migration_task_definition requires aws.ecs.migration_subnets and aws.ecs.migration_security_groups.");
        }
    }

    /// <summary>
    /// Clones the serving task definition onto the digest, and runs the product migrate-only
    /// task first when the deployment names one. Returns before any listener-rule write.
    /// </summary>
    private async Task<PreparedTaskDefinition> PrepareReleaseImageAsync(
        WorkflowOperationRecord operation,
        DeployOperationSpec spec,
        AwsEcsAlbDeployTarget target,
        CancellationToken cancellationToken)
    {
        var service = await ecsClient.DescribeServiceAsync(
                target.Cluster!,
                target.CanaryService!,
                target.Region,
                cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(service.TaskDefinitionArn))
        {
            return new PreparedTaskDefinition(null, ImageFailure(
                "ECS service has no task definition to clone. The service was not updated."));
        }

        var migrationFailure = await RunMigrationIfConfiguredAsync(operation, spec, target, cancellationToken)
            .ConfigureAwait(false);
        if (migrationFailure is not null)
        {
            return new PreparedTaskDefinition(null, migrationFailure);
        }

        var current = await ecsClient.DescribeTaskDefinitionAsync(
                service.TaskDefinitionArn,
                target.Region,
                cancellationToken)
            .ConfigureAwait(false);
        var names = ContainerNames(current);
        var containerName = EcsTaskDefinitionImage.SelectContainerName(
            names,
            GetParameter(spec.Parameters, ContainerNameParameter));
        if (containerName is null)
        {
            return new PreparedTaskDefinition(null, ImageFailure(
                "ECS image convergence could not select one container. The service was not updated."));
        }

        RegisterTaskDefinitionRequest request;
        try
        {
            request = EcsTaskDefinitionImage.CloneWithImage(current, containerName, spec.DesiredRevision.Trim());
        }
        catch (InvalidOperationException ex)
        {
            Log.SubmissionFailed(logger, operation.OperationId, spec.TargetId, ex.Message);
            return new PreparedTaskDefinition(null, ImageFailure(
                "ECS image convergence refused to register a task definition. The service was not updated."));
        }

        var registeredArn = await ecsClient.RegisterTaskDefinitionAsync(request, target.Region, cancellationToken)
            .ConfigureAwait(false);
        if (!ReleaseImageDigest.IsEcsTaskDefinitionArn(registeredArn))
        {
            return new PreparedTaskDefinition(null, ImageFailure(
                "ECS task definition registration did not return a task definition ARN. The service was not updated."));
        }

        return new PreparedTaskDefinition(registeredArn, null);
    }

    private async Task<DeploySubmissionResult?> RunMigrationIfConfiguredAsync(
        WorkflowOperationRecord operation,
        DeployOperationSpec spec,
        AwsEcsAlbDeployTarget target,
        CancellationToken cancellationToken)
    {
        var migrationTask = GetParameter(spec.Parameters, MigrationTaskParameter);
        if (migrationTask is null)
        {
            return null;
        }

        var subnets = SplitList(GetParameter(spec.Parameters, MigrationSubnetsParameter));
        var securityGroups = SplitList(GetParameter(spec.Parameters, MigrationSecurityGroupsParameter));
        if (subnets.Length == 0 || securityGroups.Length == 0)
        {
            return ImageFailure(
                "ECS image convergence with aws.ecs.migration_task_definition requires aws.ecs.migration_subnets and aws.ecs.migration_security_groups. The service was not updated.");
        }

        var migrationDefinition = await ecsClient.DescribeTaskDefinitionAsync(
                migrationTask,
                target.Region,
                cancellationToken)
            .ConfigureAwait(false);
        var containerName = GetParameter(spec.Parameters, MigrationContainerParameter) ?? "honua-migrate";
        RegisterTaskDefinitionRequest request;
        try
        {
            request = EcsTaskDefinitionImage.CloneWithImage(migrationDefinition, containerName, spec.DesiredRevision.Trim());
        }
        catch (InvalidOperationException ex)
        {
            Log.SubmissionFailed(logger, operation.OperationId, spec.TargetId, ex.Message);
            return ImageFailure(
                "ECS migration task definition could not be cloned. The service was not updated.");
        }

        var registeredArn = await ecsClient.RegisterTaskDefinitionAsync(request, target.Region, cancellationToken)
            .ConfigureAwait(false);
        var taskArn = await ecsClient.RunTaskAsync(
                target.Cluster!,
                registeredArn,
                subnets,
                securityGroups,
                target.Region,
                cancellationToken)
            .ConfigureAwait(false);

        var attempts = ReadBoundedInt(spec.Parameters, MigrationAttemptsParameter, defaultValue: 15, max: 60);
        var delayMs = ReadBoundedInt(spec.Parameters, MigrationDelayParameter, defaultValue: 2000, max: 30_000);
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (attempt > 0 && delayMs > 0)
            {
                await System.Threading.Tasks.Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }

            var stopped = await ecsClient.DescribeStoppedTaskAsync(
                    target.Cluster!,
                    taskArn,
                    containerName,
                    target.Region,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(stopped.LastStatus, "STOPPED", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (stopped.ExitCode == 0)
            {
                return null;
            }

            return ImageFailure("ECS migration task did not exit 0. The service was not updated.");
        }

        return ImageFailure("ECS migration task did not finish. The service was not updated.");
    }

    private async Task<DeploySubmissionResult> AwaitReleaseImageSteadyAsync(
        WorkflowOperationRecord operation,
        DeployOperationSpec spec,
        AwsEcsAlbDeployTarget target,
        string taskDefinitionArn,
        string? priorTaskDefinitionArn,
        int? canaryWeight,
        CancellationToken cancellationToken)
    {
        var attempts = ReadBoundedInt(spec.Parameters, SteadyAttemptsParameter, defaultValue: 15, max: 60);
        var delayMs = ReadBoundedInt(spec.Parameters, SteadyDelayParameter, defaultValue: 2000, max: 30_000);
        var expectedShare = canaryWeight ?? 100;
        AwsEcsServiceState? last = null;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (attempt > 0 && delayMs > 0)
            {
                await System.Threading.Tasks.Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }

            last = await ecsClient.DescribeServiceAsync(
                    target.Cluster!,
                    target.CanaryService!,
                    target.Region,
                    cancellationToken)
                .ConfigureAwait(false);
            var primary = last.Deployments.FirstOrDefault(deployment =>
                string.Equals(deployment.Status, "PRIMARY", StringComparison.OrdinalIgnoreCase));
            if (string.Equals(primary?.RolloutState, "FAILED", StringComparison.OrdinalIgnoreCase))
            {
                return new DeploySubmissionResult
                {
                    Status = WorkflowOperationStatus.Failed,
                    ProviderOperationId = taskDefinitionArn,
                    ObservedRevision = RollbackIdentity(priorTaskDefinitionArn),
                    Message = "ECS rollout failed before the service reached steady state."
                };
            }

            var convergence = EvaluateDeploymentConvergence(last, taskDefinitionArn);
            if (!convergence.IsConverged)
            {
                continue;
            }

            var ruleState = await albClient.GetListenerRuleWeightsAsync(
                    target.ListenerRuleArn!,
                    target.Region,
                    cancellationToken)
                .ConfigureAwait(false);
            var weights = SummarizeAlbWeights(ruleState, target);
            if (!weights.MatchesExpectedShare(expectedShare))
            {
                continue;
            }

            // A canary share is not a completed rollout. Promotion still has to move the rule to 100%.
            if (canaryWeight.HasValue)
            {
                return new DeploySubmissionResult
                {
                    Status = WorkflowOperationStatus.Submitted,
                    ProviderOperationId = taskDefinitionArn,
                    ObservedRevision = RollbackIdentity(priorTaskDefinitionArn),
                    Message = $"ECS canary service '{target.CanaryService}' is routing {expectedShare}% of traffic to task definition '{taskDefinitionArn}'."
                };
            }

            return new DeploySubmissionResult
            {
                Status = WorkflowOperationStatus.Submitted,
                ProviderOperationId = taskDefinitionArn,
                ObservedRevision = RollbackIdentity(priorTaskDefinitionArn),
                Message = $"ECS canary service '{target.CanaryService}' is serving 100% of traffic on task definition '{taskDefinitionArn}'."
            };
        }

        return new DeploySubmissionResult
        {
            Status = WorkflowOperationStatus.Submitted,
            ProviderOperationId = taskDefinitionArn,
            ObservedRevision = RollbackIdentity(priorTaskDefinitionArn),
            Message = $"ECS canary service '{target.CanaryService}' is still converging on task definition '{taskDefinitionArn}'."
        };
    }

    // CurrentRevision is what an in-flight rollback compares to the stable service.
    // Keep that pre-update task definition here; the new ARN stays on ProviderOperationId.
    private static string? RollbackIdentity(string? priorTaskDefinitionArn)
        => string.IsNullOrWhiteSpace(priorTaskDefinitionArn) ? null : priorTaskDefinitionArn;

    private async System.Threading.Tasks.Task RestoreAlbWeightsAsync(
        WorkflowOperationRecord operation,
        DeployOperationSpec spec,
        AwsEcsAlbDeployTarget target,
        AwsAlbListenerRuleState originalRuleState,
        CancellationToken cancellationToken)
    {
        try
        {
            await albClient.UpdateListenerRuleWeightsAsync(
                    target.ListenerRuleArn!,
                    originalRuleState.TargetGroupWeights,
                    target.Region,
                    cancellationToken)
                .ConfigureAwait(false);
            Log.AlbWeightsRestored(logger, operation.OperationId, spec.TargetId, target.ListenerRuleArn!);
        }
        catch (Exception restoreEx) when (IsAwsRuntimeException(restoreEx))
        {
            Log.AlbWeightRestoreFailed(logger, operation.OperationId, spec.TargetId, restoreEx.Message);
        }
    }

    private static DeploySubmissionResult ImageFailure(string message)
        => new()
        {
            Status = WorkflowOperationStatus.Failed,
            Message = message
        };

    private static List<string> ContainerNames(TaskDefinition definition)
    {
        var names = new List<string>();
        if (definition.ContainerDefinitions is null)
        {
            return names;
        }

        foreach (var container in definition.ContainerDefinitions)
        {
            if (!string.IsNullOrWhiteSpace(container.Name))
            {
                names.Add(container.Name);
            }
        }

        return names;
    }

    private static string[] SplitList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static int ReadBoundedInt(
        IReadOnlyDictionary<string, string> parameters,
        string key,
        int defaultValue,
        int max)
    {
        var raw = GetParameter(parameters, key);
        if (raw is null || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return defaultValue;
        }

        if (parsed < 0)
        {
            return 0;
        }

        return Math.Min(parsed, max);
    }
}
