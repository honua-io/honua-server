// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Amazon;
using Amazon.ElasticLoadBalancingV2;
using Amazon.ElasticLoadBalancingV2.Model;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.CloudIntegration.Tests;

internal static class AwsEcsAlbCertificationSupport
{
    public const int CanaryShare = 25;
    public const int BaselineStableWeight = 100;
    public const int BaselineCanaryWeight = 0;

    public static IReadOnlyList<AwsAlbTargetGroupWeight> BuildBaselineWeights(
        string stableTargetGroup,
        string canaryTargetGroup)
        =>
        [
            new AwsAlbTargetGroupWeight { TargetGroupArn = stableTargetGroup, Weight = BaselineStableWeight },
            new AwsAlbTargetGroupWeight { TargetGroupArn = canaryTargetGroup, Weight = BaselineCanaryWeight },
        ];

    public static (int Canary, int Stable) ReadShares(
        AwsAlbListenerRuleState state,
        string canaryTargetGroupArn,
        string stableTargetGroupArn)
    {
        var canary = 0;
        var stable = 0;
        foreach (var weight in state.TargetGroupWeights)
        {
            if (string.Equals(weight.TargetGroupArn, canaryTargetGroupArn, StringComparison.OrdinalIgnoreCase))
            {
                canary = weight.Weight;
            }
            else if (string.Equals(weight.TargetGroupArn, stableTargetGroupArn, StringComparison.OrdinalIgnoreCase))
            {
                stable = weight.Weight;
            }
        }

        return (canary, stable);
    }

    public static async Task<string> ResolveWeightedRuleArnAsync(
        string listenerArn,
        string canaryTargetGroup,
        string stableTargetGroup,
        string region)
    {
        using var elb = string.IsNullOrWhiteSpace(region)
            ? new AmazonElasticLoadBalancingV2Client()
            : new AmazonElasticLoadBalancingV2Client(RegionEndpoint.GetBySystemName(region));

        // AWS forbids ModifyRule on a listener's DEFAULT rule (OperationNotPermitted), so the
        // substrate parks the weighted forward action on a dedicated non-default rule — exactly
        // how a production canary deployment is wired. Certify against the non-default rule whose
        // forward action targets BOTH the configured stable and canary target groups; picking the
        // first non-default rule blindly could select an unrelated redirect/fixed-response/other
        // rule and then mutate the wrong resource.
        var response = await elb.DescribeRulesAsync(new DescribeRulesRequest { ListenerArn = listenerArn });
        var candidates = response.Rules?.Where(rule => rule.IsDefault != true).ToList() ?? [];

        var weightedRule = candidates.FirstOrDefault(
            rule => RuleForwardsToBothTargetGroups(rule, canaryTargetGroup, stableTargetGroup))
            ?? throw new InvalidOperationException(
                $"Listener '{listenerArn}' has no non-default rule whose forward action targets both the "
                + $"configured stable ('{stableTargetGroup}') and canary ('{canaryTargetGroup}') target "
                + "groups to certify weighted cutover against.");

        if (string.IsNullOrWhiteSpace(weightedRule.RuleArn))
        {
            throw new InvalidOperationException(
                $"Listener '{listenerArn}' weighted rule did not resolve to a rule ARN.");
        }

        return weightedRule.RuleArn;
    }

    public static WorkflowOperationRecord BuildOperation(
        string cluster,
        string service,
        string listenerRuleArn,
        string canaryTargetGroup,
        string stableTargetGroup,
        string region,
        string desiredTaskDefinition,
        string operationPrefix = "cert-ecs-alb")
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["aws.region"] = region,
            ["aws.ecs.cluster"] = cluster,
            ["aws.ecs.canary_service"] = service,
            ["aws.alb.listener_rule_arn"] = listenerRuleArn,
            ["aws.alb.canary_target_group_arn"] = canaryTargetGroup,
            ["aws.alb.stable_target_group_arn"] = stableTargetGroup,
            // Partial canary weight — the backend shifts this share to the canary target group on
            // StartAsync and treats the converged partial as PromotionRecommended.
            ["aws.ecs.canary_weight_percentage"] = CanaryShare.ToString(CultureInfo.InvariantCulture),
        };

        var now = DateTimeOffset.UtcNow;
        return new WorkflowOperationRecord
        {
            OperationId = $"{operationPrefix}-{Guid.NewGuid():N}",
            Kind = WorkflowOperationKind.Deploy,
            Status = WorkflowOperationStatus.Submitted,
            CreatedAt = now.AddMinutes(-1),
            UpdatedAt = now,
            CurrentPhase = "Certification",
            Audit = new OperationAuditInfo(),
            Concurrency = new OperationConcurrencyPolicy
            {
                PartitionKey = $"cert-ecs-alb:{service}",
                RequiresExclusiveLease = true,
            },
            Deploy = new DeployOperationSpec
            {
                TargetId = "cert-ecs-alb",
                TargetKind = DeployTargetKind.AwsEcs,
                Backend = AwsEcsAlbDeployBackend.AdapterBackendName,
                Environment = "cert",
                TargetName = service,
                CurrentRevision = desiredTaskDefinition,
                DesiredRevision = desiredTaskDefinition,
                Parameters = parameters,
            },
        };
    }

    private static bool RuleForwardsToBothTargetGroups(
        Rule rule,
        string canaryTargetGroup,
        string stableTargetGroup)
    {
        var forward = rule.Actions?.FirstOrDefault(action => action.Type == ActionTypeEnum.Forward);
        var targetGroups = forward?.ForwardConfig?.TargetGroups;
        if (targetGroups is null)
        {
            return false;
        }

        var arns = targetGroups
            .Where(tuple => !string.IsNullOrWhiteSpace(tuple.TargetGroupArn))
            .Select(tuple => tuple.TargetGroupArn)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return arns.Contains(canaryTargetGroup) && arns.Contains(stableTargetGroup);
    }
}
