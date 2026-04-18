// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Deployment.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Deployment.Domain;

public class DeploymentTests
{
    [UnitTest]
    public void CreateDraft_ShouldCreateDeploymentInDraftStatus()
    {
        var source = DeploymentSource.FromPublishedService("svc-001");
        var target = CreateTestTarget();

        var deployment = Honua.Core.Features.Deployment.Domain.Deployment.CreateDraft(
            "dep-001",
            source,
            target);

        deployment.DeploymentId.Should().Be("dep-001");
        deployment.Source.Should().Be(source);
        deployment.Target.Should().Be(target);
        deployment.Status.Should().Be(DeploymentStatus.Draft);
        deployment.PublicationState.Should().Be(DeploymentPublicationState.Draft);
        deployment.Rollout.Strategy.Should().Be(RolloutStrategy.Immediate);
        deployment.RolloutState.Should().Be(RolloutState.Pending);
        deployment.CurrentRolloutStep.Should().BeNull();
        deployment.Schedule.Should().BeNull();
        deployment.Runtime.Health.Should().Be(RuntimeHealth.Unknown);
        deployment.ActivatedAt.Should().BeNull();
        deployment.RetiredAt.Should().BeNull();
        deployment.FailureReason.Should().BeNull();
        deployment.SupersededByDeploymentId.Should().BeNull();
        deployment.Transitions.Should().BeEmpty();
        deployment.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2));
    }

    [UnitTest]
    public void CreateDraft_WithRolloutAndSchedule_ShouldPopulateAllFields()
    {
        var schedule = DeploymentSchedule.At(DateTimeOffset.UtcNow.AddHours(1));
        var rollout = RolloutPlan.Canary([10, 50, 100]);
        var audit = new OperationAuditInfo { RequestedBy = "operator@honua.io", Reason = "Stage flood-risk app" };

        var deployment = CreateTestDeployment(rollout: rollout, schedule: schedule, audit: audit);

        deployment.Rollout.Should().Be(rollout);
        deployment.Schedule.Should().Be(schedule);
        deployment.Audit.RequestedBy.Should().Be("operator@honua.io");
        deployment.Audit.Reason.Should().Be("Stage flood-risk app");
    }

    [UnitTest]
    public void WithScheduled_ShouldTransitionAndAttachSchedule()
    {
        var deployment = CreateTestDeployment();
        var schedule = DeploymentSchedule.At(DateTimeOffset.UtcNow.AddHours(2));

        var scheduled = deployment.WithScheduled(schedule, reason: "Weekly publish window");

        scheduled.Status.Should().Be(DeploymentStatus.Scheduled);
        scheduled.PublicationState.Should().Be(DeploymentPublicationState.Scheduled);
        scheduled.Schedule.Should().Be(schedule);
        scheduled.Transitions.Should().HaveCount(1);
        scheduled.Transitions[0].From.Should().Be(DeploymentStatus.Draft);
        scheduled.Transitions[0].To.Should().Be(DeploymentStatus.Scheduled);
        scheduled.Transitions[0].Reason.Should().Be("Weekly publish window");
    }

    [UnitTest]
    public void WithProvisioning_ShouldTransitionToProvisioning()
    {
        var deployment = CreateTestDeployment();

        var provisioning = deployment.WithProvisioning();

        provisioning.Status.Should().Be(DeploymentStatus.Provisioning);
        provisioning.Transitions.Should().HaveCount(1);
        provisioning.Transitions[0].To.Should().Be(DeploymentStatus.Provisioning);
    }

    [UnitTest]
    public void WithRollingOut_ShouldTransitionAndStartFirstCanaryStep()
    {
        var deployment = CreateTestDeployment(rollout: RolloutPlan.Canary([25, 50, 100]));

        var rolling = deployment.WithProvisioning().WithRollingOut();

        rolling.Status.Should().Be(DeploymentStatus.RollingOut);
        rolling.RolloutState.Should().Be(RolloutState.InProgress);
        rolling.CurrentRolloutStep.Should().Be(0);
    }

    [UnitTest]
    public void WithRollingOut_OnImmediateRollout_ShouldNotSetStep()
    {
        var deployment = CreateTestDeployment();

        var rolling = deployment.WithProvisioning().WithRollingOut();

        rolling.Status.Should().Be(DeploymentStatus.RollingOut);
        rolling.RolloutState.Should().Be(RolloutState.InProgress);
        rolling.CurrentRolloutStep.Should().BeNull();
    }

    [UnitTest]
    public void WithRolloutStep_OnCanary_ShouldAdvanceStepWithoutChangingStatus()
    {
        var deployment = CreateTestDeployment(rollout: RolloutPlan.Canary([10, 50, 100]))
            .WithProvisioning()
            .WithRollingOut();

        var advanced = deployment.WithRolloutStep(1);

        advanced.Status.Should().Be(DeploymentStatus.RollingOut);
        advanced.CurrentRolloutStep.Should().Be(1);
        advanced.Transitions[^1].Reason.Should().Be("rollout_step_1");
    }

    [UnitTest]
    public void WithRolloutStep_OnNonCanaryRollout_ShouldThrow()
    {
        var deployment = CreateTestDeployment().WithProvisioning().WithRollingOut();

        var act = () => deployment.WithRolloutStep(0);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*canary*");
    }

    [UnitTest]
    public void WithRolloutStep_OutsideStepRange_ShouldThrow()
    {
        var deployment = CreateTestDeployment(rollout: RolloutPlan.Canary([50, 100]))
            .WithProvisioning()
            .WithRollingOut();

        var act = () => deployment.WithRolloutStep(5);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("step");
    }

    [UnitTest]
    public void WithRolloutStep_WhenNotRollingOut_ShouldThrow()
    {
        var deployment = CreateTestDeployment(rollout: RolloutPlan.Canary([50, 100]));

        var act = () => deployment.WithRolloutStep(0);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*rolling out*");
    }

    [UnitTest]
    public void WithActive_ShouldTransitionPromoteAndPublish()
    {
        var deployment = CreateTestDeployment().WithProvisioning().WithRollingOut();

        var active = deployment.WithActive("/apps/flood");

        active.Status.Should().Be(DeploymentStatus.Active);
        active.PublicationState.Should().Be(DeploymentPublicationState.Published);
        active.RolloutState.Should().Be(RolloutState.Promoted);
        active.Runtime.PublicUrl.Should().Be("/apps/flood");
        active.Runtime.Health.Should().Be(RuntimeHealth.Healthy);
        active.ActivatedAt.Should().NotBeNull();
        active.ActivatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2));
    }

    [UnitTest]
    public void WithActive_ShouldPreserveExistingActivatedAt()
    {
        var deployment = CreateTestDeployment().WithProvisioning().WithRollingOut().WithActive();
        var firstActivatedAt = deployment.ActivatedAt;

        var reactivated = deployment.WithActive();

        reactivated.ActivatedAt.Should().Be(firstActivatedAt);
    }

    [UnitTest]
    public void WithSuperseded_ShouldRecordSuccessorAndUnpublish()
    {
        var deployment = CreateTestDeployment().WithProvisioning().WithRollingOut().WithActive();

        var superseded = deployment.WithSuperseded("dep-002", reason: "Rolled forward");

        superseded.Status.Should().Be(DeploymentStatus.Superseded);
        superseded.PublicationState.Should().Be(DeploymentPublicationState.Unpublished);
        superseded.SupersededByDeploymentId.Should().Be("dep-002");
        superseded.RetiredAt.Should().NotBeNull();
    }

    [UnitTest]
    public void WithSuperseded_WithoutSuccessor_ShouldThrow()
    {
        var deployment = CreateTestDeployment().WithProvisioning().WithRollingOut().WithActive();

        var act = () => deployment.WithSuperseded(string.Empty);

        act.Should().Throw<ArgumentException>();
    }

    [UnitTest]
    public void WithRetired_ShouldUnpublishAndStampRetiredAt()
    {
        var deployment = CreateTestDeployment().WithProvisioning().WithRollingOut().WithActive();

        var retired = deployment.WithRetired("End of campaign");

        retired.Status.Should().Be(DeploymentStatus.Retired);
        retired.PublicationState.Should().Be(DeploymentPublicationState.Retired);
        retired.RetiredAt.Should().NotBeNull();
    }

    [UnitTest]
    public void WithFailed_ShouldCaptureFailureReasonAndMarkRolloutFailed()
    {
        var deployment = CreateTestDeployment().WithProvisioning().WithRollingOut();

        var failed = deployment.WithFailed("Hosting quota exceeded");

        failed.Status.Should().Be(DeploymentStatus.Failed);
        failed.FailureReason.Should().Be("Hosting quota exceeded");
        failed.RolloutState.Should().Be(RolloutState.Failed);
    }

    [UnitTest]
    public void WithCancelled_ShouldMarkRolloutCancelled()
    {
        var deployment = CreateTestDeployment().WithProvisioning();

        var cancelled = deployment.WithCancelled("Operator abandoned deploy");

        cancelled.Status.Should().Be(DeploymentStatus.Cancelled);
        cancelled.RolloutState.Should().Be(RolloutState.Cancelled);
        cancelled.PublicationState.Should().Be(DeploymentPublicationState.Unpublished);
        cancelled.Schedule.Should().BeNull();
    }

    [UnitTest]
    public void WithCancelled_FromScheduled_ShouldClearScheduleAndPublicationState()
    {
        var schedule = DeploymentSchedule.At(DateTimeOffset.UtcNow.AddHours(2));
        var deployment = CreateTestDeployment().WithScheduled(schedule);

        var cancelled = deployment.WithCancelled("Schedule revoked");

        cancelled.Status.Should().Be(DeploymentStatus.Cancelled);
        cancelled.RolloutState.Should().Be(RolloutState.Cancelled);
        cancelled.PublicationState.Should().Be(DeploymentPublicationState.Unpublished);
        cancelled.Schedule.Should().BeNull();
    }

    [UnitTest]
    public void WithFailed_FromScheduled_ShouldClearScheduleAndPublicationState()
    {
        var schedule = DeploymentSchedule.At(DateTimeOffset.UtcNow.AddHours(1));
        var deployment = CreateTestDeployment().WithScheduled(schedule);

        var failed = deployment.WithFailed("Scheduler outage");

        failed.Status.Should().Be(DeploymentStatus.Failed);
        failed.PublicationState.Should().Be(DeploymentPublicationState.Unpublished);
        failed.Schedule.Should().BeNull();
        failed.FailureReason.Should().Be("Scheduler outage");
    }

    [UnitTest]
    public void WithSuperseded_FromScheduled_ShouldClearSchedule()
    {
        var schedule = DeploymentSchedule.At(DateTimeOffset.UtcNow.AddHours(1));
        var deployment = CreateTestDeployment().WithScheduled(schedule);

        var superseded = deployment.WithSuperseded("dep-002", reason: "Replaced before publish");

        superseded.Status.Should().Be(DeploymentStatus.Superseded);
        superseded.PublicationState.Should().Be(DeploymentPublicationState.Unpublished);
        superseded.Schedule.Should().BeNull();
    }

    [UnitTest]
    public void WithRetired_FromScheduled_ShouldClearSchedule()
    {
        var schedule = DeploymentSchedule.At(DateTimeOffset.UtcNow.AddHours(1));
        var deployment = CreateTestDeployment().WithScheduled(schedule);

        var retired = deployment.WithRetired("Campaign cancelled before publish");

        retired.Status.Should().Be(DeploymentStatus.Retired);
        retired.PublicationState.Should().Be(DeploymentPublicationState.Retired);
        retired.Schedule.Should().BeNull();
    }

    [UnitTest]
    public void WithRollbackRequested_ShouldClearScheduleAndUnpublish()
    {
        var schedule = DeploymentSchedule.At(DateTimeOffset.UtcNow.AddHours(1));
        var deployment = CreateTestDeployment()
            .WithScheduled(schedule)
            .WithProvisioning()
            .WithRollingOut();

        var rolledBack = deployment.WithRollbackRequested("Health regressed");

        rolledBack.Status.Should().Be(DeploymentStatus.Failed);
        rolledBack.RolloutState.Should().Be(RolloutState.RolledBack);
        rolledBack.PublicationState.Should().Be(DeploymentPublicationState.Unpublished);
        rolledBack.Schedule.Should().BeNull();
    }

    [UnitTest]
    public void WithRollbackRequested_ShouldMarkRolloutRolledBackAndFail()
    {
        var deployment = CreateTestDeployment().WithProvisioning().WithRollingOut();

        var rolledBack = deployment.WithRollbackRequested("Canary health regressed");

        rolledBack.Status.Should().Be(DeploymentStatus.Failed);
        rolledBack.RolloutState.Should().Be(RolloutState.RolledBack);
        rolledBack.FailureReason.Should().Be("Canary health regressed");
    }

    [UnitTest]
    public void WithFailed_ShouldRequireReason()
    {
        var deployment = CreateTestDeployment().WithProvisioning();

        var act = () => deployment.WithFailed(string.Empty);

        act.Should().Throw<ArgumentException>();
    }

    [UnitTest]
    public void WithRuntime_ShouldUpdateRuntimeStateWithoutChangingStatus()
    {
        var deployment = CreateTestDeployment().WithProvisioning().WithRollingOut().WithActive();
        var observed = RuntimeState.Degraded("Provider latency elevated", publicUrl: "/apps/flood");

        var updated = deployment.WithRuntime(observed);

        updated.Status.Should().Be(DeploymentStatus.Active);
        updated.Runtime.Should().Be(observed);
        updated.Transitions.Count.Should().Be(deployment.Transitions.Count);
    }

    [UnitTest]
    public void Transitions_ShouldBeAppendOnlyAcrossLifecycle()
    {
        var audit = new OperationAuditInfo { RequestedBy = "ops@honua.io" };
        var deployment = CreateTestDeployment(rollout: RolloutPlan.Canary([25, 75, 100]), audit: audit);

        var progression = deployment
            .WithProvisioning(audit: audit)
            .WithRollingOut(audit: audit)
            .WithRolloutStep(1, audit: audit)
            .WithRolloutStep(2, audit: audit)
            .WithActive("/apps/flood", audit: audit);

        progression.Transitions.Should().HaveCount(5);
        progression.Transitions.Select(t => t.To).Should().Equal(
            DeploymentStatus.Provisioning,
            DeploymentStatus.RollingOut,
            DeploymentStatus.RollingOut,
            DeploymentStatus.RollingOut,
            DeploymentStatus.Active);
        progression.Transitions.All(t => t.Audit.RequestedBy == "ops@honua.io").Should().BeTrue();
        progression.Status.Should().Be(DeploymentStatus.Active);
        progression.CurrentRolloutStep.Should().Be(2);
    }

    [UnitTest]
    public void FullLifecycle_FromDraftThroughRetirement_ShouldTransitionCorrectly()
    {
        var deployment = CreateTestDeployment(rollout: RolloutPlan.BlueGreen());

        var active = deployment.WithProvisioning().WithRollingOut().WithActive("/apps/lifecycle");
        active.Status.Should().Be(DeploymentStatus.Active);

        var retired = active.WithRetired("Campaign complete");
        retired.Status.Should().Be(DeploymentStatus.Retired);
        retired.RetiredAt.Should().NotBeNull();
        retired.Transitions[^1].To.Should().Be(DeploymentStatus.Retired);
    }

    [UnitTest]
    public void ControlPlaneOperationId_ShouldAttachReferenceWithoutAppendingTransition()
    {
        var deployment = CreateTestDeployment().WithProvisioning();

        var linked = deployment.WithControlPlaneOperation("cp-op-1");

        linked.ControlPlaneOperationId.Should().Be("cp-op-1");
        linked.Transitions.Count.Should().Be(deployment.Transitions.Count);
    }

    [UnitTest]
    public void DeploymentSource_Factories_ShouldCaptureArtifactIdentifiers()
    {
        DeploymentSource.FromPublishedService("svc").Kind.Should().Be(DeploymentSourceKind.PublishedService);
        DeploymentSource.FromPublishedService("svc").PublishedServiceId.Should().Be("svc");
        DeploymentSource.FromMapPackage("map").Kind.Should().Be(DeploymentSourceKind.MapPackage);
        DeploymentSource.FromMapPackage("map").MapPackageId.Should().Be("map");

        var app = DeploymentSource.FromAppPackage("app", mapPackageId: "map", publishedServiceId: "svc");
        app.Kind.Should().Be(DeploymentSourceKind.AppPackage);
        app.AppPackageId.Should().Be("app");
        app.MapPackageId.Should().Be("map");
        app.PublishedServiceId.Should().Be("svc");
    }

    private static Honua.Core.Features.Deployment.Domain.Deployment CreateTestDeployment(
        RolloutPlan? rollout = null,
        DeploymentSchedule? schedule = null,
        OperationAuditInfo? audit = null)
        => Honua.Core.Features.Deployment.Domain.Deployment.CreateDraft(
            "dep-test",
            DeploymentSource.FromPublishedService("svc-test"),
            CreateTestTarget(),
            rollout,
            schedule,
            audit);

    private static DeploymentTarget CreateTestTarget()
        => new()
        {
            TargetId = "target-test",
            Kind = DeploymentKind.FeatureService,
            HostingMode = HostingMode.ManagedService,
            RoutePrefix = "/rest/services/test/FeatureServer",
            Environment = "production"
        };
}
