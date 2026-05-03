// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using Amazon;
using Amazon.ECS;
using Amazon.ECS.Model;
using Amazon.ElasticLoadBalancingV2;
using Amazon.ElasticLoadBalancingV2.Model;
using Amazon.Runtime;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ServiceDefaults;
using AlbAction = Amazon.ElasticLoadBalancingV2.Model.Action;

namespace Honua.Server.Features.Infrastructure.ControlPlane;

/// <summary>
/// Weight entry for one ALB target group within a listener-rule forward action.
/// </summary>
internal sealed record AwsAlbTargetGroupWeight
{
    public required string TargetGroupArn { get; init; }

    public int Weight { get; init; }
}

/// <summary>
/// Read-only snapshot of an ALB listener rule's weighted forward action.
/// </summary>
internal sealed record AwsAlbListenerRuleState
{
    public required string ListenerRuleArn { get; init; }

    public IReadOnlyList<AwsAlbTargetGroupWeight> TargetGroupWeights { get; init; } =
        Array.Empty<AwsAlbTargetGroupWeight>();
}

/// <summary>
/// Read-only snapshot of an ECS service used to confirm canary convergence.
/// </summary>
internal sealed record AwsEcsServiceState
{
    public required string ServiceName { get; init; }

    public string? TaskDefinitionArn { get; init; }

    public int RunningCount { get; init; }

    public int DesiredCount { get; init; }

    public int PendingCount { get; init; }

    public string? Status { get; init; }
}

/// <summary>
/// ALB client surface used by the deploy backend so unit tests can substitute a stub
/// without exercising the AWS SDK.
/// </summary>
internal interface IAwsAlbClient
{
    Task<AwsAlbListenerRuleState> GetListenerRuleWeightsAsync(
        string ruleArn,
        string? region,
        CancellationToken cancellationToken = default);

    Task<AwsAlbListenerRuleState> UpdateListenerRuleWeightsAsync(
        string ruleArn,
        IReadOnlyList<AwsAlbTargetGroupWeight> weights,
        string? region,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// ECS client surface used by the deploy backend so unit tests can substitute a stub
/// without exercising the AWS SDK.
/// </summary>
internal interface IAwsEcsClient
{
    Task<AwsEcsServiceState> DescribeServiceAsync(
        string cluster,
        string serviceName,
        string? region,
        CancellationToken cancellationToken = default);

    System.Threading.Tasks.Task UpdateServiceTaskDefinitionAsync(
        string cluster,
        string serviceName,
        string taskDefinitionArn,
        string? region,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// AWS SDK implementation of <see cref="IAwsAlbClient"/> backed by
/// <c>AmazonElasticLoadBalancingV2Client</c>. Mirrors the per-call client construction
/// used by <see cref="AwsSdkLambdaAliasClient"/> so credential resolution and region
/// selection follow the same chain that the rest of the server already trusts.
/// </summary>
internal sealed class AwsSdkAlbClient : IAwsAlbClient
{
    public async Task<AwsAlbListenerRuleState> GetListenerRuleWeightsAsync(
        string ruleArn,
        string? region,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(region);
        var response = await client.DescribeRulesAsync(
                new DescribeRulesRequest
                {
                    RuleArns = [ruleArn]
                },
                cancellationToken)
            .ConfigureAwait(false);

        var rule = response.Rules?.FirstOrDefault()
            ?? throw new AmazonElasticLoadBalancingV2Exception($"Listener rule '{ruleArn}' was not found.");
        return MapRule(rule);
    }

    public async Task<AwsAlbListenerRuleState> UpdateListenerRuleWeightsAsync(
        string ruleArn,
        IReadOnlyList<AwsAlbTargetGroupWeight> weights,
        string? region,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(region);

        // Read the existing rule first so ModifyRule can preserve the forward
        // action's TargetGroupStickinessConfig, action ordering, and any sibling
        // action types (for example authenticate-cognito chained before forward).
        // ModifyRule replaces the entire Actions array, so a blind rebuild would
        // silently drop those settings — the runbook contract is to mutate weights
        // on the existing rule, not to recreate it.
        var describeResponse = await client.DescribeRulesAsync(
                new DescribeRulesRequest { RuleArns = [ruleArn] },
                cancellationToken)
            .ConfigureAwait(false);
        var existingRule = describeResponse.Rules?.FirstOrDefault()
            ?? throw new AmazonElasticLoadBalancingV2Exception($"Listener rule '{ruleArn}' was not found.");

        var existingActions = (IReadOnlyList<AlbAction>?)existingRule.Actions ?? Array.Empty<AlbAction>();
        var updatedActions = AwsAlbActionMutator.RebuildActionsWithUpdatedWeights(existingActions, weights, ruleArn);

        var response = await client.ModifyRuleAsync(
                new ModifyRuleRequest
                {
                    RuleArn = ruleArn,
                    Actions = [.. updatedActions]
                },
                cancellationToken)
            .ConfigureAwait(false);

        var rule = response.Rules?.FirstOrDefault()
            ?? throw new AmazonElasticLoadBalancingV2Exception($"ModifyRule did not return a rule body for '{ruleArn}'.");
        return MapRule(rule);
    }

    private static AmazonElasticLoadBalancingV2Client CreateClient(string? region)
        => string.IsNullOrWhiteSpace(region)
            ? new AmazonElasticLoadBalancingV2Client()
            : new AmazonElasticLoadBalancingV2Client(RegionEndpoint.GetBySystemName(region));

    private static AwsAlbListenerRuleState MapRule(Rule rule)
    {
        var weights = new List<AwsAlbTargetGroupWeight>();
        var forward = rule.Actions?.FirstOrDefault(action => action.Type == ActionTypeEnum.Forward);
        var tuples = forward?.ForwardConfig?.TargetGroups;
        if (tuples != null)
        {
            foreach (var tuple in tuples)
            {
                if (string.IsNullOrWhiteSpace(tuple.TargetGroupArn))
                {
                    continue;
                }

                weights.Add(new AwsAlbTargetGroupWeight
                {
                    TargetGroupArn = tuple.TargetGroupArn,
                    Weight = tuple.Weight ?? 0
                });
            }
        }

        return new AwsAlbListenerRuleState
        {
            ListenerRuleArn = rule.RuleArn ?? string.Empty,
            TargetGroupWeights = weights
        };
    }
}

/// <summary>
/// Builds the <see cref="AlbAction"/> list passed to <c>ModifyRule</c> so target
/// group weights change in place while the existing forward action's stickiness
/// configuration, action ordering, and sibling actions (for example
/// authenticate-cognito) are preserved. Lifted out of <see cref="AwsSdkAlbClient"/>
/// so unit tests can verify the mutation contract without an AWS SDK fake.
/// </summary>
internal static class AwsAlbActionMutator
{
    public static List<AlbAction> RebuildActionsWithUpdatedWeights(
        IReadOnlyList<AlbAction> existingActions,
        IReadOnlyList<AwsAlbTargetGroupWeight> newWeights,
        string ruleArn)
    {
        var existingForward = existingActions.FirstOrDefault(action => action.Type == ActionTypeEnum.Forward)
            ?? throw new AmazonElasticLoadBalancingV2Exception(
                $"Listener rule '{ruleArn}' has no forward action; weighted traffic shifting requires an existing forward action.");

        var updatedForward = new AlbAction
        {
            Type = ActionTypeEnum.Forward,
            Order = existingForward.Order,
            ForwardConfig = new ForwardActionConfig
            {
                TargetGroups = newWeights
                    .Select(weight => new TargetGroupTuple
                    {
                        TargetGroupArn = weight.TargetGroupArn,
                        Weight = weight.Weight
                    })
                    .ToList(),
                TargetGroupStickinessConfig = existingForward.ForwardConfig?.TargetGroupStickinessConfig
            }
        };

        var result = new List<AlbAction>(existingActions.Count);
        var replaced = false;
        foreach (var action in existingActions)
        {
            if (!replaced && action.Type == ActionTypeEnum.Forward)
            {
                result.Add(updatedForward);
                replaced = true;
            }
            else
            {
                result.Add(action);
            }
        }

        return result;
    }
}

/// <summary>
/// AWS SDK implementation of <see cref="IAwsEcsClient"/> backed by <c>AmazonECSClient</c>.
/// </summary>
internal sealed class AwsSdkEcsClient : IAwsEcsClient
{
    public async Task<AwsEcsServiceState> DescribeServiceAsync(
        string cluster,
        string serviceName,
        string? region,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(region);
        var response = await client.DescribeServicesAsync(
                new DescribeServicesRequest
                {
                    Cluster = cluster,
                    Services = [serviceName]
                },
                cancellationToken)
            .ConfigureAwait(false);

        var service = response.Services?.FirstOrDefault()
            ?? throw new AmazonECSException($"ECS service '{serviceName}' was not found in cluster '{cluster}'.");

        return new AwsEcsServiceState
        {
            ServiceName = service.ServiceName ?? serviceName,
            TaskDefinitionArn = service.TaskDefinition,
            RunningCount = service.RunningCount ?? 0,
            DesiredCount = service.DesiredCount ?? 0,
            PendingCount = service.PendingCount ?? 0,
            Status = service.Status
        };
    }

    public async System.Threading.Tasks.Task UpdateServiceTaskDefinitionAsync(
        string cluster,
        string serviceName,
        string taskDefinitionArn,
        string? region,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(region);
        await client.UpdateServiceAsync(
                new UpdateServiceRequest
                {
                    Cluster = cluster,
                    Service = serviceName,
                    TaskDefinition = taskDefinitionArn
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static AmazonECSClient CreateClient(string? region)
        => string.IsNullOrWhiteSpace(region)
            ? new AmazonECSClient()
            : new AmazonECSClient(RegionEndpoint.GetBySystemName(region));
}

/// <summary>
/// Native AWS ECS + ALB canary deploy backend. Manages ALB listener-rule weights and
/// the canary ECS service's task definition through direct AWS SDK calls so Honua
/// controls promotion and rollback end-to-end without relying on an external GitOps
/// agent. Coexists with <see cref="AwsEcsGitOpsDeployBackend"/> under
/// <see cref="DeployTargetKind.AwsEcs"/>; targets pick by backend name.
/// </summary>
internal sealed partial class AwsEcsAlbDeployBackend(
    IAwsAlbClient albClient,
    IAwsEcsClient ecsClient,
    ILogger<AwsEcsAlbDeployBackend> logger) : IDeployBackend
{
    internal const string AdapterBackendName = "honua-aws-ecs-alb";

    public string BackendName => AdapterBackendName;

    public DeployTargetKind TargetKind => DeployTargetKind.AwsEcs;

    public Task<DeployBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        => System.Threading.Tasks.Task.FromResult(new DeployBackendCapabilities
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

        if (string.IsNullOrWhiteSpace(spec.DesiredRevision))
        {
            blockingReasons.Add("A desired task definition ARN is required for AWS ECS/ALB deploy workflows.");
        }

        if (string.IsNullOrWhiteSpace(target.Cluster))
        {
            blockingReasons.Add("AWS ECS/ALB deploy workflows require aws.ecs.cluster.");
        }

        if (string.IsNullOrWhiteSpace(target.CanaryService))
        {
            blockingReasons.Add("AWS ECS/ALB deploy workflows require aws.ecs.canary_service.");
        }

        if (string.IsNullOrWhiteSpace(target.ListenerRuleArn))
        {
            blockingReasons.Add("AWS ECS/ALB deploy workflows require aws.alb.listener_rule_arn.");
        }

        if (string.IsNullOrWhiteSpace(target.CanaryTargetGroupArn))
        {
            blockingReasons.Add("AWS ECS/ALB deploy workflows require aws.alb.canary_target_group_arn.");
        }

        if (string.IsNullOrWhiteSpace(target.StableTargetGroupArn))
        {
            blockingReasons.Add("AWS ECS/ALB deploy workflows require aws.alb.stable_target_group_arn.");
        }

        if (string.IsNullOrWhiteSpace(spec.ArtifactReference))
        {
            warnings.Add("No artifact reference is configured for this target.");
        }

        if (!TryResolveCanaryWeightPercentage(spec.Parameters, out var canaryWeight, out var canaryError))
        {
            blockingReasons.Add(canaryError ?? "AWS ECS/ALB canary weight is invalid.");
        }
        else if (canaryWeight.HasValue &&
            (!spec.Parameters.TryGetValue("telemetry.connection", out var telemetryConnection) ||
             string.IsNullOrWhiteSpace(telemetryConnection)))
        {
            blockingReasons.Add("AWS ECS/ALB canary traffic shifting requires telemetry.connection so the rollout can be promoted or rolled back automatically.");
        }

        return System.Threading.Tasks.Task.FromResult(new DeployPlan
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

        using var activity = StartActivity(ControlPlaneTelemetry.Activities.BackendStart, operation, target);

        try
        {
            var canaryServiceState = await ecsClient.DescribeServiceAsync(
                    target.Cluster!,
                    target.CanaryService!,
                    target.Region,
                    cancellationToken)
                .ConfigureAwait(false);
            var observedTaskDefinition = canaryServiceState.TaskDefinitionArn;

            await ecsClient.UpdateServiceTaskDefinitionAsync(
                    target.Cluster!,
                    target.CanaryService!,
                    spec.DesiredRevision,
                    target.Region,
                    cancellationToken)
                .ConfigureAwait(false);

            var canaryShare = canaryWeight ?? 100;
            var stableShare = 100 - canaryShare;
            await albClient.UpdateListenerRuleWeightsAsync(
                    target.ListenerRuleArn!,
                    BuildWeights(target.CanaryTargetGroupArn!, canaryShare, target.StableTargetGroupArn!, stableShare),
                    target.Region,
                    cancellationToken)
                .ConfigureAwait(false);

            Log.OperationSubmitted(logger, operation.OperationId, spec.TargetId, target.Cluster!, target.CanaryService!, spec.DesiredRevision);
            return new DeploySubmissionResult
            {
                Status = WorkflowOperationStatus.Submitted,
                ProviderOperationId = $"{target.Cluster}:{target.CanaryService}",
                ObservedRevision = observedTaskDefinition,
                Message = canaryWeight.HasValue
                    ? $"ECS canary service '{target.CanaryService}' is routing {canaryShare}% of traffic to task definition '{spec.DesiredRevision}'."
                    : $"ECS canary service '{target.CanaryService}' is moving 100% of traffic to task definition '{spec.DesiredRevision}'."
            };
        }
        catch (AmazonElasticLoadBalancingV2Exception ex)
        {
            // The AWS SDK error text can carry ARNs, request IDs, account hints, or
            // other internals that should not surface to the operator-facing
            // ErrorMessage that DeployWorkflowService persists. Mirror the
            // ObserveAsync sanitization path: log structured detail, return a
            // sanitized Failed submission so the durable record stays clean.
            Log.SubmissionFailed(logger, operation.OperationId, spec.TargetId, ex.Message);
            activity?.SetStatus(ActivityStatusCode.Error);
            return SanitizedSubmissionFailure(ex);
        }
        catch (AmazonECSException ex)
        {
            Log.SubmissionFailed(logger, operation.OperationId, spec.TargetId, ex.Message);
            activity?.SetStatus(ActivityStatusCode.Error);
            return SanitizedSubmissionFailure(ex);
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

            var ruleState = await albClient.GetListenerRuleWeightsAsync(
                    target.ListenerRuleArn!,
                    target.Region,
                    cancellationToken)
                .ConfigureAwait(false);
            var serviceState = await ecsClient.DescribeServiceAsync(
                    target.Cluster!,
                    target.CanaryService!,
                    target.Region,
                    cancellationToken)
                .ConfigureAwait(false);

            var canaryShare = ResolveWeight(ruleState, target.CanaryTargetGroupArn!);
            var stableShare = ResolveWeight(ruleState, target.StableTargetGroupArn!);
            Log.StateObserved(logger, target.CanaryService!, serviceState.RunningCount, serviceState.DesiredCount, canaryShare);

            if (operation.Status == WorkflowOperationStatus.RollbackRequested)
            {
                // Rollback is terminal once ALB weights are stable=100/canary=0 and the
                // canary ECS deployment has settled (PendingCount == 0). The canary
                // service may still hold warm tasks because operators routinely keep it
                // scaled out for the next rollout — no traffic flows there. Waiting for
                // RunningCount to reach zero would leave the operation pinned in
                // RollbackRequested forever for the common warm-canary topology.
                if (stableShare == 100 && canaryShare == 0 && IsCanaryDeploymentSettled(serviceState))
                {
                    return new DeployObservation
                    {
                        Status = WorkflowOperationStatus.RolledBack,
                        ProviderOperationId = operation.ProviderOperationId,
                        ObservedRevision = serviceState.TaskDefinitionArn,
                        Message = $"ECS/ALB rollout rolled back: stable target group is serving 100% of traffic and canary service '{target.CanaryService}' has no pending deployment."
                    };
                }

                return new DeployObservation
                {
                    Status = WorkflowOperationStatus.RollbackRequested,
                    ProviderOperationId = operation.ProviderOperationId,
                    ObservedRevision = serviceState.TaskDefinitionArn,
                    Message = $"ECS/ALB rollout rollback is still settling canary service '{target.CanaryService}' (pending={serviceState.PendingCount}, canaryWeight={canaryShare})."
                };
            }

            var canaryConverged = serviceState.RunningCount >= serviceState.DesiredCount && serviceState.PendingCount == 0;
            // Promotion or success requires the ECS service to actually be running the
            // requested task definition. Otherwise an external rollback or a stale
            // service definition could satisfy ALB weights and rolling counts while
            // the workload is still on the previous revision.
            var taskDefinitionMatches = string.Equals(
                serviceState.TaskDefinitionArn,
                spec.DesiredRevision,
                StringComparison.Ordinal);

            if (canaryShare == 100 && canaryConverged && taskDefinitionMatches)
            {
                return new DeployObservation
                {
                    Status = WorkflowOperationStatus.Succeeded,
                    ProviderOperationId = operation.ProviderOperationId,
                    ObservedRevision = serviceState.TaskDefinitionArn,
                    Message = $"ECS canary service '{target.CanaryService}' is serving 100% of traffic on task definition '{spec.DesiredRevision}'."
                };
            }

            if (desiredCanaryWeight.HasValue &&
                canaryShare == desiredCanaryWeight.Value &&
                canaryConverged &&
                taskDefinitionMatches)
            {
                return new DeployObservation
                {
                    Status = WorkflowOperationStatus.Reconciling,
                    ProviderOperationId = operation.ProviderOperationId,
                    ObservedRevision = serviceState.TaskDefinitionArn,
                    PromotionRecommended = true,
                    Message = $"ECS canary service '{target.CanaryService}' is holding {canaryShare}% of traffic on task definition '{spec.DesiredRevision}' and is ready for promotion."
                };
            }

            if (!taskDefinitionMatches)
            {
                return new DeployObservation
                {
                    Status = WorkflowOperationStatus.Reconciling,
                    ProviderOperationId = operation.ProviderOperationId,
                    ObservedRevision = serviceState.TaskDefinitionArn,
                    Message = $"ECS canary service '{target.CanaryService}' is reporting task definition '{serviceState.TaskDefinitionArn ?? "<unknown>"}' instead of the desired '{spec.DesiredRevision}'; waiting for ECS to adopt the requested revision."
                };
            }

            return new DeployObservation
            {
                Status = WorkflowOperationStatus.Reconciling,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = serviceState.TaskDefinitionArn,
                Message = $"ECS canary service '{target.CanaryService}' is converging to task definition '{spec.DesiredRevision}' (running={serviceState.RunningCount}, desired={serviceState.DesiredCount}, canaryWeight={canaryShare})."
            };
        }
        catch (AmazonElasticLoadBalancingV2Exception ex)
        {
            Log.StateLookupFailed(logger, operation.OperationId, spec.TargetId, ex.Message);
            activity?.SetStatus(ActivityStatusCode.Error);
            return SanitizedFailure(operation, ex);
        }
        catch (AmazonECSException ex)
        {
            Log.StateLookupFailed(logger, operation.OperationId, spec.TargetId, ex.Message);
            activity?.SetStatus(ActivityStatusCode.Error);
            return SanitizedFailure(operation, ex);
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
            await albClient.UpdateListenerRuleWeightsAsync(
                    target.ListenerRuleArn!,
                    BuildWeights(target.CanaryTargetGroupArn!, 100, target.StableTargetGroupArn!, 0),
                    target.Region,
                    cancellationToken)
                .ConfigureAwait(false);

            Log.PromotionCompleted(logger, operation.OperationId, spec.TargetId, target.Cluster!, target.CanaryService!, spec.DesiredRevision);
            return new DeployObservation
            {
                Status = WorkflowOperationStatus.Succeeded,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = spec.DesiredRevision,
                Message = $"ECS/ALB rollout promoted: canary target group is now serving 100% of traffic on task definition '{spec.DesiredRevision}'."
            };
        }
        catch (AmazonElasticLoadBalancingV2Exception ex)
        {
            Log.StateLookupFailed(logger, operation.OperationId, spec.TargetId, ex.Message);
            activity?.SetStatus(ActivityStatusCode.Error);
            return SanitizedFailure(operation, ex);
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
            await albClient.UpdateListenerRuleWeightsAsync(
                    target.ListenerRuleArn!,
                    BuildWeights(target.CanaryTargetGroupArn!, 0, target.StableTargetGroupArn!, 100),
                    target.Region,
                    cancellationToken)
                .ConfigureAwait(false);

            Log.RollbackRequested(logger, operation.OperationId, spec.TargetId, target.Cluster!, target.CanaryService!);
            return new DeployObservation
            {
                Status = WorkflowOperationStatus.RollbackRequested,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = spec.CurrentRevision,
                Message = $"ECS/ALB rollout rollback requested: stable target group is being shifted to 100% of traffic. Canary service '{target.CanaryService}' will settle on subsequent reconciliations."
            };
        }
        catch (AmazonElasticLoadBalancingV2Exception ex)
        {
            Log.StateLookupFailed(logger, operation.OperationId, spec.TargetId, ex.Message);
            activity?.SetStatus(ActivityStatusCode.Error);
            return SanitizedFailure(operation, ex);
        }
    }

    private static DeployObservation SanitizedFailure(WorkflowOperationRecord operation, AmazonServiceException ex)
        => new()
        {
            Status = WorkflowOperationStatus.Failed,
            ProviderOperationId = operation.ProviderOperationId,
            ObservedRevision = operation.ObservedState,
            // Provider error text can contain ARNs, request IDs, account hints, or other
            // internals. The raw error already went to the structured log; the operator
            // surface gets a stable, generic message that points at the log for diagnosis.
            Message = ex is AmazonECSException
                ? "ECS state lookup failed for this rollout. Check the deploy controller logs for the underlying AWS error."
                : "ALB state lookup failed for this rollout. Check the deploy controller logs for the underlying AWS error."
        };

    private static DeploySubmissionResult SanitizedSubmissionFailure(AmazonServiceException ex)
        => new()
        {
            Status = WorkflowOperationStatus.Failed,
            // Mirror the ObserveAsync sanitization contract for the submission path.
            // The raw AWS error is already in the structured log; the durable operation
            // record gets a stable, generic message and operators consult logs for the
            // underlying provider detail.
            Message = ex is AmazonECSException
                ? "ECS state lookup failed for this rollout. Check the deploy controller logs for the underlying AWS error."
                : "ALB state lookup failed for this rollout. Check the deploy controller logs for the underlying AWS error."
        };

    private static AwsAlbTargetGroupWeight[] BuildWeights(
        string canaryTargetGroupArn,
        int canaryWeight,
        string stableTargetGroupArn,
        int stableWeight)
        =>
        [
            new AwsAlbTargetGroupWeight { TargetGroupArn = canaryTargetGroupArn, Weight = canaryWeight },
            new AwsAlbTargetGroupWeight { TargetGroupArn = stableTargetGroupArn, Weight = stableWeight }
        ];

    private static int ResolveWeight(AwsAlbListenerRuleState state, string targetGroupArn)
    {
        foreach (var entry in state.TargetGroupWeights)
        {
            if (string.Equals(entry.TargetGroupArn, targetGroupArn, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Weight;
            }
        }

        return 0;
    }

    private static bool IsCanaryDeploymentSettled(AwsEcsServiceState state)
    {
        // After the listener rule shifts to stable=100 the canary service is no longer
        // receiving traffic, so the rollback is effectively complete once ECS has
        // finished any in-flight rolling update. PendingCount == 0 means the service is
        // at steady state; running tasks may remain warm for the next rollout. An
        // INACTIVE service is a synthetic steady state too — there are no tasks to
        // settle.
        if (string.Equals(state.Status, "INACTIVE", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return state.PendingCount == 0;
    }

    private static AwsEcsAlbDeployTarget ResolveTarget(DeployOperationSpec spec)
    {
        var parameters = spec.Parameters;
        return new AwsEcsAlbDeployTarget
        {
            Region = GetParameter(parameters, "aws.region"),
            Cluster = GetParameter(parameters, "aws.ecs.cluster"),
            CanaryService = GetParameter(parameters, "aws.ecs.canary_service") ?? spec.TargetName,
            ListenerRuleArn = GetParameter(parameters, "aws.alb.listener_rule_arn"),
            CanaryTargetGroupArn = GetParameter(parameters, "aws.alb.canary_target_group_arn"),
            StableTargetGroupArn = GetParameter(parameters, "aws.alb.stable_target_group_arn")
        };
    }

    private static void EnsureValidTarget(AwsEcsAlbDeployTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.Cluster) ||
            string.IsNullOrWhiteSpace(target.CanaryService) ||
            string.IsNullOrWhiteSpace(target.ListenerRuleArn) ||
            string.IsNullOrWhiteSpace(target.CanaryTargetGroupArn) ||
            string.IsNullOrWhiteSpace(target.StableTargetGroupArn))
        {
            throw new InvalidOperationException("AWS ECS/ALB deploy target is missing cluster, canary service, listener rule, or target group metadata.");
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
        var rawValue = GetParameter(parameters, "aws.ecs.canary_weight_percentage")
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
            error = "AWS ECS/ALB canary weight must be an integer percentage between 1 and 99.";
            return false;
        }

        if (parsedPercentage <= 0 || parsedPercentage >= 100)
        {
            weight = null;
            error = "AWS ECS/ALB canary weight must be greater than 0 and less than 100 percent.";
            return false;
        }

        weight = parsedPercentage;
        error = null;
        return true;
    }

    private static Activity? StartActivity(string activityName, WorkflowOperationRecord operation, AwsEcsAlbDeployTarget target)
    {
        var activity = HonuaTelemetry.ActivitySource.StartActivity(activityName, ActivityKind.Internal);
        if (activity == null)
        {
            return null;
        }

        activity.SetTag(HonuaTelemetry.Tags.Operation, AdapterBackendName);
        activity.SetTag(ControlPlaneTelemetry.Tags.Backend, AdapterBackendName);
        activity.SetTag(ControlPlaneTelemetry.Tags.TargetKind, DeployTargetKind.AwsEcs.ToString());
        activity.SetTag("honua.controlplane.operation_id", operation.OperationId);
        if (operation.Deploy is { } deploy)
        {
            activity.SetTag("honua.controlplane.target_id", deploy.TargetId);
        }

        if (!string.IsNullOrWhiteSpace(target.Cluster))
        {
            activity.SetTag("honua.controlplane.aws.ecs.cluster", target.Cluster);
        }

        if (!string.IsNullOrWhiteSpace(target.CanaryService))
        {
            activity.SetTag("honua.controlplane.aws.ecs.canary_service", target.CanaryService);
        }

        return activity;
    }

    private sealed record AwsEcsAlbDeployTarget
    {
        public string? Region { get; init; }

        public string? Cluster { get; init; }

        public string? CanaryService { get; init; }

        public string? ListenerRuleArn { get; init; }

        public string? CanaryTargetGroupArn { get; init; }

        public string? StableTargetGroupArn { get; init; }
    }

    private static partial class Log
    {
        [LoggerMessage(9089, LogLevel.Information, "Submitted ECS/ALB deploy workflow operation {OperationId} for target {TargetId} ({Cluster}:{CanaryService}) to task definition {DesiredRevision}")]
        public static partial void OperationSubmitted(ILogger logger, string operationId, string targetId, string cluster, string canaryService, string desiredRevision);

        [LoggerMessage(9090, LogLevel.Warning, "Rollback requested for ECS/ALB workflow operation {OperationId} targeting {TargetId} ({Cluster}:{CanaryService})")]
        public static partial void RollbackRequested(ILogger logger, string operationId, string targetId, string cluster, string canaryService);

        [LoggerMessage(9091, LogLevel.Information, "Promotion completed for ECS/ALB workflow operation {OperationId} targeting {TargetId} ({Cluster}:{CanaryService}) to task definition {DesiredRevision}")]
        public static partial void PromotionCompleted(ILogger logger, string operationId, string targetId, string cluster, string canaryService, string desiredRevision);

        [LoggerMessage(9092, LogLevel.Warning, "ECS/ALB state lookup failed for workflow operation {OperationId} targeting {TargetId}: {ErrorMessage}")]
        public static partial void StateLookupFailed(ILogger logger, string operationId, string targetId, string errorMessage);

        [LoggerMessage(9093, LogLevel.Debug, "Observed ECS/ALB canary service {CanaryService}: running={RunningCount} desired={DesiredCount} canaryWeight={CanaryWeight}")]
        public static partial void StateObserved(ILogger logger, string canaryService, int runningCount, int desiredCount, int canaryWeight);

        [LoggerMessage(9094, LogLevel.Warning, "ECS/ALB submission failed for workflow operation {OperationId} targeting {TargetId}: {ErrorMessage}")]
        public static partial void SubmissionFailed(ILogger logger, string operationId, string targetId, string errorMessage);
    }
}
