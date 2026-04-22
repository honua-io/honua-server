// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Publishing.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Publishing.Domain;

public class PublishingProgressTests
{
    [UnitTest]
    public void CreateInitial_ShouldCreateDraftProgressWithIntentId()
    {
        var progress = PublishingProgress.CreateInitial("op-001", "pi-001");

        progress.OperationId.Should().Be("op-001");
        progress.IntentId.Should().Be("pi-001");
        progress.IntentStatus.Should().Be(PublishIntentStatus.Draft);
        progress.ServiceId.Should().BeNull();
        progress.CurrentPhase.Should().Be("Initializing");
        progress.StartedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2));
        progress.CompletedAt.Should().BeNull();
    }

    [UnitTest]
    public void CreateExecuting_ShouldCreateExecutingProgress()
    {
        var progress = PublishingProgress.CreateExecuting("op-002", "pi-002");

        progress.IntentStatus.Should().Be(PublishIntentStatus.Executing);
        progress.CurrentPhase.Should().Be("Publishing");
    }

    [UnitTest]
    public void Status_Draft_ShouldMapToQueued()
    {
        var progress = PublishingProgress.CreateInitial("op-q1", "pi-q1");

        progress.Status.Should().Be(OperationStatus.Queued);
    }

    [UnitTest]
    public void Status_Validated_ShouldMapToQueued()
    {
        var progress = new PublishingProgress
        {
            OperationId = "op-v1",
            IntentStatus = PublishIntentStatus.Validated
        };

        progress.Status.Should().Be(OperationStatus.Queued);
    }

    [UnitTest]
    public void Status_AwaitingApproval_ShouldMapToQueued()
    {
        var progress = new PublishingProgress
        {
            OperationId = "op-aa1",
            IntentStatus = PublishIntentStatus.AwaitingApproval
        };

        progress.Status.Should().Be(OperationStatus.Queued);
    }

    [UnitTest]
    public void Status_Approved_ShouldMapToQueued()
    {
        var progress = new PublishingProgress
        {
            OperationId = "op-ap1",
            IntentStatus = PublishIntentStatus.Approved
        };

        progress.Status.Should().Be(OperationStatus.Queued);
    }

    [UnitTest]
    public void Status_Executing_ShouldMapToProcessing()
    {
        var progress = PublishingProgress.CreateExecuting("op-e1", "pi-e1");

        progress.Status.Should().Be(OperationStatus.Processing);
    }

    [UnitTest]
    public void Status_Completed_ShouldMapToCompleted()
    {
        var progress = new PublishingProgress
        {
            OperationId = "op-c1",
            IntentStatus = PublishIntentStatus.Completed
        };

        progress.Status.Should().Be(OperationStatus.Completed);
    }

    [UnitTest]
    public void Status_Rejected_ShouldMapToFailed()
    {
        var progress = new PublishingProgress
        {
            OperationId = "op-r1",
            IntentStatus = PublishIntentStatus.Rejected
        };

        progress.Status.Should().Be(OperationStatus.Failed);
    }

    [UnitTest]
    public void Status_Failed_ShouldMapToFailed()
    {
        var progress = new PublishingProgress
        {
            OperationId = "op-f1",
            IntentStatus = PublishIntentStatus.Failed
        };

        progress.Status.Should().Be(OperationStatus.Failed);
    }

    [UnitTest]
    public void Status_Cancelled_ShouldMapToCancelled()
    {
        var progress = new PublishingProgress
        {
            OperationId = "op-x1",
            IntentStatus = PublishIntentStatus.Cancelled
        };

        progress.Status.Should().Be(OperationStatus.Cancelled);
    }

    [UnitTest]
    public void WithCancellation_ShouldTransitionToCancelledWithTimestamp()
    {
        var progress = PublishingProgress.CreateExecuting("op-cancel", "pi-cancel");
        var cancelTime = DateTimeOffset.UtcNow;

        var cancelled = (PublishingProgress)progress.WithCancellation(cancelTime, "Cancelled by operator");

        cancelled.IntentStatus.Should().Be(PublishIntentStatus.Cancelled);
        cancelled.CompletedAt.Should().Be(cancelTime);
        cancelled.CurrentPhase.Should().Be("Cancelled by operator");
        cancelled.Status.Should().Be(OperationStatus.Cancelled);
    }

    [UnitTest]
    public void OperationType_ShouldBePublishing()
    {
        IOperationProgress progress = PublishingProgress.CreateInitial("op-type", "pi-type");

        progress.Type.Should().Be(OperationType.Publishing);
    }

    [UnitTest]
    public void Duration_WhenRunning_ShouldReturnElapsedTime()
    {
        var progress = new PublishingProgress
        {
            OperationId = "op-dur",
            IntentStatus = PublishIntentStatus.Executing,
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-10)
        };

        progress.Duration.Should().BeGreaterThan(TimeSpan.FromSeconds(9));
    }

    [UnitTest]
    public void Duration_WhenCompleted_ShouldReturnFixedDuration()
    {
        var startedAt = DateTimeOffset.UtcNow.AddSeconds(-30);
        var completedAt = DateTimeOffset.UtcNow.AddSeconds(-10);
        var progress = new PublishingProgress
        {
            OperationId = "op-dur2",
            IntentStatus = PublishIntentStatus.Completed,
            StartedAt = startedAt,
            CompletedAt = completedAt
        };

        progress.Duration.Should().BeCloseTo(TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(100));
    }
}
