// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Amazon;
using Amazon.ElasticLoadBalancingV2;
using Amazon.ElasticLoadBalancingV2.Model;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Honua.CloudIntegration.Tests;

/// <summary>
/// Real-AWS certification lane (#2164) — the ECS/ALB weighted-cutover cell. Drives the REAL
/// <see cref="AwsEcsAlbDeployBackend"/> wired with its PRODUCTION seams (<see cref="AwsSdkAlbClient"/>
/// and <see cref="AwsSdkEcsClient"/> — the exact clients production injects) against the standing
/// certification substrate (honua-iac <c>examples/aws-cert</c>): a smallest-Fargate ECS service
/// attached to both target groups behind an internal ALB whose listener default rule is a weighted
/// forward action (stable=100/canary=0). It certifies the weighted-cutover control-plane MECHANICS
/// the deploy controller rides — shift → observe convergence → promote → rollback → settle — end to
/// end through live ELBv2 <c>ModifyRule</c>/<c>DescribeRules</c> and ECS <c>UpdateService</c>/
/// <c>DescribeServices</c> calls.
///
/// SAME-REVISION BY DESIGN (documented rationale): the cell resolves the service's CURRENT task
/// definition ARN and submits THAT as the desired revision. A genuine two-revision upgrade would need
/// a freshly-built/pushed container image and a second task-definition registration on every weekly
/// run, which mutates production-relevant artifacts and adds standing build infrastructure for no
/// extra coverage of the traffic-shifting seam. Deploying the same revision means ECS is already at
/// steady state on the desired task definition, so <c>UpdateService</c> converges immediately and the
/// observe/promote/rollback assertions exercise the ALB weight primitive and the convergence-decision
/// logic without waiting on an image roll. The cell therefore certifies weighted-cutover +
/// convergence + rollback mechanics, NOT a functional application upgrade.
///
/// PLANASYNC IS BYPASSED (documented decision): <see cref="AwsEcsAlbDeployBackend.PlanAsync"/> blocks
/// any canary-weighted submit on a <c>telemetry.connection</c> parameter so the production workflow
/// can auto-promote/roll-back from telemetry — a workflow-orchestration concern with no bearing on the
/// SDK cutover mechanics this cell certifies, and one the backend's unit tests already cover
/// (<c>AwsEcsAlbDeployBackendTests.PlanAsync_*</c>). Like the other certification cells, which drive
/// their backends directly, this cell calls <c>StartAsync</c> without a telemetry connection and
/// performs the promotion decision itself.
///
/// TEARDOWN INVARIANT: the listener rule is a STANDING resource (not created here), so there is
/// nothing to tag or reap — correctness is a guaranteed restore to a KNOWN BASELINE. The cell asserts
/// the resolved weighted rule is at the baseline (stable=100/canary=0) at test START and fails loudly
/// if it has drifted (surfacing a prior poisoned run), then restores that KNOWN baseline (NOT a
/// captured snapshot) plus the original service task definition UNCONDITIONALLY in a <c>finally</c>
/// block, even when an assertion throws. Restoring the fixed baseline rather than a snapshot is
/// deliberate: a run killed between promote and restore must never be able to leave canary=100 and
/// have the next run snapshot+restore that poison — the substrate always converges back to
/// stable=100/canary=0 for the next weekly run.
///
/// SAFETY: OFF unless <see cref="RealAwsCertificationFixture.EcsAlbConfigured"/>; the single fact
/// <c>[SkippableFact]</c>-skips otherwise (forks, PRs without secrets, ordinary local runs).
/// </summary>
[Trait(CloudIntegrationTraits.Category, CloudIntegrationTraits.RealAwsCertification)]
public sealed class AwsEcsAlbRealCertificationTests : IClassFixture<RealAwsCertificationFixture>
{
    // Hard wall-clock budget for the shift→PromotionRecommended poll. A same-revision deploy converges
    // fast; this ceiling keeps a misconfigured substrate from burning the workflow's 30-minute timeout.
    private static readonly TimeSpan ConvergenceBudget = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RollbackSettleBudget = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    // Partial canary share the cutover shifts to before promotion. Between 1 and 99 (the backend's
    // valid canary-weight range) so the observe path lands on PromotionRecommended, not Succeeded.
    private const int CanaryShare = 25;

    // The KNOWN baseline the standing substrate is provisioned at and must always be restored to:
    // stable serves 100%, canary serves 0%.
    private const int BaselineStableWeight = 100;
    private const int BaselineCanaryWeight = 0;

    private readonly RealAwsCertificationFixture _cert;
    private readonly ITestOutputHelper _output;

    public AwsEcsAlbRealCertificationTests(RealAwsCertificationFixture cert, ITestOutputHelper output)
    {
        _cert = cert;
        _output = output;
    }

    [SkippableFact]
    public async Task WeightedCutover_ShiftObservePromoteRollback_ThroughProductionBackend()
    {
        Skip.IfNot(
            _cert.EcsAlbConfigured,
            "Real-AWS ECS/ALB cell not configured (needs HONUA_REALAWS_CERT_ENABLED=true, "
            + "HONUA_REALAWS_CERT_ECS_CLUSTER, HONUA_REALAWS_CERT_ECS_SERVICE, "
            + "HONUA_REALAWS_CERT_ALB_LISTENER_ARN, HONUA_REALAWS_CERT_CANARY_TARGET_GROUP_ARN, "
            + "HONUA_REALAWS_CERT_STABLE_TARGET_GROUP_ARN with credentials present).");

        var region = _cert.Region;
        var cluster = _cert.EcsCluster!;
        var service = _cert.EcsService!;
        var canaryTargetGroup = _cert.CanaryTargetGroupArn!;
        var stableTargetGroup = _cert.StableTargetGroupArn!;

        // The production seams, wired exactly as production wires the backend.
        using var albClient = new AwsSdkAlbClient();
        using var ecsClient = new AwsSdkEcsClient();

        // Resolve the listener's weighted NON-DEFAULT rule (AWS forbids ModifyRule on a default rule,
        // so the substrate parks the weighted forward action on a dedicated non-default rule). The
        // substrate hands us a LISTENER ARN; the backend operates on a RULE ARN, so derive it live —
        // selecting the rule whose forward action targets BOTH configured target groups, never just
        // the first non-default rule (which could be an unrelated redirect/fixed-response rule).
        var listenerRuleArn = await ResolveWeightedRuleArnAsync(
            _cert.AlbListenerArn!, canaryTargetGroup, stableTargetGroup, region);

        // Assert the substrate is at the KNOWN BASELINE (stable=100/canary=0) before we mutate it, and
        // FAIL LOUDLY if it has drifted. A prior run that died between promote and restore would leave
        // canary=100 standing; surfacing that here stops a poisoned run from silently snapshotting and
        // perpetuating the bad weight (the finally always restores the known baseline, never a snapshot).
        var startingShares = ReadShares(await albClient.GetListenerRuleWeightsAsync(listenerRuleArn, region));
        startingShares.Stable.Should().Be(
            BaselineStableWeight,
            "the ECS/ALB substrate must start each run at the known baseline (stable=100); a drifted "
            + "stable weight means a prior run left the substrate poisoned and must be investigated");
        startingShares.Canary.Should().Be(
            BaselineCanaryWeight,
            "the ECS/ALB substrate must start each run at the known baseline (canary=0); a drifted "
            + "canary weight means a prior run left the substrate poisoned and must be investigated");

        // Same-revision deploy: the desired revision IS the service's current task definition, so ECS
        // is already converged and the cell certifies the traffic-shift mechanics, not an image roll.
        var currentService = await ecsClient.DescribeServiceAsync(cluster, service, region);
        var currentTaskDefinition = currentService.TaskDefinitionArn;
        currentTaskDefinition.Should().NotBeNullOrWhiteSpace(
            "the certification ECS service must resolve to a concrete task definition to deploy");

        var backend = new AwsEcsAlbDeployBackend(albClient, ecsClient, NullLogger<AwsEcsAlbDeployBackend>.Instance);
        var operation = BuildOperation(
            cluster,
            service,
            listenerRuleArn,
            canaryTargetGroup,
            stableTargetGroup,
            region,
            currentTaskDefinition!);

        try
        {
            // 1) Shift a partial canary share and roll the (same) task definition.
            var submission = await backend.StartAsync(operation);
            submission.Status.Should().Be(
                WorkflowOperationStatus.Submitted,
                $"the production ECS/ALB backend must accept the weighted cutover (message: {submission.Message})");

            var afterShift = ReadShares(await albClient.GetListenerRuleWeightsAsync(listenerRuleArn, region));
            afterShift.Canary.Should().Be(
                CanaryShare, "StartAsync must actually shift the live ALB canary weight");
            afterShift.Stable.Should().Be(
                100 - CanaryShare, "StartAsync must actually shift the live ALB stable weight");

            // 2) Observe until the rollout is holding the partial share and is promotable. On a
            //    same-revision deploy ECS is already at steady state, so this settles quickly.
            var recommended = await PollForPromotionAsync(backend, operation);
            recommended.Should().BeTrue(
                "a converged partial canary must reach PromotionRecommended within the budget");

            // 3) Promote → canary target group takes 100% of traffic.
            var promotion = await backend.PromoteAsync(operation);
            promotion.Status.Should().Be(
                WorkflowOperationStatus.Succeeded, "promotion must complete the cutover");
            promotion.ObservedRevision.Should().Be(
                currentTaskDefinition, "promotion must report the promoted task definition");

            var afterPromote = ReadShares(await albClient.GetListenerRuleWeightsAsync(listenerRuleArn, region));
            afterPromote.Canary.Should().Be(100, "promotion must move the canary weight to 100");
            afterPromote.Stable.Should().Be(0, "promotion must move the stable weight to 0");

            // 4) Rollback → stable target group restored to 100%, then Observe settles RolledBack.
            var rollback = await backend.RollbackAsync(operation);
            rollback.Status.Should().Be(
                WorkflowOperationStatus.RollbackRequested,
                "rollback shifts traffic back to stable and then settles on subsequent observes");

            var afterRollback = ReadShares(await albClient.GetListenerRuleWeightsAsync(listenerRuleArn, region));
            afterRollback.Canary.Should().Be(0, "rollback must move the canary weight to 0");
            afterRollback.Stable.Should().Be(100, "rollback must restore the stable weight to 100");

            // Drive the backend's terminal rollback settlement (Observe requires RollbackRequested).
            var rollingBack = operation with { Status = WorkflowOperationStatus.RollbackRequested };
            var settled = await PollForRolledBackAsync(backend, rollingBack);
            settled.Should().Be(
                WorkflowOperationStatus.RolledBack,
                "the ECS/ALB rollout must settle to RolledBack once stable serves 100% and the canary is idle");
        }
        finally
        {
            // Guaranteed restore of the STANDING substrate to the KNOWN BASELINE (stable=100/canary=0)
            // plus the original task definition, so the next weekly run starts pristine regardless of
            // any assertion failure above. Restoring the KNOWN baseline (not a captured snapshot) is
            // deliberate: a run killed between promote and restore must not be able to leave canary=100
            // and have the next run snapshot+restore that poison. Best-effort — a transient restore blip
            // must not mask a primary assertion; the start-of-test baseline assertion is the backstop
            // that surfaces any restore that did not take.
            await BestEffortRestoreAsync(
                albClient,
                ecsClient,
                listenerRuleArn,
                BuildBaselineWeights(stableTargetGroup, canaryTargetGroup),
                cluster,
                service,
                currentTaskDefinition!,
                region);
        }
    }

    // The known-baseline weighted forward action the substrate is provisioned at and is always
    // restored to: stable serves 100%, canary serves 0%.
    private static IReadOnlyList<AwsAlbTargetGroupWeight> BuildBaselineWeights(
        string stableTargetGroup,
        string canaryTargetGroup)
        =>
        [
            new AwsAlbTargetGroupWeight { TargetGroupArn = stableTargetGroup, Weight = BaselineStableWeight },
            new AwsAlbTargetGroupWeight { TargetGroupArn = canaryTargetGroup, Weight = BaselineCanaryWeight },
        ];

    private static async Task<bool> PollForPromotionAsync(
        AwsEcsAlbDeployBackend backend,
        WorkflowOperationRecord operation)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < ConvergenceBudget)
        {
            var observation = await backend.ObserveAsync(operation);

            // Reconciling + PromotionRecommended is the converged-partial endpoint. A fully-converged
            // 100% share (Succeeded) is accepted defensively but should not occur at a partial weight.
            if (observation.PromotionRecommended || observation.Status == WorkflowOperationStatus.Succeeded)
            {
                return true;
            }

            observation.Status.Should().Be(
                WorkflowOperationStatus.Reconciling,
                "a partial canary that is not yet promotable must remain Reconciling, not fail");

            await Task.Delay(PollInterval);
        }

        return false;
    }

    private static async Task<WorkflowOperationStatus> PollForRolledBackAsync(
        AwsEcsAlbDeployBackend backend,
        WorkflowOperationRecord rollingBack)
    {
        var stopwatch = Stopwatch.StartNew();
        var latest = WorkflowOperationStatus.RollbackRequested;
        while (stopwatch.Elapsed < RollbackSettleBudget)
        {
            var observation = await backend.ObserveAsync(rollingBack);
            latest = observation.Status;
            if (latest == WorkflowOperationStatus.RolledBack)
            {
                return latest;
            }

            await Task.Delay(PollInterval);
        }

        return latest;
    }

    private async Task BestEffortRestoreAsync(
        AwsSdkAlbClient albClient,
        AwsSdkEcsClient ecsClient,
        string listenerRuleArn,
        IReadOnlyList<AwsAlbTargetGroupWeight> baselineWeights,
        string cluster,
        string service,
        string originalTaskDefinition,
        string region)
    {
        try
        {
            await albClient.UpdateListenerRuleWeightsAsync(listenerRuleArn, baselineWeights, region);
        }
        catch (Exception ex)
        {
            // Best-effort: if this restore does not take, the rule stays at whatever weight the last
            // successful mutation set — possibly canary=100 after a promote — so it can leave traffic
            // shifted. There is NO guarantee a failed rollout already moved traffic back to stable.
            // We do not throw (that would mask a primary assertion), but we log LOUDLY so CI shows it,
            // and the next run's start-of-test baseline assertion fails hard on the leftover drift.
            _output.WriteLine(
                $"[cert] WARNING: baseline restore of listener rule '{listenerRuleArn}' FAILED: "
                + $"{ex.Message}. The rule may be left off-baseline (e.g. canary=100); the next run's "
                + "baseline assertion will surface it.");
        }

        try
        {
            await ecsClient.UpdateServiceTaskDefinitionAsync(cluster, service, originalTaskDefinition, region);
        }
        catch (Exception ex)
        {
            // Best-effort: same-revision deploys never change the service task definition, so this is a
            // defensive no-op that must not mask the primary assertion outcome — but log it if it fails.
            _output.WriteLine(
                $"[cert] task-definition restore for service '{service}' failed (defensive no-op on the "
                + $"same-revision happy path): {ex.Message}");
        }
    }

    private static async Task<string> ResolveWeightedRuleArnAsync(
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

    // True when the rule has a forward action whose target-group set contains BOTH configured groups.
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

    private (int Canary, int Stable) ReadShares(AwsAlbListenerRuleState state)
    {
        var canary = 0;
        var stable = 0;
        foreach (var weight in state.TargetGroupWeights)
        {
            if (string.Equals(weight.TargetGroupArn, _cert.CanaryTargetGroupArn, StringComparison.OrdinalIgnoreCase))
            {
                canary = weight.Weight;
            }
            else if (string.Equals(weight.TargetGroupArn, _cert.StableTargetGroupArn, StringComparison.OrdinalIgnoreCase))
            {
                stable = weight.Weight;
            }
        }

        return (canary, stable);
    }

    private static WorkflowOperationRecord BuildOperation(
        string cluster,
        string service,
        string listenerRuleArn,
        string canaryTargetGroup,
        string stableTargetGroup,
        string region,
        string desiredTaskDefinition)
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
            ["aws.ecs.canary_weight_percentage"] = CanaryShare.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        var now = DateTimeOffset.UtcNow;
        return new WorkflowOperationRecord
        {
            OperationId = $"cert-ecs-alb-{Guid.NewGuid():N}",
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
}
