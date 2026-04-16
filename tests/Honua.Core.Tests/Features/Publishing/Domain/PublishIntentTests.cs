// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Publishing.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Publishing.Domain;

public class PublishIntentTests
{
    [UnitTest]
    public void CreateDraft_ShouldCreateIntentWithDraftStatus()
    {
        var intent = PublishIntent.CreateDraft(
            "pi-001",
            PublishSourceKind.ResultPackage,
            "rp-abc",
            PublishTargetKind.FeatureService);

        intent.IntentId.Should().Be("pi-001");
        intent.SourceKind.Should().Be(PublishSourceKind.ResultPackage);
        intent.SourceId.Should().Be("rp-abc");
        intent.TargetKind.Should().Be(PublishTargetKind.FeatureService);
        intent.Status.Should().Be(PublishIntentStatus.Draft);
        intent.ArtifactIds.Should().BeEmpty();
        intent.TargetConfig.Should().BeEmpty();
        intent.RejectionReason.Should().BeNull();
        intent.FailureReason.Should().BeNull();
        intent.PublishedServiceId.Should().BeNull();
        intent.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2));
        intent.UpdatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2));
    }

    [UnitTest]
    public void CreateDraft_WithAuditAndArtifacts_ShouldPopulateAllFields()
    {
        var audit = new OperationAuditInfo
        {
            RequestedBy = "operator@honua.io",
            Reason = "Publish flood analysis results",
            IdempotencyKey = "pub-flood-001"
        };
        var artifactIds = new[] { "art-1", "art-2" };
        var targetConfig = new Dictionary<string, string> { ["serviceName"] = "flood-results" };

        var intent = PublishIntent.CreateDraft(
            "pi-002",
            PublishSourceKind.ResultPackage,
            "rp-flood",
            PublishTargetKind.FeatureService,
            audit,
            artifactIds,
            targetConfig);

        intent.Audit.RequestedBy.Should().Be("operator@honua.io");
        intent.Audit.Reason.Should().Be("Publish flood analysis results");
        intent.ArtifactIds.Should().HaveCount(2);
        intent.TargetConfig.Should().ContainKey("serviceName");
    }

    [UnitTest]
    public void WithValidated_ShouldTransitionToValidatedStatus()
    {
        var intent = CreateTestDraft();

        var validated = intent.WithValidated();

        validated.Status.Should().Be(PublishIntentStatus.Validated);
        validated.IntentId.Should().Be(intent.IntentId);
        validated.UpdatedAt.Should().BeOnOrAfter(intent.UpdatedAt);
    }

    [UnitTest]
    public void WithAwaitingApproval_ShouldTransitionToAwaitingApprovalStatus()
    {
        var intent = CreateTestDraft().WithValidated();

        var awaiting = intent.WithAwaitingApproval();

        awaiting.Status.Should().Be(PublishIntentStatus.AwaitingApproval);
    }

    [UnitTest]
    public void WithApproved_ShouldTransitionToApprovedStatus()
    {
        var intent = CreateTestDraft().WithValidated().WithAwaitingApproval();

        var approved = intent.WithApproved();

        approved.Status.Should().Be(PublishIntentStatus.Approved);
    }

    [UnitTest]
    public void WithExecuting_ShouldTransitionToExecutingStatus()
    {
        var intent = CreateTestDraft().WithValidated().WithApproved();

        var executing = intent.WithExecuting();

        executing.Status.Should().Be(PublishIntentStatus.Executing);
    }

    [UnitTest]
    public void WithCompleted_ShouldTransitionToCompletedStatusAndSetServiceId()
    {
        var intent = CreateTestDraft().WithValidated().WithApproved().WithExecuting();

        var completed = intent.WithCompleted("svc-001");

        completed.Status.Should().Be(PublishIntentStatus.Completed);
        completed.PublishedServiceId.Should().Be("svc-001");
    }

    [UnitTest]
    public void WithRejected_ShouldTransitionToRejectedStatusWithReason()
    {
        var intent = CreateTestDraft().WithValidated().WithAwaitingApproval();

        var rejected = intent.WithRejected("Insufficient permissions");

        rejected.Status.Should().Be(PublishIntentStatus.Rejected);
        rejected.RejectionReason.Should().Be("Insufficient permissions");
        rejected.FailureReason.Should().BeNull();
    }

    [UnitTest]
    public void WithFailed_ShouldTransitionToFailedStatusWithFailureReason()
    {
        var intent = CreateTestDraft().WithValidated().WithApproved().WithExecuting();

        var failed = intent.WithFailed("Target service unavailable");

        failed.Status.Should().Be(PublishIntentStatus.Failed);
        failed.FailureReason.Should().Be("Target service unavailable");
        failed.RejectionReason.Should().BeNull();
    }

    [UnitTest]
    public void WithCancelled_ShouldTransitionToCancelledStatus()
    {
        var intent = CreateTestDraft().WithValidated();

        var cancelled = intent.WithCancelled();

        cancelled.Status.Should().Be(PublishIntentStatus.Cancelled);
    }

    [UnitTest]
    public void FullLifecycle_DraftThroughCompletion_ShouldTransitionCorrectly()
    {
        var intent = PublishIntent.CreateDraft(
            "pi-lifecycle",
            PublishSourceKind.WorkspaceArtifact,
            "ws-123",
            PublishTargetKind.MapService);

        intent.Status.Should().Be(PublishIntentStatus.Draft);

        var validated = intent.WithValidated();
        validated.Status.Should().Be(PublishIntentStatus.Validated);

        var approved = validated.WithApproved();
        approved.Status.Should().Be(PublishIntentStatus.Approved);

        var executing = approved.WithExecuting();
        executing.Status.Should().Be(PublishIntentStatus.Executing);

        var completed = executing.WithCompleted("svc-lifecycle");
        completed.Status.Should().Be(PublishIntentStatus.Completed);
        completed.PublishedServiceId.Should().Be("svc-lifecycle");
    }

    [UnitTest]
    public void TransitionMethods_ShouldPreserveImmutableSourceFields()
    {
        var audit = new OperationAuditInfo { RequestedBy = "test-user" };
        var intent = PublishIntent.CreateDraft(
            "pi-immutable",
            PublishSourceKind.FeatureLayer,
            "layer-42",
            PublishTargetKind.TileService,
            audit);

        var completed = intent.WithValidated().WithApproved().WithExecuting().WithCompleted("svc-99");

        completed.IntentId.Should().Be("pi-immutable");
        completed.SourceKind.Should().Be(PublishSourceKind.FeatureLayer);
        completed.SourceId.Should().Be("layer-42");
        completed.TargetKind.Should().Be(PublishTargetKind.TileService);
        completed.Audit.RequestedBy.Should().Be("test-user");
        completed.CreatedAt.Should().Be(intent.CreatedAt);
    }

    private static PublishIntent CreateTestDraft()
        => PublishIntent.CreateDraft(
            "pi-test",
            PublishSourceKind.ResultPackage,
            "rp-test",
            PublishTargetKind.FeatureService);
}
