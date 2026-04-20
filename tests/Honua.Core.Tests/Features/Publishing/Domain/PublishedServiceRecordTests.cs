// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Publishing.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Publishing.Domain;

public class PublishedServiceRecordTests
{
    [UnitTest]
    public void CreateFromIntent_ShouldCreateProvisioningRecord()
    {
        var intent = PublishIntent.CreateDraft(
            "pi-001",
            PublishSourceKind.ResultPackage,
            "rp-abc",
            PublishTargetKind.FeatureService,
            new OperationAuditInfo { RequestedBy = "operator@honua.io" });

        var service = PublishedServiceRecord.CreateFromIntent("svc-001", intent);

        service.ServiceId.Should().Be("svc-001");
        service.IntentId.Should().Be("pi-001");
        service.SourceKind.Should().Be(PublishSourceKind.ResultPackage);
        service.SourceId.Should().Be("rp-abc");
        service.TargetKind.Should().Be(PublishTargetKind.FeatureService);
        service.Status.Should().Be(PublishedServiceStatus.Provisioning);
        service.Artifacts.Should().BeEmpty();
        service.Endpoint.Should().BeNull();
        service.RefreshPolicy.Should().BeNull();
        service.LastRefreshedAt.Should().BeNull();
        service.Audit.RequestedBy.Should().Be("operator@honua.io");
        service.PublishedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2));
    }

    [UnitTest]
    public void CreateFromIntent_WithArtifactsAndPolicy_ShouldPopulateAllFields()
    {
        var intent = PublishIntent.CreateDraft(
            "pi-002",
            PublishSourceKind.ResultPackage,
            "rp-flood",
            PublishTargetKind.FeatureService);

        var artifacts = new[]
        {
            new ArtifactRef { ArtifactId = "art-1", Kind = ArtifactKind.FeatureLayer, Label = "Flood zones" },
            new ArtifactRef { ArtifactId = "art-2", Kind = ArtifactKind.Table, Label = "Statistics" }
        };
        var policy = RefreshPolicy.Scheduled(TimeSpan.FromHours(6));

        var service = PublishedServiceRecord.CreateFromIntent(
            "svc-002", intent, artifacts, "/rest/services/flood/FeatureServer", policy);

        service.Artifacts.Should().HaveCount(2);
        service.Endpoint.Should().Be("/rest/services/flood/FeatureServer");
        service.RefreshPolicy.Should().NotBeNull();
        service.RefreshPolicy!.Mode.Should().Be(RefreshMode.Scheduled);
    }

    [UnitTest]
    public void WithActive_ShouldTransitionToActiveStatus()
    {
        var service = CreateTestService();

        var active = service.WithActive("/rest/services/test/FeatureServer");

        active.Status.Should().Be(PublishedServiceStatus.Active);
        active.Endpoint.Should().Be("/rest/services/test/FeatureServer");
        active.UpdatedAt.Should().BeOnOrAfter(service.UpdatedAt);
    }

    [UnitTest]
    public void WithActive_WithoutEndpoint_ShouldPreserveExistingEndpoint()
    {
        var service = CreateTestService(endpoint: "/existing");

        var active = service.WithActive();

        active.Status.Should().Be(PublishedServiceStatus.Active);
        active.Endpoint.Should().Be("/existing");
    }

    [UnitTest]
    public void WithSuspended_ShouldTransitionToSuspendedStatus()
    {
        var service = CreateTestService().WithActive();

        var suspended = service.WithSuspended();

        suspended.Status.Should().Be(PublishedServiceStatus.Suspended);
    }

    [UnitTest]
    public void WithRefreshed_ShouldUpdateArtifactsAndTimestamp()
    {
        var service = CreateTestService().WithActive();
        var updatedArtifacts = new[]
        {
            new ArtifactRef { ArtifactId = "art-new", Kind = ArtifactKind.FeatureLayer, Label = "Updated layer" }
        };

        var refreshed = service.WithRefreshed(updatedArtifacts);

        refreshed.Status.Should().Be(PublishedServiceStatus.Active);
        refreshed.Artifacts.Should().HaveCount(1);
        refreshed.Artifacts[0].ArtifactId.Should().Be("art-new");
        refreshed.LastRefreshedAt.Should().NotBeNull();
        refreshed.LastRefreshedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2));
    }

    [UnitTest]
    public void WithRefreshed_ShouldAdvanceScheduledRefreshPolicy()
    {
        var interval = TimeSpan.FromHours(6);
        var intent = PublishIntent.CreateDraft(
            "pi-sched",
            PublishSourceKind.ResultPackage,
            "rp-sched",
            PublishTargetKind.FeatureService);
        var policy = RefreshPolicy.Scheduled(interval);
        var originalNextRefreshAt = policy.NextRefreshAt;

        var service = PublishedServiceRecord.CreateFromIntent(
            "svc-sched", intent, refreshPolicy: policy).WithActive();

        var refreshed = service.WithRefreshed();

        refreshed.RefreshPolicy.Should().NotBeNull();
        refreshed.RefreshPolicy!.Mode.Should().Be(RefreshMode.Scheduled);
        refreshed.RefreshPolicy.Interval.Should().Be(interval);
        refreshed.RefreshPolicy.LastRefreshAt.Should().NotBeNull();
        refreshed.RefreshPolicy.LastRefreshAt.Should().Be(refreshed.LastRefreshedAt);
        refreshed.RefreshPolicy.NextRefreshAt.Should().NotBeNull();
        refreshed.RefreshPolicy.NextRefreshAt.Should().BeAfter(originalNextRefreshAt!.Value);
        refreshed.RefreshPolicy.NextRefreshAt.Should().BeCloseTo(
            refreshed.LastRefreshedAt!.Value + interval,
            TimeSpan.FromSeconds(2));
    }

    [UnitTest]
    public void WithRefreshed_ShouldUpdateManualRefreshPolicyLastRefreshAt()
    {
        var intent = PublishIntent.CreateDraft(
            "pi-manual",
            PublishSourceKind.ResultPackage,
            "rp-manual",
            PublishTargetKind.FeatureService);
        var policy = RefreshPolicy.Manual();

        var service = PublishedServiceRecord.CreateFromIntent(
            "svc-manual", intent, refreshPolicy: policy).WithActive();

        var refreshed = service.WithRefreshed();

        refreshed.RefreshPolicy.Should().NotBeNull();
        refreshed.RefreshPolicy!.Mode.Should().Be(RefreshMode.Manual);
        refreshed.RefreshPolicy.LastRefreshAt.Should().Be(refreshed.LastRefreshedAt);
        refreshed.RefreshPolicy.NextRefreshAt.Should().BeNull();
    }

    [UnitTest]
    public void WithRefreshed_WithoutRefreshPolicy_ShouldLeavePolicyNull()
    {
        var service = CreateTestService().WithActive();

        var refreshed = service.WithRefreshed();

        refreshed.RefreshPolicy.Should().BeNull();
        refreshed.LastRefreshedAt.Should().NotBeNull();
    }

    [UnitTest]
    public void WithRefreshed_WithoutUpdatedArtifacts_ShouldPreserveExisting()
    {
        var artifacts = new[]
        {
            new ArtifactRef { ArtifactId = "art-orig", Kind = ArtifactKind.FeatureLayer, Label = "Original" }
        };
        var intent = PublishIntent.CreateDraft("pi-keep", PublishSourceKind.FeatureLayer, "layer-1", PublishTargetKind.FeatureService);
        var service = PublishedServiceRecord.CreateFromIntent("svc-keep", intent, artifacts).WithActive();

        var refreshed = service.WithRefreshed();

        refreshed.Artifacts.Should().HaveCount(1);
        refreshed.Artifacts[0].ArtifactId.Should().Be("art-orig");
    }

    [UnitTest]
    public void WithRefreshFailed_ShouldTransitionToRefreshFailedStatus()
    {
        var service = CreateTestService().WithActive();
        var warnings = new[] { "Source table not found", "Falling back to cached data" };

        var failed = service.WithRefreshFailed(warnings);

        failed.Status.Should().Be(PublishedServiceStatus.RefreshFailed);
        failed.Warnings.Should().HaveCount(2);
    }

    [UnitTest]
    public void WithDecommissioned_ShouldTransitionToDecommissionedStatus()
    {
        var service = CreateTestService().WithActive();

        var decommissioned = service.WithDecommissioned();

        decommissioned.Status.Should().Be(PublishedServiceStatus.Decommissioned);
    }

    [UnitTest]
    public void FullLifecycle_ProvisioningThroughDecommission_ShouldTransitionCorrectly()
    {
        var intent = PublishIntent.CreateDraft(
            "pi-lifecycle",
            PublishSourceKind.WorkspaceArtifact,
            "ws-lifecycle",
            PublishTargetKind.MapService);

        var service = PublishedServiceRecord.CreateFromIntent("svc-lifecycle", intent);
        service.Status.Should().Be(PublishedServiceStatus.Provisioning);

        var active = service.WithActive("/rest/services/lifecycle/MapServer");
        active.Status.Should().Be(PublishedServiceStatus.Active);

        var refreshed = active.WithRefreshed();
        refreshed.Status.Should().Be(PublishedServiceStatus.Active);
        refreshed.LastRefreshedAt.Should().NotBeNull();

        var suspended = refreshed.WithSuspended();
        suspended.Status.Should().Be(PublishedServiceStatus.Suspended);

        var decommissioned = suspended.WithDecommissioned();
        decommissioned.Status.Should().Be(PublishedServiceStatus.Decommissioned);
    }

    [UnitTest]
    public void TransitionMethods_ShouldPreserveSourceReferences()
    {
        var service = CreateTestService();

        var decommissioned = service.WithActive().WithRefreshed().WithDecommissioned();

        decommissioned.ServiceId.Should().Be(service.ServiceId);
        decommissioned.IntentId.Should().Be(service.IntentId);
        decommissioned.SourceKind.Should().Be(service.SourceKind);
        decommissioned.SourceId.Should().Be(service.SourceId);
        decommissioned.TargetKind.Should().Be(service.TargetKind);
        decommissioned.PublishedAt.Should().Be(service.PublishedAt);
    }

    private static PublishedServiceRecord CreateTestService(string? endpoint = null)
    {
        var intent = PublishIntent.CreateDraft(
            "pi-test",
            PublishSourceKind.ResultPackage,
            "rp-test",
            PublishTargetKind.FeatureService);

        return PublishedServiceRecord.CreateFromIntent("svc-test", intent, endpoint: endpoint);
    }
}
