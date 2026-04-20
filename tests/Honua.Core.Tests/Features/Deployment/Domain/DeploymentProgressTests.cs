// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Deployment.Domain;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Deployment.Domain;

public class DeploymentProgressTests
{
    [UnitTest]
    public void CreateInitial_ShouldReturnDraftProgressWithDeploymentId()
    {
        var progress = DeploymentProgress.CreateInitial("op-001", "dep-001");

        progress.OperationId.Should().Be("op-001");
        progress.DeploymentId.Should().Be("dep-001");
        progress.DeploymentStatus.Should().Be(DeploymentStatus.Draft);
        progress.CurrentPhase.Should().Be("Initializing");
        progress.StartedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2));
        progress.CompletedAt.Should().BeNull();
    }

    [UnitTest]
    public void CreateProvisioning_ShouldReturnProvisioningProgress()
    {
        var progress = DeploymentProgress.CreateProvisioning("op-002", "dep-002");

        progress.DeploymentStatus.Should().Be(DeploymentStatus.Provisioning);
        progress.CurrentPhase.Should().Be("Provisioning");
    }

    [UnitTest]
    public void CreateRollingOut_ShouldCaptureRolloutState()
    {
        var progress = DeploymentProgress.CreateRollingOut("op-003", "dep-003", RolloutState.InProgress);

        progress.DeploymentStatus.Should().Be(DeploymentStatus.RollingOut);
        progress.RolloutState.Should().Be(RolloutState.InProgress);
        progress.CurrentPhase.Should().Be("Rolling out");
    }

    [UnitTest]
    public void Status_Draft_ShouldMapToQueued()
    {
        var progress = DeploymentProgress.CreateInitial("op-q1", "dep-q1");

        progress.Status.Should().Be(OperationStatus.Queued);
    }

    [UnitTest]
    public void Status_Scheduled_ShouldMapToQueued()
    {
        var progress = new DeploymentProgress
        {
            OperationId = "op-s1",
            DeploymentStatus = DeploymentStatus.Scheduled
        };

        progress.Status.Should().Be(OperationStatus.Queued);
    }

    [UnitTest]
    public void Status_Provisioning_ShouldMapToProcessing()
    {
        var progress = DeploymentProgress.CreateProvisioning("op-p1", "dep-p1");

        progress.Status.Should().Be(OperationStatus.Processing);
    }

    [UnitTest]
    public void Status_RollingOut_ShouldMapToProcessing()
    {
        var progress = DeploymentProgress.CreateRollingOut("op-r1", "dep-r1", RolloutState.InProgress);

        progress.Status.Should().Be(OperationStatus.Processing);
    }

    [UnitTest]
    public void Status_Active_ShouldMapToCompleted()
    {
        var progress = new DeploymentProgress
        {
            OperationId = "op-a1",
            DeploymentStatus = DeploymentStatus.Active
        };

        progress.Status.Should().Be(OperationStatus.Completed);
    }

    [UnitTest]
    public void Status_Superseded_ShouldMapToCompleted()
    {
        var progress = new DeploymentProgress
        {
            OperationId = "op-sp1",
            DeploymentStatus = DeploymentStatus.Superseded
        };

        progress.Status.Should().Be(OperationStatus.Completed);
    }

    [UnitTest]
    public void Status_Retired_ShouldMapToCompleted()
    {
        var progress = new DeploymentProgress
        {
            OperationId = "op-rt1",
            DeploymentStatus = DeploymentStatus.Retired
        };

        progress.Status.Should().Be(OperationStatus.Completed);
    }

    [UnitTest]
    public void Status_Failed_ShouldMapToFailed()
    {
        var progress = new DeploymentProgress
        {
            OperationId = "op-f1",
            DeploymentStatus = DeploymentStatus.Failed
        };

        progress.Status.Should().Be(OperationStatus.Failed);
    }

    [UnitTest]
    public void Status_Cancelled_ShouldMapToCancelled()
    {
        var progress = new DeploymentProgress
        {
            OperationId = "op-x1",
            DeploymentStatus = DeploymentStatus.Cancelled
        };

        progress.Status.Should().Be(OperationStatus.Cancelled);
    }

    [UnitTest]
    public void WithCancellation_ShouldTransitionToCancelledWithTimestamp()
    {
        var progress = DeploymentProgress.CreateRollingOut("op-cancel", "dep-cancel", RolloutState.InProgress);
        var cancelTime = DateTimeOffset.UtcNow;

        var cancelled = (DeploymentProgress)progress.WithCancellation(cancelTime, "Cancelled by operator");

        cancelled.DeploymentStatus.Should().Be(DeploymentStatus.Cancelled);
        cancelled.CompletedAt.Should().Be(cancelTime);
        cancelled.CurrentPhase.Should().Be("Cancelled by operator");
        cancelled.Status.Should().Be(OperationStatus.Cancelled);
    }

    [UnitTest]
    public void WithCancellation_FromRollingOut_ShouldNormalizeRolloutStateToCancelled()
    {
        var progress = DeploymentProgress.CreateRollingOut("op-stale", "dep-stale", RolloutState.InProgress);

        var cancelled = (DeploymentProgress)progress.WithCancellation(DateTimeOffset.UtcNow, "Cancelled");

        cancelled.RolloutState.Should().Be(RolloutState.Cancelled);
    }

    [UnitTest]
    public void WithCancellation_FromInitialDraft_ShouldNormalizeRolloutStateToCancelled()
    {
        var progress = DeploymentProgress.CreateInitial("op-draft-cancel", "dep-draft-cancel");

        var cancelled = (DeploymentProgress)progress.WithCancellation(DateTimeOffset.UtcNow, "Cancelled before provision");

        cancelled.DeploymentStatus.Should().Be(DeploymentStatus.Cancelled);
        cancelled.RolloutState.Should().Be(RolloutState.Cancelled);
    }

    [UnitTest]
    public void OperationType_ShouldBeDeployment()
    {
        IOperationProgress progress = DeploymentProgress.CreateInitial("op-type", "dep-type");

        progress.Type.Should().Be(OperationType.Deployment);
    }

    [UnitTest]
    public void Duration_WhenRunning_ShouldReturnElapsedTime()
    {
        var progress = new DeploymentProgress
        {
            OperationId = "op-dur",
            DeploymentStatus = DeploymentStatus.RollingOut,
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-10)
        };

        progress.Duration.Should().BeGreaterThan(TimeSpan.FromSeconds(9));
    }

    [UnitTest]
    public void Duration_WhenCompleted_ShouldReturnFixedDuration()
    {
        var startedAt = DateTimeOffset.UtcNow.AddSeconds(-30);
        var completedAt = DateTimeOffset.UtcNow.AddSeconds(-10);
        var progress = new DeploymentProgress
        {
            OperationId = "op-dur2",
            DeploymentStatus = DeploymentStatus.Active,
            StartedAt = startedAt,
            CompletedAt = completedAt
        };

        progress.Duration.Should().BeCloseTo(TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(100));
    }
}
