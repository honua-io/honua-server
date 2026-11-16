// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Honua.CloudIntegration.Tests;

[Trait(CloudIntegrationTraits.Category, CloudIntegrationTraits.RealAwsCertification)]
public sealed class AwsEcsAlbRealCertificationTests : IClassFixture<RealAwsCertificationFixture>
{
    // Hard wall-clock budget for the shift→PromotionRecommended poll. A same-revision deploy converges
    // fast; this ceiling keeps a misconfigured substrate from burning the workflow's 30-minute timeout.
    private static readonly TimeSpan ConvergenceBudget = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

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
        var listenerRuleArn = await AwsEcsAlbCertificationSupport.ResolveWeightedRuleArnAsync(
            _cert.AlbListenerArn!, canaryTargetGroup, stableTargetGroup, region);

        // Assert the substrate is at the KNOWN BASELINE (stable=100/canary=0) before we mutate it, and
        // FAIL LOUDLY if it has drifted. A prior run that died between promote and restore would leave
        // canary=100 standing; surfacing that here stops a poisoned run from silently snapshotting and
        // perpetuating the bad weight (the finally always restores the known baseline, never a snapshot).
        var startingShares = AwsEcsAlbCertificationSupport.ReadShares(
            await albClient.GetListenerRuleWeightsAsync(listenerRuleArn, region),
            canaryTargetGroup,
            stableTargetGroup);
        startingShares.Stable.Should().Be(
            AwsEcsAlbCertificationSupport.BaselineStableWeight,
            "the ECS/ALB substrate must start each run at the known baseline (stable=100); a drifted "
            + "stable weight means a prior run left the substrate poisoned and must be investigated");
        startingShares.Canary.Should().Be(
            AwsEcsAlbCertificationSupport.BaselineCanaryWeight,
            "the ECS/ALB substrate must start each run at the known baseline (canary=0); a drifted "
            + "canary weight means a prior run left the substrate poisoned and must be investigated");

        // Same-revision deploy: the desired revision IS the service's current task definition, so ECS
        // is already converged and the cell certifies the traffic-shift mechanics, not an image roll.
        var currentService = await ecsClient.DescribeServiceAsync(cluster, service, region);
        var currentTaskDefinition = currentService.TaskDefinitionArn;
        currentTaskDefinition.Should().NotBeNullOrWhiteSpace(
            "the certification ECS service must resolve to a concrete task definition to deploy");

        var backend = new AwsEcsAlbDeployBackend(
            new AwsEcsAlbDeployClients(albClient, ecsClient),
            NullLogger<AwsEcsAlbDeployBackend>.Instance);
        var operation = AwsEcsAlbCertificationSupport.BuildOperation(
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

            var afterShift = AwsEcsAlbCertificationSupport.ReadShares(
                await albClient.GetListenerRuleWeightsAsync(listenerRuleArn, region),
                canaryTargetGroup,
                stableTargetGroup);
            afterShift.Canary.Should().Be(
                AwsEcsAlbCertificationSupport.CanaryShare,
                "StartAsync must actually shift the live ALB canary weight");
            afterShift.Stable.Should().Be(
                100 - AwsEcsAlbCertificationSupport.CanaryShare,
                "StartAsync must actually shift the live ALB stable weight");

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

            var afterPromote = AwsEcsAlbCertificationSupport.ReadShares(
                await albClient.GetListenerRuleWeightsAsync(listenerRuleArn, region),
                canaryTargetGroup,
                stableTargetGroup);
            afterPromote.Canary.Should().Be(100, "promotion must move the canary weight to 100");
            afterPromote.Stable.Should().Be(0, "promotion must move the stable weight to 0");

            // 4) Rollback → stable target group restored to 100%, then Observe settles RolledBack.
            var rollback = await backend.RollbackAsync(operation);
            rollback.Status.Should().Be(
                WorkflowOperationStatus.RollbackRequested,
                "rollback shifts traffic back to stable and then settles on subsequent observes");

            var afterRollback = AwsEcsAlbCertificationSupport.ReadShares(
                await albClient.GetListenerRuleWeightsAsync(listenerRuleArn, region),
                canaryTargetGroup,
                stableTargetGroup);
            afterRollback.Canary.Should().Be(0, "rollback must move the canary weight to 0");
            afterRollback.Stable.Should().Be(100, "rollback must restore the stable weight to 100");

            // Weight convergence is not restored data-plane proof. This lane has no stable-service
            // identity, target-health sample, or functional query, so the operation must stay
            // non-terminal instead of reporting RolledBack (honua-server#3892).
            var rollingBack = operation with { Status = WorkflowOperationStatus.RollbackRequested };
            var settled = await backend.ObserveAsync(rollingBack);
            settled.Status.Should().Be(
                WorkflowOperationStatus.RollbackRequested,
                "stable=100 with no proven restored target health must not terminate as RolledBack");
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
                AwsEcsAlbCertificationSupport.BuildBaselineWeights(stableTargetGroup, canaryTargetGroup),
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
        catch (Exception ex) when (ex is not OutOfMemoryException)
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
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Best-effort: same-revision deploys never change the service task definition, so this is a
            // defensive no-op that must not mask the primary assertion outcome — but log it if it fails.
            _output.WriteLine(
                $"[cert] task-definition restore for service '{service}' failed (defensive no-op on the "
                + $"same-revision happy path): {ex.Message}");
        }
    }

}
