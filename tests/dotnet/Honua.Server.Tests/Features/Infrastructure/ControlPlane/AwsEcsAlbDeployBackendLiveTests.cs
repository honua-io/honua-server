// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Live validation that exercises the AWS ECS/ALB deploy backend against a real
/// AWS environment provisioned by Terraform. Each test requires the full set of
/// HONUA_LIVE_ECS_ALB_* environment variables; missing values cause the test to
/// skip rather than fail. The active path runs the production
/// <see cref="AwsSdkAlbClient"/> and <see cref="AwsSdkEcsClient"/> against the
/// configured cluster, listener rule, target groups, and task definition.
/// </summary>
public sealed class AwsEcsAlbDeployBackendLiveTests
{
    private const string RegionEnv = "HONUA_LIVE_ECS_ALB_REGION";
    private const string ClusterEnv = "HONUA_LIVE_ECS_ALB_CLUSTER";
    private const string CanaryServiceEnv = "HONUA_LIVE_ECS_ALB_CANARY_SERVICE";
    private const string ListenerRuleArnEnv = "HONUA_LIVE_ECS_ALB_LISTENER_RULE_ARN";
    private const string CanaryTargetGroupArnEnv = "HONUA_LIVE_ECS_ALB_CANARY_TARGET_GROUP_ARN";
    private const string StableTargetGroupArnEnv = "HONUA_LIVE_ECS_ALB_STABLE_TARGET_GROUP_ARN";
    private const string TaskDefinitionArnEnv = "HONUA_LIVE_ECS_ALB_TASK_DEFINITION_ARN";
    private const string TelemetryConnectionEnv = "HONUA_LIVE_ECS_ALB_TELEMETRY_CONNECTION";

    [CloudTest(
        ClusterEnv,
        CanaryServiceEnv,
        ListenerRuleArnEnv,
        CanaryTargetGroupArnEnv,
        StableTargetGroupArnEnv,
        TaskDefinitionArnEnv)]
    public async Task ObserveAsync_AgainstProvisionedEcsAlbStack_ReturnsState()
    {
        var operation = BuildOperation();
        var backend = new AwsEcsAlbDeployBackend(
            new AwsSdkAlbClient(),
            new AwsSdkEcsClient(),
            NullLogger<AwsEcsAlbDeployBackend>.Instance);

        var observation = await backend.ObserveAsync(operation);

        observation.Status.Should().BeOneOf(
            WorkflowOperationStatus.Reconciling,
            WorkflowOperationStatus.Succeeded,
            WorkflowOperationStatus.RolledBack,
            WorkflowOperationStatus.RollbackRequested,
            WorkflowOperationStatus.Failed);
        observation.Message.Should().NotBeNullOrWhiteSpace();
    }

    [CloudTest(
        ClusterEnv,
        CanaryServiceEnv,
        ListenerRuleArnEnv,
        CanaryTargetGroupArnEnv,
        StableTargetGroupArnEnv,
        TaskDefinitionArnEnv,
        TelemetryConnectionEnv)]
    public async Task PlanAsync_AgainstProvisionedEcsAlbStack_IsReadyToSubmit()
    {
        var operation = BuildOperation();
        var backend = new AwsEcsAlbDeployBackend(
            new AwsSdkAlbClient(),
            new AwsSdkEcsClient(),
            NullLogger<AwsEcsAlbDeployBackend>.Instance);

        var plan = await backend.PlanAsync(operation.Deploy!);

        plan.IsReadyToSubmit.Should().BeTrue();
        plan.BlockingReasons.Should().BeEmpty();
    }

    private static WorkflowOperationRecord BuildOperation()
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["aws.ecs.cluster"] = Required(ClusterEnv),
            ["aws.ecs.canary_service"] = Required(CanaryServiceEnv),
            ["aws.alb.listener_rule_arn"] = Required(ListenerRuleArnEnv),
            ["aws.alb.canary_target_group_arn"] = Required(CanaryTargetGroupArnEnv),
            ["aws.alb.stable_target_group_arn"] = Required(StableTargetGroupArnEnv)
        };

        var region = Environment.GetEnvironmentVariable(RegionEnv);
        if (!string.IsNullOrWhiteSpace(region))
        {
            parameters["aws.region"] = region;
        }

        var telemetryConnection = Environment.GetEnvironmentVariable(TelemetryConnectionEnv);
        if (!string.IsNullOrWhiteSpace(telemetryConnection))
        {
            parameters["telemetry.connection"] = telemetryConnection;
            parameters["deployment.canary_weight_percentage"] = "10";
        }

        return new WorkflowOperationRecord
        {
            OperationId = $"live-ecs-alb-{Guid.NewGuid():N}",
            Kind = WorkflowOperationKind.Deploy,
            Status = WorkflowOperationStatus.Submitted,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "LiveValidation",
            Audit = new OperationAuditInfo(),
            Concurrency = new OperationConcurrencyPolicy
            {
                PartitionKey = $"live-ecs-alb:{parameters["aws.ecs.canary_service"]}",
                RequiresExclusiveLease = true
            },
            Deploy = new DeployOperationSpec
            {
                TargetId = "live-ecs-alb",
                TargetKind = DeployTargetKind.AwsEcs,
                Backend = "honua-aws-ecs-alb",
                Environment = "live",
                TargetName = parameters["aws.ecs.canary_service"],
                DesiredRevision = Required(TaskDefinitionArnEnv),
                Parameters = parameters
            }
        };
    }

    private static string Required(string envName)
    {
        var value = Environment.GetEnvironmentVariable(envName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Live ECS/ALB validation requires environment variable {envName}.");
        }

        return value.Trim();
    }
}
