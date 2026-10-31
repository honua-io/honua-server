// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Amazon.ECS.Model;
using Honua.Core.Features.ControlPlane.Abstractions;
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

        if (string.IsNullOrWhiteSpace(GetParameter(spec.Parameters, "aws.ecs.stable_service")))
        {
            blockingReasons.Add("ECS image convergence requires aws.ecs.stable_service to verify the rollback revision.");
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

    private async Task<DeployObservation> ExposeCheckpointedCandidateAsync(
        WorkflowOperationRecord operation, DeployOperationSpec spec, AwsEcsAlbDeployTarget target, CancellationToken cancellationToken)
    {
        if (!ReleaseImageDigest.IsEcsTaskDefinitionArn(operation.ProviderOperationId))
        {
            throw new InvalidOperationException("ECS checkpoint is incomplete.");
        }

        var service = await ecsClient.DescribeServiceAsync(target.Cluster!, target.CanaryService!, target.Region, cancellationToken).ConfigureAwait(false);
        var health = await albClient.DescribeTargetHealthAsync(target.CanaryTargetGroupArn!, target.Region, cancellationToken).ConfigureAwait(false);
        var rule = await albClient.GetListenerRuleWeightsAsync(target.ListenerRuleArn!, target.Region, cancellationToken).ConfigureAwait(false);
        var weights = SummarizeAlbWeights(rule, target);
        _ = TryResolveCanaryWeightPercentage(spec.Parameters, out var configuredWeight, out _);
        var share = spec.CanaryRamp is { IsValid: true } ramp ? ramp.CurrentStepWeight ?? 100 : configuredWeight ?? 100;
        if (!EvaluateDeploymentConvergence(service, operation.ProviderOperationId!).IsConverged ||
            service.DesiredCount <= 0 || health.HealthyCount < service.DesiredCount ||
            (!weights.MatchesExpectedShare(0) && !weights.MatchesExpectedShare(share)))
        {
            throw new InvalidOperationException("ECS candidate or routing changed before cutover.");
        }

        if (!weights.MatchesExpectedShare(share))
        {
            await albClient.UpdateListenerRuleWeightsAsync(target.ListenerRuleArn!,
                BuildWeights(target.CanaryTargetGroupArn!, share, target.StableTargetGroupArn!, 100 - share),
                target.Region, cancellationToken).ConfigureAwait(false);
        }

        return new DeployObservation
        {
            Status = WorkflowOperationStatus.Reconciling,
            ProviderOperationId = operation.ProviderOperationId,
            Message = "ECS candidate traffic shift was submitted."
        };
    }

    private async Task<DeploySubmissionResult> AdvanceCheckpointedCanaryAsync(
        WorkflowOperationRecord operation, DeployOperationSpec spec, AwsEcsAlbDeployTarget target, CancellationToken cancellationToken)
    {
        var ramp = spec.CanaryRamp!;
        var weight = ramp.CurrentStepWeight;
        if (weight is null || ramp.CurrentStepIndex <= 0 || ramp.CurrentStepIndex >= ramp.StepWeights.Count)
        {
            return ImageFailure("ECS canary ramp step is invalid.");
        }

        try
        {
            var service = await ecsClient.DescribeServiceAsync(target.Cluster!, target.CanaryService!, target.Region, cancellationToken).ConfigureAwait(false);
            var convergence = EvaluateDeploymentConvergence(service, operation.ProviderOperationId!);
            var health = await albClient.DescribeTargetHealthAsync(target.CanaryTargetGroupArn!, target.Region, cancellationToken).ConfigureAwait(false);
            var rule = await albClient.GetListenerRuleWeightsAsync(target.ListenerRuleArn!, target.Region, cancellationToken).ConfigureAwait(false);
            var weights = SummarizeAlbWeights(rule, target);
            var previousWeight = ramp.StepWeights[ramp.CurrentStepIndex - 1];
            if (!convergence.IsConverged || service.DesiredCount <= 0 || health.HealthyCount < service.DesiredCount ||
                (!weights.MatchesExpectedShare(previousWeight) && !weights.MatchesExpectedShare(weight.Value)))
            {
                return ImageFailure("ECS canary ramp cannot advance because the candidate or routing state changed.");
            }

            if (!weights.MatchesExpectedShare(weight.Value))
            {
                await albClient.UpdateListenerRuleWeightsAsync(target.ListenerRuleArn!,
                    BuildWeights(target.CanaryTargetGroupArn!, weight.Value, target.StableTargetGroupArn!, 100 - weight.Value),
                    target.Region, cancellationToken).ConfigureAwait(false);
            }

            return new DeploySubmissionResult
            {
                Status = WorkflowOperationStatus.Submitted,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = spec.CurrentRevision,
                Message = $"ECS canary traffic advanced to {weight.Value}%."
            };
        }
        catch (Exception ex) when (IsAwsRuntimeException(ex))
        {
            return SanitizedSubmissionFailure(ex);
        }
    }

    public bool RequiresCheckpoint(WorkflowOperationRecord operation)
        => operation.Deploy is { } spec && ReleaseImageDigest.IsImmutable(spec.DesiredRevision);

    public async Task<DeployCheckpoint> PrepareCheckpointAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken)
    {
        var spec = operation.Deploy ?? throw new InvalidOperationException("Deploy metadata is missing.");
        var target = ResolveTarget(spec);
        EnsureValidTarget(target);
        var stableName = GetParameter(spec.Parameters, "aws.ecs.stable_service")
            ?? throw new InvalidOperationException("Stable ECS service is required to verify rollback identity.");
        if (string.Equals(stableName, target.CanaryService, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Stable and canary ECS services must be distinct.");
        }

        var stable = await ecsClient.DescribeServiceAsync(target.Cluster!, stableName, target.Region, cancellationToken).ConfigureAwait(false);
        var current = await ecsClient.DescribeServiceAsync(target.Cluster!, target.CanaryService!, target.Region, cancellationToken).ConfigureAwait(false);
        if (!ReleaseImageDigest.IsEcsTaskDefinitionArn(stable.TaskDefinitionArn) ||
            !ReleaseImageDigest.IsEcsTaskDefinitionArn(current.TaskDefinitionArn))
        {
            throw new InvalidOperationException("ECS service revision could not be verified.");
        }

        var prepared = await PrepareReleaseImageAsync(operation, spec, target, cancellationToken).ConfigureAwait(false);
        if (prepared.Failure is not null || prepared.TaskDefinitionArn is null)
        {
            throw new InvalidOperationException("ECS image or migration preparation failed; verify provider state.");
        }

        var after = await ecsClient.DescribeServiceAsync(target.Cluster!, target.CanaryService!, target.Region, cancellationToken).ConfigureAwait(false);
        var stableAfter = await ecsClient.DescribeServiceAsync(target.Cluster!, stableName, target.Region, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(current.TaskDefinitionArn, after.TaskDefinitionArn, StringComparison.Ordinal) ||
            !string.Equals(stable.TaskDefinitionArn, stableAfter.TaskDefinitionArn, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("ECS service revision changed during preparation.");
        }

        return new DeployCheckpoint
        {
            CandidateRevision = prepared.TaskDefinitionArn,
            StableRevision = stable.TaskDefinitionArn!,
            OriginalServiceRevision = current.TaskDefinitionArn!
        };
    }

    public async Task<DeployObservation> ActivateCheckpointAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken)
    {
        var spec = operation.Deploy ?? throw new InvalidOperationException("Deploy metadata is missing.");
        var target = ResolveTarget(spec);
        EnsureValidTarget(target);
        var stableName = GetParameter(spec.Parameters, "aws.ecs.stable_service")
            ?? throw new InvalidOperationException("Stable ECS service is required.");
        if (!ReleaseImageDigest.IsEcsTaskDefinitionArn(operation.ProviderOperationId) ||
            !ReleaseImageDigest.IsEcsTaskDefinitionArn(spec.CurrentRevision) ||
            !ReleaseImageDigest.IsEcsTaskDefinitionArn(operation.ObservedState))
        {
            throw new InvalidOperationException("ECS checkpoint is incomplete.");
        }

        if (string.Equals(stableName, target.CanaryService, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Stable and canary ECS services must be distinct.");
        }

        var stable = await ecsClient.DescribeServiceAsync(target.Cluster!, stableName, target.Region, cancellationToken).ConfigureAwait(false);
        var current = await ecsClient.DescribeServiceAsync(target.Cluster!, target.CanaryService!, target.Region, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(stable.TaskDefinitionArn, spec.CurrentRevision, StringComparison.Ordinal) ||
            (!string.Equals(current.TaskDefinitionArn, operation.ObservedState, StringComparison.Ordinal) &&
             !string.Equals(current.TaskDefinitionArn, operation.ProviderOperationId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("ECS service revision changed after preparation.");
        }

        // Keep new traffic on stable while the candidate service is updated. Routing is shifted
        // only by observation after ECS has proved the candidate revision is steady.
        var rule = await albClient.GetListenerRuleWeightsAsync(target.ListenerRuleArn!, target.Region, cancellationToken).ConfigureAwait(false);
        var weights = SummarizeAlbWeights(rule, target);
        if (!string.Equals(current.TaskDefinitionArn, operation.ProviderOperationId, StringComparison.Ordinal) &&
            !weights.MatchesExpectedShare(0))
        {
            throw new InvalidOperationException("ECS candidate service must have zero traffic before activation.");
        }

        if (!string.Equals(current.TaskDefinitionArn, operation.ProviderOperationId, StringComparison.Ordinal))
        {
            await ecsClient.UpdateServiceTaskDefinitionAsync(target.Cluster!, target.CanaryService!,
                operation.ProviderOperationId!, target.Region, cancellationToken).ConfigureAwait(false);
        }

        return new DeployObservation
        {
            Status = WorkflowOperationStatus.Submitted,
            ProviderOperationId = operation.ProviderOperationId,
            Message = "ECS activation submitted; awaiting convergence before traffic shift."
        };
    }

    public async Task<DeployObservation> CompleteProtectionAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
    {
        var spec = operation.Deploy ?? throw new InvalidOperationException("Deploy metadata is missing.");
        if (!RequiresCheckpoint(operation))
        {
            return new DeployObservation
            {
                Status = WorkflowOperationStatus.Succeeded,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = spec.Protection?.CandidateRevision ?? spec.CurrentRevision
            };
        }

        var target = ResolveTarget(spec);
        var stableName = GetParameter(spec.Parameters, "aws.ecs.stable_service")
            ?? throw new InvalidOperationException("Stable ECS service is required.");
        if (!ReleaseImageDigest.IsEcsTaskDefinitionArn(operation.ProviderOperationId) ||
            !ReleaseImageDigest.IsEcsTaskDefinitionArn(spec.CurrentRevision))
        {
            throw new InvalidOperationException("ECS checkpoint is incomplete.");
        }

        if (spec.Protection is { } protection && DateTimeOffset.UtcNow > protection.ObservationDeadline.AddMinutes(30))
        {
            return new DeployObservation { Status = WorkflowOperationStatus.Failed, Message = "Stable ECS service handoff exceeded its time limit." };
        }

        var stable = await ecsClient.DescribeServiceAsync(target.Cluster!, stableName, target.Region, cancellationToken).ConfigureAwait(false);
        var rule = await albClient.GetListenerRuleWeightsAsync(target.ListenerRuleArn!, target.Region, cancellationToken).ConfigureAwait(false);
        var weights = SummarizeAlbWeights(rule, target);
        if (!string.Equals(stable.TaskDefinitionArn, operation.ProviderOperationId, StringComparison.Ordinal))
        {
            if (!weights.MatchesExpectedShare(100))
            {
                throw new InvalidOperationException("Candidate must serve traffic while stable is updated.");
            }

            if (!string.Equals(stable.TaskDefinitionArn, spec.CurrentRevision, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Stable ECS service revision changed during handoff.");
            }

            await ecsClient.UpdateServiceTaskDefinitionAsync(target.Cluster!, stableName, operation.ProviderOperationId!, target.Region, cancellationToken).ConfigureAwait(false);
            return new DeployObservation { Status = WorkflowOperationStatus.Reconciling, Message = "Updating the stable service after protection." };
        }

        if (!weights.MatchesExpectedShare(100) && !weights.MatchesExpectedShare(0))
        {
            throw new InvalidOperationException("ECS traffic routing changed during stable handoff.");
        }

        var primary = stable.Deployments.FirstOrDefault(deployment =>
            string.Equals(deployment.Status, "PRIMARY", StringComparison.OrdinalIgnoreCase));
        if (string.Equals(primary?.RolloutState, "FAILED", StringComparison.OrdinalIgnoreCase))
        {
            return new DeployObservation { Status = WorkflowOperationStatus.Failed, Message = "Stable ECS service handoff failed." };
        }

        var convergence = EvaluateDeploymentConvergence(stable, operation.ProviderOperationId!);
        var health = await albClient.DescribeTargetHealthAsync(target.StableTargetGroupArn!, target.Region, cancellationToken).ConfigureAwait(false);
        if (!convergence.IsConverged || stable.DesiredCount <= 0 || health.HealthyCount < stable.DesiredCount)
        {
            return new DeployObservation { Status = WorkflowOperationStatus.Reconciling, Message = "Waiting for the stable service to become healthy." };
        }

        await albClient.UpdateListenerRuleWeightsAsync(target.ListenerRuleArn!,
            BuildWeights(target.CanaryTargetGroupArn!, 0, target.StableTargetGroupArn!, 100), target.Region, cancellationToken).ConfigureAwait(false);
        return new DeployObservation { Status = WorkflowOperationStatus.Succeeded, ProviderOperationId = operation.ProviderOperationId, Message = "Stable service is healthy on the candidate revision." };
    }

    private async System.Threading.Tasks.Task RestoreStableServiceWhenReadyAsync(
        WorkflowOperationRecord operation, DeployOperationSpec spec, AwsEcsAlbDeployTarget target, CancellationToken cancellationToken)
    {
        var stableName = GetParameter(spec.Parameters, "aws.ecs.stable_service")
            ?? throw new InvalidOperationException("Stable ECS service is required.");
        if (!ReleaseImageDigest.IsEcsTaskDefinitionArn(spec.CurrentRevision))
        {
            throw new InvalidOperationException("Stable rollback revision is missing.");
        }

        var stable = await ecsClient.DescribeServiceAsync(target.Cluster!, stableName, target.Region, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(stable.TaskDefinitionArn, spec.CurrentRevision, StringComparison.Ordinal))
        {
            if (!string.Equals(stable.TaskDefinitionArn, operation.ProviderOperationId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Stable ECS service revision changed during rollback.");
            }

            await ecsClient.UpdateServiceTaskDefinitionAsync(target.Cluster!, stableName, spec.CurrentRevision!, target.Region, cancellationToken).ConfigureAwait(false);
            return;
        }

        var convergence = EvaluateDeploymentConvergence(stable, spec.CurrentRevision!);
        var health = await albClient.DescribeTargetHealthAsync(target.StableTargetGroupArn!, target.Region, cancellationToken).ConfigureAwait(false);
        if (!convergence.IsConverged || stable.DesiredCount <= 0 || health.HealthyCount < stable.DesiredCount) return;
        await albClient.UpdateListenerRuleWeightsAsync(target.ListenerRuleArn!,
            BuildWeights(target.CanaryTargetGroupArn!, 0, target.StableTargetGroupArn!, 100), target.Region, cancellationToken).ConfigureAwait(false);
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
