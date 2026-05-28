// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Amazon.ECS;
using Amazon.ElasticLoadBalancingV2;
using Amazon.Runtime;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.ControlPlane;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class AwsEcsAlbDeployBackendTests
{
    private const string Cluster = "honua-prod-ecs";
    private const string CanaryService = "honua-prod-canary";
    private const string ListenerRuleArn = "arn:aws:elasticloadbalancing:us-east-1:123456789012:listener-rule/app/honua-prod/abc/def/123";
    private const string CanaryTargetGroupArn = "arn:aws:elasticloadbalancing:us-east-1:123456789012:targetgroup/honua-canary/abcdef0123";
    private const string StableTargetGroupArn = "arn:aws:elasticloadbalancing:us-east-1:123456789012:targetgroup/honua-stable/0123abcdef";
    private const string TaskDefArn = "arn:aws:ecs:us-east-1:123456789012:task-definition/honua-app:42";
    private const string PreviousTaskDefArn = "arn:aws:ecs:us-east-1:123456789012:task-definition/honua-app:41";

    [Fact]
    public async Task PlanAsync_MissingCluster_HasBlockingReason()
    {
        var backend = CreateBackend();

        var plan = await backend.PlanAsync(CreateSpec(parameters: BaseParameters(includeCluster: false)));

        plan.IsReadyToSubmit.Should().BeFalse();
        plan.BlockingReasons.Should().Contain(reason => reason.Contains("aws.ecs.cluster", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlanAsync_MissingListenerRuleArn_HasBlockingReason()
    {
        var backend = CreateBackend();

        var parameters = BaseParameters();
        parameters.Remove("aws.alb.listener_rule_arn");

        var plan = await backend.PlanAsync(CreateSpec(parameters: parameters));

        plan.IsReadyToSubmit.Should().BeFalse();
        plan.BlockingReasons.Should().Contain(reason => reason.Contains("aws.alb.listener_rule_arn", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlanAsync_CanaryWeightWithoutTelemetryConnection_HasBlockingReason()
    {
        var backend = CreateBackend();

        var parameters = BaseParameters();
        parameters["deployment.canary_weight_percentage"] = "10";

        var plan = await backend.PlanAsync(CreateSpec(parameters: parameters));

        plan.IsReadyToSubmit.Should().BeFalse();
        plan.BlockingReasons.Should().Contain(reason => reason.Contains("telemetry.connection", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlanAsync_ValidImmediateParameters_IsReadyToSubmit()
    {
        var backend = CreateBackend();

        var plan = await backend.PlanAsync(CreateSpec());

        plan.IsReadyToSubmit.Should().BeTrue();
        plan.BlockingReasons.Should().BeEmpty();
    }

    [Fact]
    public async Task PlanAsync_ValidCanaryParameters_IsReadyToSubmit()
    {
        var backend = CreateBackend();

        var plan = await backend.PlanAsync(CreateSpec(parameters: CanaryParameters()));

        plan.IsReadyToSubmit.Should().BeTrue();
        plan.BlockingReasons.Should().BeEmpty();
    }

    [Fact]
    public async Task StartAsync_WithCanaryWeight_SetsInitialWeights()
    {
        var albClient = new StubAwsAlbClient();
        var ecsClient = new StubAwsEcsClient
        {
            ServiceState = new AwsEcsServiceState
            {
                ServiceName = CanaryService,
                TaskDefinitionArn = PreviousTaskDefArn,
                RunningCount = 2,
                DesiredCount = 2,
                PendingCount = 0,
                Status = "ACTIVE"
            }
        };
        var backend = CreateBackend(albClient, ecsClient);

        var submission = await backend.StartAsync(CreateOperation(parameters: CanaryParameters()));

        submission.Status.Should().Be(WorkflowOperationStatus.Submitted);
        submission.ProviderOperationId.Should().Be($"{Cluster}:{CanaryService}");
        submission.ObservedRevision.Should().Be(PreviousTaskDefArn);
        submission.Message.Should().Contain("10%");
        ecsClient.LastUpdateTaskDefinitionArn.Should().Be(TaskDefArn);
        albClient.LastWeights.Should().NotBeNull();
        albClient.LastWeights!.Single(w => w.TargetGroupArn == CanaryTargetGroupArn).Weight.Should().Be(10);
        albClient.LastWeights.Single(w => w.TargetGroupArn == StableTargetGroupArn).Weight.Should().Be(90);
    }

    [Fact]
    public async Task StartAsync_WithoutCanaryWeight_ImmediateCutover()
    {
        var albClient = new StubAwsAlbClient();
        var ecsClient = new StubAwsEcsClient
        {
            ServiceState = new AwsEcsServiceState
            {
                ServiceName = CanaryService,
                TaskDefinitionArn = PreviousTaskDefArn,
                RunningCount = 2,
                DesiredCount = 2,
                PendingCount = 0,
                Status = "ACTIVE"
            }
        };
        var backend = CreateBackend(albClient, ecsClient);

        var submission = await backend.StartAsync(CreateOperation());

        submission.Status.Should().Be(WorkflowOperationStatus.Submitted);
        submission.Message.Should().Contain("100%");
        albClient.LastWeights.Should().NotBeNull();
        albClient.LastWeights!.Single(w => w.TargetGroupArn == CanaryTargetGroupArn).Weight.Should().Be(100);
        albClient.LastWeights.Single(w => w.TargetGroupArn == StableTargetGroupArn).Weight.Should().Be(0);
    }

    [Fact]
    public async Task ObserveAsync_CanaryConvergedAtInitialWeight_RecommendsPromotion()
    {
        var albClient = new StubAwsAlbClient
        {
            RuleState = new AwsAlbListenerRuleState
            {
                ListenerRuleArn = ListenerRuleArn,
                TargetGroupWeights =
                [
                    new AwsAlbTargetGroupWeight { TargetGroupArn = CanaryTargetGroupArn, Weight = 10 },
                    new AwsAlbTargetGroupWeight { TargetGroupArn = StableTargetGroupArn, Weight = 90 }
                ]
            }
        };
        var ecsClient = new StubAwsEcsClient
        {
            ServiceState = new AwsEcsServiceState
            {
                ServiceName = CanaryService,
                TaskDefinitionArn = TaskDefArn,
                RunningCount = 2,
                DesiredCount = 2,
                PendingCount = 0,
                Status = "ACTIVE"
            }
        };
        var backend = CreateBackend(albClient, ecsClient);

        var observation = await backend.ObserveAsync(CreateOperation(
            parameters: CanaryParameters(),
            status: WorkflowOperationStatus.Reconciling));

        observation.Status.Should().Be(WorkflowOperationStatus.Reconciling);
        observation.PromotionRecommended.Should().BeTrue();
        observation.ObservedRevision.Should().Be(TaskDefArn);
    }

    [Fact]
    public async Task ObserveAsync_CanaryFullyShifted_ReturnsSucceeded()
    {
        var albClient = new StubAwsAlbClient
        {
            RuleState = new AwsAlbListenerRuleState
            {
                ListenerRuleArn = ListenerRuleArn,
                TargetGroupWeights =
                [
                    new AwsAlbTargetGroupWeight { TargetGroupArn = CanaryTargetGroupArn, Weight = 100 },
                    new AwsAlbTargetGroupWeight { TargetGroupArn = StableTargetGroupArn, Weight = 0 }
                ]
            }
        };
        var ecsClient = new StubAwsEcsClient
        {
            ServiceState = new AwsEcsServiceState
            {
                ServiceName = CanaryService,
                TaskDefinitionArn = TaskDefArn,
                RunningCount = 2,
                DesiredCount = 2,
                PendingCount = 0,
                Status = "ACTIVE"
            }
        };
        var backend = CreateBackend(albClient, ecsClient);

        var observation = await backend.ObserveAsync(CreateOperation(status: WorkflowOperationStatus.Reconciling));

        observation.Status.Should().Be(WorkflowOperationStatus.Succeeded);
        observation.ObservedRevision.Should().Be(TaskDefArn);
    }

    [Fact]
    public async Task ObserveAsync_RollbackRequested_StableFullWeight_ReturnsRolledBack()
    {
        var albClient = new StubAwsAlbClient
        {
            RuleState = new AwsAlbListenerRuleState
            {
                ListenerRuleArn = ListenerRuleArn,
                TargetGroupWeights =
                [
                    new AwsAlbTargetGroupWeight { TargetGroupArn = CanaryTargetGroupArn, Weight = 0 },
                    new AwsAlbTargetGroupWeight { TargetGroupArn = StableTargetGroupArn, Weight = 100 }
                ]
            }
        };
        var ecsClient = new StubAwsEcsClient
        {
            ServiceState = new AwsEcsServiceState
            {
                ServiceName = CanaryService,
                TaskDefinitionArn = TaskDefArn,
                RunningCount = 0,
                DesiredCount = 0,
                PendingCount = 0,
                Status = "ACTIVE"
            }
        };
        var backend = CreateBackend(albClient, ecsClient);

        var observation = await backend.ObserveAsync(CreateOperation(status: WorkflowOperationStatus.RollbackRequested));

        observation.Status.Should().Be(WorkflowOperationStatus.RolledBack);
    }

    [Fact]
    public async Task ObserveAsync_RollbackRequested_WarmCanaryAtSteadyState_ReturnsRolledBack()
    {
        var albClient = new StubAwsAlbClient
        {
            RuleState = new AwsAlbListenerRuleState
            {
                ListenerRuleArn = ListenerRuleArn,
                TargetGroupWeights =
                [
                    new AwsAlbTargetGroupWeight { TargetGroupArn = CanaryTargetGroupArn, Weight = 0 },
                    new AwsAlbTargetGroupWeight { TargetGroupArn = StableTargetGroupArn, Weight = 100 }
                ]
            }
        };
        var ecsClient = new StubAwsEcsClient
        {
            ServiceState = new AwsEcsServiceState
            {
                ServiceName = CanaryService,
                TaskDefinitionArn = TaskDefArn,
                RunningCount = 2,
                DesiredCount = 2,
                PendingCount = 0,
                Status = "ACTIVE"
            }
        };
        var backend = CreateBackend(albClient, ecsClient);

        var observation = await backend.ObserveAsync(CreateOperation(status: WorkflowOperationStatus.RollbackRequested));

        observation.Status.Should().Be(WorkflowOperationStatus.RolledBack);
        observation.Message.Should().Contain("no pending deployment");
    }

    [Fact]
    public async Task ObserveAsync_RollbackRequested_CanaryStillRolling_RemainsRollbackRequested()
    {
        var albClient = new StubAwsAlbClient
        {
            RuleState = new AwsAlbListenerRuleState
            {
                ListenerRuleArn = ListenerRuleArn,
                TargetGroupWeights =
                [
                    new AwsAlbTargetGroupWeight { TargetGroupArn = CanaryTargetGroupArn, Weight = 0 },
                    new AwsAlbTargetGroupWeight { TargetGroupArn = StableTargetGroupArn, Weight = 100 }
                ]
            }
        };
        var ecsClient = new StubAwsEcsClient
        {
            ServiceState = new AwsEcsServiceState
            {
                ServiceName = CanaryService,
                TaskDefinitionArn = TaskDefArn,
                RunningCount = 1,
                DesiredCount = 2,
                PendingCount = 1,
                Status = "ACTIVE"
            }
        };
        var backend = CreateBackend(albClient, ecsClient);

        var observation = await backend.ObserveAsync(CreateOperation(status: WorkflowOperationStatus.RollbackRequested));

        observation.Status.Should().Be(WorkflowOperationStatus.RollbackRequested);
    }

    [Fact]
    public async Task ObserveAsync_FullCanaryWeight_TaskDefinitionMismatch_DoesNotSucceed()
    {
        var albClient = new StubAwsAlbClient
        {
            RuleState = new AwsAlbListenerRuleState
            {
                ListenerRuleArn = ListenerRuleArn,
                TargetGroupWeights =
                [
                    new AwsAlbTargetGroupWeight { TargetGroupArn = CanaryTargetGroupArn, Weight = 100 },
                    new AwsAlbTargetGroupWeight { TargetGroupArn = StableTargetGroupArn, Weight = 0 }
                ]
            }
        };
        var ecsClient = new StubAwsEcsClient
        {
            ServiceState = new AwsEcsServiceState
            {
                ServiceName = CanaryService,
                TaskDefinitionArn = PreviousTaskDefArn,
                RunningCount = 2,
                DesiredCount = 2,
                PendingCount = 0,
                Status = "ACTIVE"
            }
        };
        var backend = CreateBackend(albClient, ecsClient);

        var observation = await backend.ObserveAsync(CreateOperation(status: WorkflowOperationStatus.Reconciling));

        observation.Status.Should().Be(WorkflowOperationStatus.Reconciling);
        observation.PromotionRecommended.Should().BeFalse();
        observation.Message.Should().Contain("task definition");
        observation.ObservedRevision.Should().Be(PreviousTaskDefArn);
    }

    [Fact]
    public async Task ObserveAsync_CanaryWeightMatch_TaskDefinitionMismatch_DoesNotRecommendPromotion()
    {
        var albClient = new StubAwsAlbClient
        {
            RuleState = new AwsAlbListenerRuleState
            {
                ListenerRuleArn = ListenerRuleArn,
                TargetGroupWeights =
                [
                    new AwsAlbTargetGroupWeight { TargetGroupArn = CanaryTargetGroupArn, Weight = 10 },
                    new AwsAlbTargetGroupWeight { TargetGroupArn = StableTargetGroupArn, Weight = 90 }
                ]
            }
        };
        var ecsClient = new StubAwsEcsClient
        {
            ServiceState = new AwsEcsServiceState
            {
                ServiceName = CanaryService,
                TaskDefinitionArn = PreviousTaskDefArn,
                RunningCount = 2,
                DesiredCount = 2,
                PendingCount = 0,
                Status = "ACTIVE"
            }
        };
        var backend = CreateBackend(albClient, ecsClient);

        var observation = await backend.ObserveAsync(CreateOperation(
            parameters: CanaryParameters(),
            status: WorkflowOperationStatus.Reconciling));

        observation.Status.Should().Be(WorkflowOperationStatus.Reconciling);
        observation.PromotionRecommended.Should().BeFalse();
        observation.Message.Should().Contain("task definition");
    }

    [Fact]
    public async Task PromoteAsync_SetsCanaryToFullWeight_ReturnsSucceeded()
    {
        var albClient = new StubAwsAlbClient();
        var backend = CreateBackend(albClient);

        var observation = await backend.PromoteAsync(CreateOperation(parameters: CanaryParameters()));

        observation.Status.Should().Be(WorkflowOperationStatus.Succeeded);
        observation.ObservedRevision.Should().Be(TaskDefArn);
        albClient.LastWeights.Should().NotBeNull();
        albClient.LastWeights!.Single(w => w.TargetGroupArn == CanaryTargetGroupArn).Weight.Should().Be(100);
        albClient.LastWeights.Single(w => w.TargetGroupArn == StableTargetGroupArn).Weight.Should().Be(0);
    }

    [Fact]
    public async Task RollbackAsync_SetsStableToFullWeight_ReturnsRollbackRequested()
    {
        var albClient = new StubAwsAlbClient();
        var backend = CreateBackend(albClient);

        var observation = await backend.RollbackAsync(CreateOperation(currentRevision: PreviousTaskDefArn, parameters: CanaryParameters()));

        observation.Status.Should().Be(WorkflowOperationStatus.RollbackRequested);
        observation.ObservedRevision.Should().Be(PreviousTaskDefArn);
        albClient.LastWeights.Should().NotBeNull();
        albClient.LastWeights!.Single(w => w.TargetGroupArn == CanaryTargetGroupArn).Weight.Should().Be(0);
        albClient.LastWeights.Single(w => w.TargetGroupArn == StableTargetGroupArn).Weight.Should().Be(100);
    }

    [Fact]
    public async Task StartAsync_EcsException_ReturnsFailedWithoutLeakingProviderDetail()
    {
        var ecsClient = new StubAwsEcsClient
        {
            DescribeException = new AmazonECSException("ServiceNotFoundException: secret-cluster-token=abcdef")
        };
        var backend = CreateBackend(ecsClient: ecsClient);

        var submission = await backend.StartAsync(CreateOperation(parameters: CanaryParameters()));

        submission.Status.Should().Be(WorkflowOperationStatus.Failed);
        submission.Message.Should().NotBeNullOrEmpty();
        submission.Message.Should().NotContain("secret-cluster-token");
        submission.Message.Should().NotContain("abcdef");
    }

    [Fact]
    public async Task StartAsync_AlbUpdateException_ReturnsFailedWithoutLeakingProviderDetail()
    {
        var albClient = new StubAwsAlbClient
        {
            UpdateException = new AmazonElasticLoadBalancingV2Exception("AccessDenied: arn=arn:aws:elasticloadbalancing:us-east-1:secret-account:rule/abc")
        };
        var backend = CreateBackend(albClient);

        var submission = await backend.StartAsync(CreateOperation(parameters: CanaryParameters()));

        submission.Status.Should().Be(WorkflowOperationStatus.Failed);
        submission.Message.Should().NotBeNullOrEmpty();
        submission.Message.Should().NotContain("secret-account");
        submission.Message.Should().NotContain("AccessDenied");
    }

    [Fact]
    public async Task PromoteAsync_AlbException_ReturnsFailedWithoutLeakingProviderDetail()
    {
        var albClient = new StubAwsAlbClient
        {
            UpdateException = new AmazonElasticLoadBalancingV2Exception("ValidationError: secret-rule-token=zzz")
        };
        var backend = CreateBackend(albClient);

        var observation = await backend.PromoteAsync(CreateOperation(parameters: CanaryParameters()));

        observation.Status.Should().Be(WorkflowOperationStatus.Failed);
        observation.Message.Should().NotBeNullOrEmpty();
        observation.Message.Should().NotContain("secret-rule-token");
    }

    [Fact]
    public async Task RollbackAsync_AlbException_ReturnsFailedWithoutLeakingProviderDetail()
    {
        var albClient = new StubAwsAlbClient
        {
            UpdateException = new AmazonElasticLoadBalancingV2Exception("ThrottlingException: secret-rule-token=zzz")
        };
        var backend = CreateBackend(albClient);

        var observation = await backend.RollbackAsync(CreateOperation(parameters: CanaryParameters()));

        observation.Status.Should().Be(WorkflowOperationStatus.Failed);
        observation.Message.Should().NotBeNullOrEmpty();
        observation.Message.Should().NotContain("secret-rule-token");
    }

    [Fact]
    public async Task ObserveAsync_AlbException_ReturnsFailedWithoutLeakingProviderDetail()
    {
        var albClient = new StubAwsAlbClient
        {
            DescribeException = new AmazonElasticLoadBalancingV2Exception("ValidationError: rule arn=secret-token::sub abcdef")
        };
        var ecsClient = new StubAwsEcsClient
        {
            ServiceState = new AwsEcsServiceState
            {
                ServiceName = CanaryService,
                TaskDefinitionArn = TaskDefArn,
                RunningCount = 2,
                DesiredCount = 2
            }
        };
        var backend = CreateBackend(albClient, ecsClient);

        var observation = await backend.ObserveAsync(CreateOperation());

        observation.Status.Should().Be(WorkflowOperationStatus.Failed);
        observation.Message.Should().NotBeNullOrEmpty();
        observation.Message.Should().NotContain("secret-token");
        observation.Message.Should().NotContain("abcdef");
    }

    [Fact]
    public async Task ObserveAsync_FullCanaryWeight_StableAlsoFullWeight_DoesNotSucceed()
    {
        // ALB target-group weights are relative; if both target groups carry
        // weight=100 traffic still splits 50/50. The deploy controller writes
        // pairs that sum to 100, so an observation that does not normalize
        // cannot be treated as a stable rollout endpoint.
        var albClient = new StubAwsAlbClient
        {
            RuleState = new AwsAlbListenerRuleState
            {
                ListenerRuleArn = ListenerRuleArn,
                TargetGroupWeights =
                [
                    new AwsAlbTargetGroupWeight { TargetGroupArn = CanaryTargetGroupArn, Weight = 100 },
                    new AwsAlbTargetGroupWeight { TargetGroupArn = StableTargetGroupArn, Weight = 100 }
                ]
            }
        };
        var ecsClient = new StubAwsEcsClient
        {
            ServiceState = ConvergedServiceState(TaskDefArn)
        };
        var backend = CreateBackend(albClient, ecsClient);

        var observation = await backend.ObserveAsync(CreateOperation(status: WorkflowOperationStatus.Reconciling));

        observation.Status.Should().Be(WorkflowOperationStatus.Reconciling);
    }

    [Fact]
    public async Task ObserveAsync_CanaryWeightWithUnexpectedTargetGroup_DoesNotRecommendPromotion()
    {
        // A third target group with non-zero weight breaks the canary/stable
        // contract because traffic could leak to a target the controller does
        // not own.
        var albClient = new StubAwsAlbClient
        {
            RuleState = new AwsAlbListenerRuleState
            {
                ListenerRuleArn = ListenerRuleArn,
                TargetGroupWeights =
                [
                    new AwsAlbTargetGroupWeight { TargetGroupArn = CanaryTargetGroupArn, Weight = 10 },
                    new AwsAlbTargetGroupWeight { TargetGroupArn = StableTargetGroupArn, Weight = 90 },
                    new AwsAlbTargetGroupWeight { TargetGroupArn = "arn:aws:elasticloadbalancing:us-east-1:123456789012:targetgroup/honua-rogue/aaa", Weight = 5 }
                ]
            }
        };
        var ecsClient = new StubAwsEcsClient
        {
            ServiceState = ConvergedServiceState(TaskDefArn)
        };
        var backend = CreateBackend(albClient, ecsClient);

        var observation = await backend.ObserveAsync(CreateOperation(
            parameters: CanaryParameters(),
            status: WorkflowOperationStatus.Reconciling));

        observation.Status.Should().Be(WorkflowOperationStatus.Reconciling);
        observation.PromotionRecommended.Should().BeFalse();
    }

    [Fact]
    public async Task ObserveAsync_PrimaryDeploymentRolloutInProgress_DoesNotSucceed()
    {
        var albClient = new StubAwsAlbClient
        {
            RuleState = new AwsAlbListenerRuleState
            {
                ListenerRuleArn = ListenerRuleArn,
                TargetGroupWeights =
                [
                    new AwsAlbTargetGroupWeight { TargetGroupArn = CanaryTargetGroupArn, Weight = 100 },
                    new AwsAlbTargetGroupWeight { TargetGroupArn = StableTargetGroupArn, Weight = 0 }
                ]
            }
        };
        var ecsClient = new StubAwsEcsClient
        {
            ServiceState = new AwsEcsServiceState
            {
                ServiceName = CanaryService,
                TaskDefinitionArn = TaskDefArn,
                RunningCount = 2,
                DesiredCount = 2,
                PendingCount = 0,
                Status = "ACTIVE",
                Deployments =
                [
                    new AwsEcsDeploymentState
                    {
                        DeploymentId = "primary",
                        Status = "PRIMARY",
                        TaskDefinitionArn = TaskDefArn,
                        RolloutState = "IN_PROGRESS",
                        RunningCount = 2,
                        DesiredCount = 2,
                        PendingCount = 0
                    }
                ]
            }
        };
        var backend = CreateBackend(albClient, ecsClient);

        var observation = await backend.ObserveAsync(CreateOperation(status: WorkflowOperationStatus.Reconciling));

        observation.Status.Should().Be(WorkflowOperationStatus.Reconciling);
        observation.Message.Should().Contain("IN_PROGRESS");
    }

    [Fact]
    public async Task ObserveAsync_OldActiveDeploymentStillRunning_DoesNotSucceed()
    {
        var albClient = new StubAwsAlbClient
        {
            RuleState = new AwsAlbListenerRuleState
            {
                ListenerRuleArn = ListenerRuleArn,
                TargetGroupWeights =
                [
                    new AwsAlbTargetGroupWeight { TargetGroupArn = CanaryTargetGroupArn, Weight = 100 },
                    new AwsAlbTargetGroupWeight { TargetGroupArn = StableTargetGroupArn, Weight = 0 }
                ]
            }
        };
        var ecsClient = new StubAwsEcsClient
        {
            ServiceState = new AwsEcsServiceState
            {
                ServiceName = CanaryService,
                TaskDefinitionArn = TaskDefArn,
                RunningCount = 4,
                DesiredCount = 2,
                PendingCount = 0,
                Status = "ACTIVE",
                Deployments =
                [
                    new AwsEcsDeploymentState
                    {
                        DeploymentId = "primary",
                        Status = "PRIMARY",
                        TaskDefinitionArn = TaskDefArn,
                        RolloutState = "COMPLETED",
                        RunningCount = 2,
                        DesiredCount = 2,
                        PendingCount = 0
                    },
                    new AwsEcsDeploymentState
                    {
                        DeploymentId = "previous",
                        Status = "ACTIVE",
                        TaskDefinitionArn = PreviousTaskDefArn,
                        RolloutState = "COMPLETED",
                        RunningCount = 2,
                        DesiredCount = 0,
                        PendingCount = 0
                    }
                ]
            }
        };
        var backend = CreateBackend(albClient, ecsClient);

        var observation = await backend.ObserveAsync(CreateOperation(status: WorkflowOperationStatus.Reconciling));

        observation.Status.Should().Be(WorkflowOperationStatus.Reconciling);
        observation.Message.Should().Contain("ACTIVE deployment");
    }

    [Fact]
    public async Task ObserveAsync_PrimaryConvergedNoLingeringActive_ReturnsSucceeded()
    {
        var albClient = new StubAwsAlbClient
        {
            RuleState = new AwsAlbListenerRuleState
            {
                ListenerRuleArn = ListenerRuleArn,
                TargetGroupWeights =
                [
                    new AwsAlbTargetGroupWeight { TargetGroupArn = CanaryTargetGroupArn, Weight = 100 },
                    new AwsAlbTargetGroupWeight { TargetGroupArn = StableTargetGroupArn, Weight = 0 }
                ]
            }
        };
        var ecsClient = new StubAwsEcsClient
        {
            ServiceState = new AwsEcsServiceState
            {
                ServiceName = CanaryService,
                TaskDefinitionArn = TaskDefArn,
                RunningCount = 2,
                DesiredCount = 2,
                PendingCount = 0,
                Status = "ACTIVE",
                Deployments =
                [
                    new AwsEcsDeploymentState
                    {
                        DeploymentId = "primary",
                        Status = "PRIMARY",
                        TaskDefinitionArn = TaskDefArn,
                        RolloutState = "COMPLETED",
                        RunningCount = 2,
                        DesiredCount = 2,
                        PendingCount = 0
                    },
                    new AwsEcsDeploymentState
                    {
                        DeploymentId = "drained",
                        Status = "ACTIVE",
                        TaskDefinitionArn = PreviousTaskDefArn,
                        RolloutState = "COMPLETED",
                        RunningCount = 0,
                        DesiredCount = 0,
                        PendingCount = 0
                    }
                ]
            }
        };
        var backend = CreateBackend(albClient, ecsClient);

        var observation = await backend.ObserveAsync(CreateOperation(status: WorkflowOperationStatus.Reconciling));

        observation.Status.Should().Be(WorkflowOperationStatus.Succeeded);
    }

    [Fact]
    public async Task ObserveAsync_NoPrimaryDeploymentReturned_DoesNotSucceed()
    {
        var albClient = new StubAwsAlbClient
        {
            RuleState = new AwsAlbListenerRuleState
            {
                ListenerRuleArn = ListenerRuleArn,
                TargetGroupWeights =
                [
                    new AwsAlbTargetGroupWeight { TargetGroupArn = CanaryTargetGroupArn, Weight = 100 },
                    new AwsAlbTargetGroupWeight { TargetGroupArn = StableTargetGroupArn, Weight = 0 }
                ]
            }
        };
        var ecsClient = new StubAwsEcsClient
        {
            ServiceState = new AwsEcsServiceState
            {
                ServiceName = CanaryService,
                TaskDefinitionArn = TaskDefArn,
                RunningCount = 2,
                DesiredCount = 2,
                PendingCount = 0,
                Status = "ACTIVE",
                Deployments =
                [
                    new AwsEcsDeploymentState
                    {
                        DeploymentId = "active-only",
                        Status = "ACTIVE",
                        TaskDefinitionArn = PreviousTaskDefArn,
                        RolloutState = "COMPLETED",
                        RunningCount = 2,
                        DesiredCount = 2,
                        PendingCount = 0
                    }
                ]
            }
        };
        var backend = CreateBackend(albClient, ecsClient);

        var observation = await backend.ObserveAsync(CreateOperation(status: WorkflowOperationStatus.Reconciling));

        observation.Status.Should().Be(WorkflowOperationStatus.Reconciling);
        observation.Message.Should().Contain("PRIMARY");
    }

    [Fact]
    public async Task StartAsync_AmazonClientException_ReturnsFailedWithoutLeakingProviderDetail()
    {
        // Credential resolution / profile lookup failures surface as
        // AmazonClientException, which in AWS SDK v4 is a sibling of
        // AmazonServiceException rather than a base. The unified catch must
        // sanitise both branches before DeployWorkflowService persists them.
        var ecsClient = new StubAwsEcsClient
        {
            DescribeException = new AmazonClientException("Unable to load credentials from profile [secret-profile-token=xyz]")
        };
        var backend = CreateBackend(ecsClient: ecsClient);

        var submission = await backend.StartAsync(CreateOperation(parameters: CanaryParameters()));

        submission.Status.Should().Be(WorkflowOperationStatus.Failed);
        submission.Message.Should().NotContain("secret-profile-token");
        submission.Message.Should().NotContain("xyz");
    }

    [Fact]
    public async Task ObserveAsync_AmazonClientException_ReturnsFailedWithoutLeakingProviderDetail()
    {
        var albClient = new StubAwsAlbClient
        {
            DescribeException = new AmazonClientException("InstanceMetadataServiceUnavailable: secret-imds-host=169.254.169.254/zzz")
        };
        var backend = CreateBackend(albClient);

        var observation = await backend.ObserveAsync(CreateOperation());

        observation.Status.Should().Be(WorkflowOperationStatus.Failed);
        observation.Message.Should().NotContain("secret-imds-host");
        observation.Message.Should().NotContain("169.254");
    }

    [Fact]
    public async Task PromoteAsync_AmazonClientException_ReturnsFailedWithoutLeakingProviderDetail()
    {
        var albClient = new StubAwsAlbClient
        {
            UpdateException = new AmazonClientException("STS AssumeRole failed: secret-role-arn")
        };
        var backend = CreateBackend(albClient);

        var observation = await backend.PromoteAsync(CreateOperation(parameters: CanaryParameters()));

        observation.Status.Should().Be(WorkflowOperationStatus.Failed);
        observation.Message.Should().NotContain("secret-role-arn");
    }

    [Fact]
    public async Task RollbackAsync_AmazonClientException_ReturnsFailedWithoutLeakingProviderDetail()
    {
        var albClient = new StubAwsAlbClient
        {
            UpdateException = new AmazonClientException("Endpoint resolution failed: secret-endpoint-arn")
        };
        var backend = CreateBackend(albClient);

        var observation = await backend.RollbackAsync(CreateOperation(parameters: CanaryParameters()));

        observation.Status.Should().Be(WorkflowOperationStatus.Failed);
        observation.Message.Should().NotContain("secret-endpoint-arn");
    }

    [Fact]
    public async Task ObserveAsync_EcsException_ReturnsFailed()
    {
        var ecsClient = new StubAwsEcsClient
        {
            DescribeException = new AmazonECSException("ServiceNotFoundException: secret-cluster-name")
        };
        var backend = CreateBackend(ecsClient: ecsClient);

        var observation = await backend.ObserveAsync(CreateOperation());

        observation.Status.Should().Be(WorkflowOperationStatus.Failed);
        observation.Message.Should().NotBeNullOrEmpty();
        observation.Message.Should().NotContain("secret-cluster-name");
    }

    [Fact]
    public async Task GetCapabilitiesAsync_AdvertisesTrafficShiftingAndPromotion()
    {
        var backend = CreateBackend();

        var capabilities = await backend.GetCapabilitiesAsync();

        capabilities.SupportsRollback.Should().BeTrue();
        capabilities.SupportsTrafficShifting.Should().BeTrue();
        capabilities.SupportsProgressPolling.Should().BeTrue();
        capabilities.SupportsRevisionPinning.Should().BeTrue();
        capabilities.SupportsCancellation.Should().BeFalse();
        capabilities.RequiresOutOfBandMigrations.Should().BeTrue();
    }

    [Fact]
    public void BackendName_AndTargetKind_AreStable()
    {
        var backend = CreateBackend();

        backend.BackendName.Should().Be("honua-aws-ecs-alb");
        backend.TargetKind.Should().Be(DeployTargetKind.AwsEcs);
    }

    private static AwsEcsAlbDeployBackend CreateBackend(
        StubAwsAlbClient? albClient = null,
        StubAwsEcsClient? ecsClient = null)
        => new(
            albClient ?? new StubAwsAlbClient(),
            ecsClient ?? new StubAwsEcsClient(),
            NullLogger<AwsEcsAlbDeployBackend>.Instance);

    private static DeployOperationSpec CreateSpec(
        string desiredRevision = TaskDefArn,
        IReadOnlyDictionary<string, string>? parameters = null)
        => new()
        {
            TargetId = "prod-ecs",
            TargetKind = DeployTargetKind.AwsEcs,
            Backend = "honua-aws-ecs-alb",
            Environment = "production",
            TargetName = CanaryService,
            ArtifactReference = "123456789012.dkr.ecr.us-east-1.amazonaws.com/honua:sha-42",
            DesiredRevision = desiredRevision,
            RequiresOutOfBandMigrations = true,
            Parameters = parameters ?? BaseParameters()
        };

    private static WorkflowOperationRecord CreateOperation(
        string desiredRevision = TaskDefArn,
        string? currentRevision = null,
        WorkflowOperationStatus status = WorkflowOperationStatus.Submitted,
        IReadOnlyDictionary<string, string>? parameters = null)
    {
        var spec = CreateSpec(desiredRevision, parameters) with
        {
            CurrentRevision = currentRevision
        };

        return new WorkflowOperationRecord
        {
            OperationId = $"deploy-{Guid.NewGuid():N}",
            Kind = WorkflowOperationKind.Deploy,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            CurrentPhase = "Testing",
            Audit = new OperationAuditInfo(),
            Concurrency = new OperationConcurrencyPolicy
            {
                PartitionKey = "production:prod-ecs",
                RequiresExclusiveLease = true
            },
            Deploy = spec
        };
    }

    private static Dictionary<string, string> BaseParameters(bool includeCluster = true)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["aws.region"] = "us-east-1",
            ["aws.ecs.canary_service"] = CanaryService,
            ["aws.alb.listener_rule_arn"] = ListenerRuleArn,
            ["aws.alb.canary_target_group_arn"] = CanaryTargetGroupArn,
            ["aws.alb.stable_target_group_arn"] = StableTargetGroupArn
        };

        if (includeCluster)
        {
            parameters["aws.ecs.cluster"] = Cluster;
        }

        return parameters;
    }

    private static Dictionary<string, string> CanaryParameters()
    {
        var parameters = BaseParameters();
        parameters["deployment.canary_weight_percentage"] = "10";
        parameters["telemetry.connection"] = "prod-prom";
        return parameters;
    }

    private static AwsEcsServiceState ConvergedServiceState(string taskDefinitionArn)
        => new()
        {
            ServiceName = CanaryService,
            TaskDefinitionArn = taskDefinitionArn,
            RunningCount = 2,
            DesiredCount = 2,
            PendingCount = 0,
            Status = "ACTIVE",
            Deployments =
            [
                new AwsEcsDeploymentState
                {
                    DeploymentId = "primary",
                    Status = "PRIMARY",
                    TaskDefinitionArn = taskDefinitionArn,
                    RolloutState = "COMPLETED",
                    RunningCount = 2,
                    DesiredCount = 2,
                    PendingCount = 0
                }
            ]
        };

    private sealed class StubAwsAlbClient : IAwsAlbClient
    {
        public AwsAlbListenerRuleState RuleState { get; set; } = new()
        {
            ListenerRuleArn = ListenerRuleArn,
            TargetGroupWeights =
            [
                new AwsAlbTargetGroupWeight { TargetGroupArn = CanaryTargetGroupArn, Weight = 0 },
                new AwsAlbTargetGroupWeight { TargetGroupArn = StableTargetGroupArn, Weight = 100 }
            ]
        };

        public IReadOnlyList<AwsAlbTargetGroupWeight>? LastWeights { get; private set; }

        public Exception? DescribeException { get; set; }

        public Exception? UpdateException { get; set; }

        public Task<AwsAlbListenerRuleState> GetListenerRuleWeightsAsync(
            string ruleArn,
            string? region,
            CancellationToken cancellationToken = default)
        {
            if (DescribeException != null)
            {
                throw DescribeException;
            }

            return Task.FromResult(RuleState);
        }

        public Task<AwsAlbListenerRuleState> UpdateListenerRuleWeightsAsync(
            string ruleArn,
            IReadOnlyList<AwsAlbTargetGroupWeight> weights,
            string? region,
            CancellationToken cancellationToken = default)
        {
            if (UpdateException != null)
            {
                throw UpdateException;
            }

            LastWeights = weights;
            RuleState = new AwsAlbListenerRuleState
            {
                ListenerRuleArn = ruleArn,
                TargetGroupWeights = weights
            };
            return Task.FromResult(RuleState);
        }
    }

    private sealed class StubAwsEcsClient : IAwsEcsClient
    {
        public AwsEcsServiceState ServiceState { get; set; } = new()
        {
            ServiceName = CanaryService,
            TaskDefinitionArn = TaskDefArn,
            RunningCount = 2,
            DesiredCount = 2,
            PendingCount = 0,
            Status = "ACTIVE"
        };

        public string? LastUpdateTaskDefinitionArn { get; private set; }

        public Exception? DescribeException { get; set; }

        public Exception? UpdateException { get; set; }

        public Task<AwsEcsServiceState> DescribeServiceAsync(
            string cluster,
            string serviceName,
            string? region,
            CancellationToken cancellationToken = default)
        {
            if (DescribeException != null)
            {
                throw DescribeException;
            }

            return Task.FromResult(ServiceState);
        }

        public System.Threading.Tasks.Task UpdateServiceTaskDefinitionAsync(
            string cluster,
            string serviceName,
            string taskDefinitionArn,
            string? region,
            CancellationToken cancellationToken = default)
        {
            if (UpdateException != null)
            {
                throw UpdateException;
            }

            LastUpdateTaskDefinitionArn = taskDefinitionArn;
            return System.Threading.Tasks.Task.CompletedTask;
        }
    }
}
