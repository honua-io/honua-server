// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Amazon.ElasticLoadBalancingV2;
using Amazon.ElasticLoadBalancingV2.Model;
using FluentAssertions;
using Honua.Server.Features.ControlPlane;
using AlbAction = Amazon.ElasticLoadBalancingV2.Model.Action;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class AwsAlbActionMutatorTests
{
    private const string RuleArn = "arn:aws:elasticloadbalancing:us-east-1:123456789012:listener-rule/app/honua/abc/def/123";
    private const string CanaryTargetGroup = "arn:aws:elasticloadbalancing:us-east-1:123456789012:targetgroup/honua-canary/abc";
    private const string StableTargetGroup = "arn:aws:elasticloadbalancing:us-east-1:123456789012:targetgroup/honua-stable/def";

    [Fact]
    public void RebuildActionsWithUpdatedWeights_PreservesTargetGroupStickinessConfig()
    {
        var stickiness = new TargetGroupStickinessConfig
        {
            Enabled = true,
            DurationSeconds = 600
        };
        var existing = new List<AlbAction>
        {
            new()
            {
                Type = ActionTypeEnum.Forward,
                Order = 1,
                ForwardConfig = new ForwardActionConfig
                {
                    TargetGroups =
                    [
                        new TargetGroupTuple { TargetGroupArn = CanaryTargetGroup, Weight = 0 },
                        new TargetGroupTuple { TargetGroupArn = StableTargetGroup, Weight = 100 }
                    ],
                    TargetGroupStickinessConfig = stickiness
                }
            }
        };

        var rebuilt = AwsAlbActionMutator.RebuildActionsWithUpdatedWeights(
            existing,
            [
                new AwsAlbTargetGroupWeight { TargetGroupArn = CanaryTargetGroup, Weight = 25 },
                new AwsAlbTargetGroupWeight { TargetGroupArn = StableTargetGroup, Weight = 75 }
            ],
            RuleArn);

        rebuilt.Should().HaveCount(1);
        var forward = rebuilt[0];
        forward.Type.Should().Be(ActionTypeEnum.Forward);
        forward.Order.Should().Be(1);
        forward.ForwardConfig.TargetGroupStickinessConfig.Should().BeSameAs(stickiness);
        forward.ForwardConfig.TargetGroups.Should().HaveCount(2);
        forward.ForwardConfig.TargetGroups.Single(t => t.TargetGroupArn == CanaryTargetGroup).Weight.Should().Be(25);
        forward.ForwardConfig.TargetGroups.Single(t => t.TargetGroupArn == StableTargetGroup).Weight.Should().Be(75);
    }

    [Fact]
    public void RebuildActionsWithUpdatedWeights_PreservesSiblingActionOrdering()
    {
        var authAction = new AlbAction
        {
            Type = ActionTypeEnum.AuthenticateCognito,
            Order = 1,
            AuthenticateCognitoConfig = new AuthenticateCognitoActionConfig
            {
                UserPoolArn = "arn:aws:cognito-idp:us-east-1:123456789012:userpool/us-east-1_AbCdEf",
                UserPoolClientId = "client-id",
                UserPoolDomain = "honua-prod"
            }
        };
        var forwardAction = new AlbAction
        {
            Type = ActionTypeEnum.Forward,
            Order = 2,
            ForwardConfig = new ForwardActionConfig
            {
                TargetGroups =
                [
                    new TargetGroupTuple { TargetGroupArn = CanaryTargetGroup, Weight = 5 },
                    new TargetGroupTuple { TargetGroupArn = StableTargetGroup, Weight = 95 }
                ]
            }
        };

        var rebuilt = AwsAlbActionMutator.RebuildActionsWithUpdatedWeights(
            [authAction, forwardAction],
            [
                new AwsAlbTargetGroupWeight { TargetGroupArn = CanaryTargetGroup, Weight = 50 },
                new AwsAlbTargetGroupWeight { TargetGroupArn = StableTargetGroup, Weight = 50 }
            ],
            RuleArn);

        rebuilt.Should().HaveCount(2);
        rebuilt[0].Should().BeSameAs(authAction);
        rebuilt[1].Type.Should().Be(ActionTypeEnum.Forward);
        rebuilt[1].Order.Should().Be(2);
        rebuilt[1].ForwardConfig.TargetGroups.Single(t => t.TargetGroupArn == CanaryTargetGroup).Weight.Should().Be(50);
    }

    [Fact]
    public void RebuildActionsWithUpdatedWeights_NoForwardAction_Throws()
    {
        var redirectOnly = new List<AlbAction>
        {
            new()
            {
                Type = ActionTypeEnum.Redirect,
                Order = 1,
                RedirectConfig = new RedirectActionConfig
                {
                    Protocol = "HTTPS",
                    Port = "443",
                    StatusCode = RedirectActionStatusCodeEnum.HTTP_301
                }
            }
        };

        var act = () => AwsAlbActionMutator.RebuildActionsWithUpdatedWeights(
            redirectOnly,
            [
                new AwsAlbTargetGroupWeight { TargetGroupArn = CanaryTargetGroup, Weight = 0 },
                new AwsAlbTargetGroupWeight { TargetGroupArn = StableTargetGroup, Weight = 100 }
            ],
            RuleArn);

        act.Should()
            .Throw<AmazonElasticLoadBalancingV2Exception>()
            .WithMessage("*forward action*");
    }
}
