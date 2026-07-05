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
/// nothing to tag or reap — correctness is a guaranteed restore. The original default-rule weights and
/// the original service task definition are snapshotted at test start and restored UNCONDITIONALLY in
/// a <c>finally</c> block, even when an assertion above throws, so every weekly run leaves the
/// substrate pristine (stable=100/canary=0) for the next one.
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

    private readonly RealAwsCertificationFixture _cert;

    public AwsEcsAlbRealCertificationTests(RealAwsCertificationFixture cert)
    {
        _cert = cert;
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

        // Resolve the listener's DEFAULT rule (the weighted forward action the backend mutates). The
        // substrate hands us a LISTENER ARN; the backend operates on a RULE ARN, so derive it live.
        var listenerRuleArn = await ResolveDefaultRuleArnAsync(_cert.AlbListenerArn!, region);

        // Same-revision deploy: the desired revision IS the service's current task definition, so ECS
        // is already converged and the cell certifies the traffic-shift mechanics, not an image roll.
        var currentService = await ecsClient.DescribeServiceAsync(cluster, service, region);
        var currentTaskDefinition = currentService.TaskDefinitionArn;
        currentTaskDefinition.Should().NotBeNullOrWhiteSpace(
            "the certification ECS service must resolve to a concrete task definition to deploy");

        // Snapshot the pre-test state so the finally can restore the substrate exactly, even on failure.
        var originalRuleState = await albClient.GetListenerRuleWeightsAsync(listenerRuleArn, region);

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
                "the production ECS/ALB backend must accept the weighted cutover");

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
            // Guaranteed restore of the STANDING substrate: original default-rule weights + original
            // task definition, so the next weekly run starts from stable=100/canary=0 regardless of any
            // assertion failure above. Best-effort — a transient restore blip must not mask a primary
            // assertion, and the task-definition restore is a no-op on the same-revision happy path.
            await BestEffortRestoreAsync(
                albClient,
                ecsClient,
                listenerRuleArn,
                originalRuleState.TargetGroupWeights,
                cluster,
                service,
                currentTaskDefinition!,
                region);
        }
    }

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

    private static async Task BestEffortRestoreAsync(
        AwsSdkAlbClient albClient,
        AwsSdkEcsClient ecsClient,
        string listenerRuleArn,
        IReadOnlyList<AwsAlbTargetGroupWeight> originalWeights,
        string cluster,
        string service,
        string originalTaskDefinition,
        string region)
    {
        try
        {
            await albClient.UpdateListenerRuleWeightsAsync(listenerRuleArn, originalWeights, region);
        }
        catch
        {
            // Best-effort: the substrate default is stable=100/canary=0 and a failed rollout already
            // shifts traffic back to stable, so a transient restore failure does not strand traffic.
        }

        try
        {
            await ecsClient.UpdateServiceTaskDefinitionAsync(cluster, service, originalTaskDefinition, region);
        }
        catch
        {
            // Best-effort: same-revision deploys never change the service task definition, so this is a
            // defensive no-op that must not mask the primary assertion outcome.
        }
    }

    private static async Task<string> ResolveDefaultRuleArnAsync(string listenerArn, string region)
    {
        using var elb = string.IsNullOrWhiteSpace(region)
            ? new AmazonElasticLoadBalancingV2Client()
            : new AmazonElasticLoadBalancingV2Client(RegionEndpoint.GetBySystemName(region));

        var response = await elb.DescribeRulesAsync(new DescribeRulesRequest { ListenerArn = listenerArn });
        var defaultRule = response.Rules?.FirstOrDefault(rule => rule.IsDefault == true)
            ?? throw new InvalidOperationException(
                $"Listener '{listenerArn}' has no default rule to certify weighted cutover against.");

        if (string.IsNullOrWhiteSpace(defaultRule.RuleArn))
        {
            throw new InvalidOperationException(
                $"Listener '{listenerArn}' default rule did not resolve to a rule ARN.");
        }

        return defaultRule.RuleArn;
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
