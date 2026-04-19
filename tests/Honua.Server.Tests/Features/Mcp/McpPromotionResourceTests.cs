// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Deployment.Abstractions;
using Honua.Core.Features.Deployment.Domain;
using Honua.Core.Features.Publishing.Abstractions;
using Honua.Core.Features.Publishing.Domain;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Mcp;
using Honua.Server.Features.Mcp.Resources;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Mcp;

/// <summary>
/// Verifies the promotion-surface resource handlers — published services,
/// deployments, map/app packages, and the list-root index — serialize canonical
/// fields, surface provenance edges, enforce authorization, return not-found for
/// unknown identifiers, and cap list reads with a <c>truncated</c> flag.
/// </summary>
[Protocol(Protocols.Mcp)]
public sealed class McpPromotionResourceTests
{
    private static readonly DateTimeOffset PublishedAt =
        DateTimeOffset.Parse("2026-04-18T10:00:00Z", CultureInfo.InvariantCulture);
    private static readonly DateTimeOffset UpdatedAt =
        DateTimeOffset.Parse("2026-04-18T12:00:00Z", CultureInfo.InvariantCulture);

    private readonly IGeoprocessingJobService _jobService = Substitute.For<IGeoprocessingJobService>();
    private readonly IPublishedServiceStore _services = Substitute.For<IPublishedServiceStore>();
    private readonly IPublishIntentStore _intents = Substitute.For<IPublishIntentStore>();
    private readonly IDeploymentStore _deployments = Substitute.For<IDeploymentStore>();

    // ------------------------------------------------------------------
    // PublishedServiceResource
    // ------------------------------------------------------------------

    [UnitTest]
    [Endpoint("POST /mcp resources/read honua://published-services/{serviceId}")]
    public async Task PublishedServiceResource_Read_SerializesRecordAndProvenance()
    {
        var service = BuildPublishedService("svc-1", intentId: "intent-1");
        _services.GetAsync("svc-1", Arg.Any<CancellationToken>()).Returns(service);
        _intents.GetAsync("intent-1", Arg.Any<CancellationToken>())
            .Returns(BuildIntent("intent-1", sourceKind: PublishSourceKind.ResultPackage, sourceId: "pkg-7"));
        _deployments.ListBySourceAsync(DeploymentSourceKind.PublishedService, "svc-1", Arg.Any<CancellationToken>())
            .Returns([BuildDeployment("dep-1", DeploymentSource.FromPublishedService("svc-1"))]);

        var resource = BuildPublishedServiceResource();
        var result = await resource.ReadAsync(
            McpTestFactory.AuthenticatedHttpContext(),
            "honua://published-services/svc-1",
            CancellationToken.None);

        var body = McpTestFactory.ParseJson(result.Contents[0].Text);
        result.Contents[0].Uri.Should().Be("honua://published-services/svc-1");
        body.GetProperty("serviceId").GetString().Should().Be("svc-1");
        body.GetProperty("resourceUri").GetString().Should().Be("honua://published-services/svc-1");
        body.GetProperty("status").GetString().Should().Be("Active");
        body.GetProperty("targetKind").GetString().Should().Be("FeatureService");
        body.GetProperty("etag").GetString().Should().StartWith("W/\"");
        body.GetProperty("deploymentCount").GetInt32().Should().Be(1);
        body.GetProperty("deploymentResourceUris")[0].GetString()
            .Should().Be("honua://deployments/dep-1");
        var provenance = body.GetProperty("provenance");
        provenance.GetProperty("originatingIntentId").GetString().Should().Be("intent-1");
        provenance.GetProperty("resultPackageId").GetString().Should().Be("pkg-7");
        provenance.TryGetProperty("parentDeploymentResourceUri", out _).Should().BeFalse();
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/read honua://published-services/{serviceId}")]
    public async Task PublishedServiceResource_Read_ExposesAllActiveDeploymentsSortedStably()
    {
        var service = BuildPublishedService("svc-multi");
        _services.GetAsync("svc-multi", Arg.Any<CancellationToken>()).Returns(service);
        _intents.GetAsync("intent-1", Arg.Any<CancellationToken>())
            .Returns(BuildIntent("intent-1"));
        // Return in reverse alphabetical order to verify sort is applied.
        _deployments.ListBySourceAsync(DeploymentSourceKind.PublishedService, "svc-multi", Arg.Any<CancellationToken>())
            .Returns(
            [
                BuildDeployment("dep-z", DeploymentSource.FromPublishedService("svc-multi")),
                BuildDeployment("dep-a", DeploymentSource.FromPublishedService("svc-multi"))
            ]);

        var resource = BuildPublishedServiceResource();
        var result = await resource.ReadAsync(
            McpTestFactory.AuthenticatedHttpContext(),
            "honua://published-services/svc-multi",
            CancellationToken.None);

        var body = McpTestFactory.ParseJson(result.Contents[0].Text);
        body.GetProperty("deploymentCount").GetInt32().Should().Be(2);
        var uris = body.GetProperty("deploymentResourceUris").EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();
        uris.Should().Equal("honua://deployments/dep-a", "honua://deployments/dep-z");
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/read honua://published-services/{serviceId}")]
    public async Task PublishedServiceResource_Read_ExcludesRetiredAndSupersededDeployments()
    {
        var service = BuildPublishedService("svc-mix");
        _services.GetAsync("svc-mix", Arg.Any<CancellationToken>()).Returns(service);
        _intents.GetAsync("intent-1", Arg.Any<CancellationToken>())
            .Returns(BuildIntent("intent-1"));
        _deployments.ListBySourceAsync(DeploymentSourceKind.PublishedService, "svc-mix", Arg.Any<CancellationToken>())
            .Returns(
            [
                BuildDeployment("dep-retired", DeploymentSource.FromPublishedService("svc-mix"), status: DeploymentStatus.Retired),
                BuildDeployment("dep-active", DeploymentSource.FromPublishedService("svc-mix")),
                BuildDeployment("dep-superseded", DeploymentSource.FromPublishedService("svc-mix"), status: DeploymentStatus.Superseded)
            ]);

        var resource = BuildPublishedServiceResource();
        var result = await resource.ReadAsync(
            McpTestFactory.AuthenticatedHttpContext(),
            "honua://published-services/svc-mix",
            CancellationToken.None);

        var body = McpTestFactory.ParseJson(result.Contents[0].Text);
        body.GetProperty("deploymentCount").GetInt32().Should().Be(1);
        body.GetProperty("deploymentResourceUris")[0].GetString()
            .Should().Be("honua://deployments/dep-active");
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/read honua://published-services/{serviceId}")]
    public async Task PublishedServiceResource_MissingRecord_ThrowsNotFound()
    {
        _services.GetAsync("missing", Arg.Any<CancellationToken>()).Returns((PublishedServiceRecord?)null);

        var resource = BuildPublishedServiceResource();
        var act = async () => await resource.ReadAsync(
            McpTestFactory.AuthenticatedHttpContext(),
            "honua://published-services/missing",
            CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingNotFoundException>();
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/read honua://published-services/{serviceId}")]
    public async Task PublishedServiceResource_AuthenticatedButUnauthorized_ThrowsPermissionDenied()
    {
        _jobService
            .When(s => s.EnsureCallerAuthorized(
                Arg.Any<ClaimsPrincipal>(), OperatorResourceType.PublishedService, OperatorOperation.Read))
            .Do(_ => throw new GeoprocessingAuthorizationException(requiresAuthentication: false));

        var resource = BuildPublishedServiceResource();
        var act = async () => await resource.ReadAsync(
            McpTestFactory.AuthenticatedHttpContext(),
            "honua://published-services/svc-1",
            CancellationToken.None);

        (await act.Should().ThrowAsync<GeoprocessingAuthorizationException>())
            .Which.RequiresAuthentication.Should().BeFalse();
        await _services.DidNotReceiveWithAnyArgs().GetAsync(default!, default);
    }

    [UnitTest]
    public void PublishedServiceResource_CanHandle_MatchesOnlyBareServiceUris()
    {
        var resource = BuildPublishedServiceResource();

        resource.CanHandle("honua://published-services/svc-1").Should().BeTrue();
        resource.CanHandle("honua://published-services/").Should().BeFalse();
        resource.CanHandle("honua://published-services/svc-1/sub").Should().BeFalse();
        resource.CanHandle("honua://published-services").Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // DeploymentResource
    // ------------------------------------------------------------------

    [UnitTest]
    [Endpoint("POST /mcp resources/read honua://deployments/{deploymentId}")]
    public async Task DeploymentResource_Read_SerializesRecordAndTransitionAudit()
    {
        var deployment = BuildDeployment(
            "dep-1",
            DeploymentSource.FromPublishedService("svc-1"),
            transitions:
            [
                new DeploymentTransition
                {
                    From = DeploymentStatus.Draft,
                    To = DeploymentStatus.Provisioning,
                    At = PublishedAt
                },
                new DeploymentTransition
                {
                    From = DeploymentStatus.Provisioning,
                    To = DeploymentStatus.Active,
                    At = UpdatedAt,
                    RolloutState = RolloutState.Promoted
                }
            ]);
        _deployments.GetAsync("dep-1", Arg.Any<CancellationToken>()).Returns(deployment);

        var resource = BuildDeploymentResource();
        var result = await resource.ReadAsync(
            McpTestFactory.AuthenticatedHttpContext(),
            "honua://deployments/dep-1",
            CancellationToken.None);

        var body = McpTestFactory.ParseJson(result.Contents[0].Text);
        body.GetProperty("deploymentId").GetString().Should().Be("dep-1");
        body.GetProperty("sourceKind").GetString().Should().Be("PublishedService");
        body.GetProperty("sourceResourceUri").GetString().Should().Be("honua://published-services/svc-1");
        body.GetProperty("transitions").GetArrayLength().Should().Be(2);
        body.GetProperty("transitions")[1].GetProperty("to").GetString().Should().Be("Active");
        body.GetProperty("transitions")[1].GetProperty("rolloutState").GetString().Should().Be("Promoted");
        body.GetProperty("provenance").GetProperty("publishedServiceResourceUri").GetString()
            .Should().Be("honua://published-services/svc-1");
        body.GetProperty("etag").GetString().Should().StartWith("W/\"");
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/read honua://deployments/{deploymentId}")]
    public async Task DeploymentResource_MissingRecord_ThrowsNotFound()
    {
        _deployments.GetAsync("missing", Arg.Any<CancellationToken>()).Returns((Deployment?)null);

        var resource = BuildDeploymentResource();
        var act = async () => await resource.ReadAsync(
            McpTestFactory.AuthenticatedHttpContext(),
            "honua://deployments/missing",
            CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingNotFoundException>();
    }

    [UnitTest]
    public void DeploymentResource_CanHandle_MatchesOnlyBareDeploymentUris()
    {
        var resource = BuildDeploymentResource();

        resource.CanHandle("honua://deployments/dep-1").Should().BeTrue();
        resource.CanHandle("honua://deployments/").Should().BeFalse();
        resource.CanHandle("honua://deployments/dep-1/transitions").Should().BeFalse();
        resource.CanHandle("honua://deployments").Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // MapPackageResource / AppPackageResource
    // ------------------------------------------------------------------

    [UnitTest]
    [Endpoint("POST /mcp resources/read honua://map-packages/{packageId}")]
    public async Task MapPackageResource_Read_ReverseLooksUpDeployments()
    {
        // Return in reverse order to verify the view sorts deployment uris stably
        // regardless of store reverse-lookup ordering.
        _deployments.ListBySourceAsync(DeploymentSourceKind.MapPackage, "map-55", Arg.Any<CancellationToken>())
            .Returns(
            [
                BuildDeployment("dep-b", DeploymentSource.FromMapPackage("map-55")),
                BuildDeployment("dep-a", DeploymentSource.FromMapPackage("map-55"))
            ]);

        var resource = new MapPackageResource(_deployments, _jobService, NullLogger<MapPackageResource>.Instance);
        var result = await resource.ReadAsync(
            McpTestFactory.AuthenticatedHttpContext(),
            "honua://map-packages/map-55",
            CancellationToken.None);

        var body = McpTestFactory.ParseJson(result.Contents[0].Text);
        body.GetProperty("packageKind").GetString().Should().Be("map_package");
        body.GetProperty("packageId").GetString().Should().Be("map-55");
        body.GetProperty("deploymentCount").GetInt32().Should().Be(2);
        var uris = body.GetProperty("deploymentResourceUris").EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();
        uris.Should().Equal("honua://deployments/dep-a", "honua://deployments/dep-b");
        body.GetProperty("provenance").TryGetProperty("parentDeploymentResourceUri", out _)
            .Should().BeFalse();
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/read honua://map-packages/{packageId}")]
    public async Task MapPackageResource_NoDeployments_ThrowsNotFound()
    {
        _deployments.ListBySourceAsync(DeploymentSourceKind.MapPackage, "orphan", Arg.Any<CancellationToken>())
            .Returns([]);

        var resource = new MapPackageResource(_deployments, _jobService, NullLogger<MapPackageResource>.Instance);
        var act = async () => await resource.ReadAsync(
            McpTestFactory.AuthenticatedHttpContext(),
            "honua://map-packages/orphan",
            CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingNotFoundException>();
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/read honua://map-packages/{packageId}")]
    public async Task MapPackageResource_OnlyRetiredDeployments_ThrowsNotFound()
    {
        _deployments.ListBySourceAsync(DeploymentSourceKind.MapPackage, "retired-only", Arg.Any<CancellationToken>())
            .Returns(
            [
                BuildDeployment("dep-r1", DeploymentSource.FromMapPackage("retired-only"), status: DeploymentStatus.Retired),
                BuildDeployment("dep-r2", DeploymentSource.FromMapPackage("retired-only"), status: DeploymentStatus.Superseded)
            ]);

        var resource = new MapPackageResource(_deployments, _jobService, NullLogger<MapPackageResource>.Instance);
        var act = async () => await resource.ReadAsync(
            McpTestFactory.AuthenticatedHttpContext(),
            "honua://map-packages/retired-only",
            CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingNotFoundException>();
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/read honua://map-packages/{packageId}")]
    public async Task MapPackageResource_Read_ExcludesRetiredDeploymentsFromCount()
    {
        _deployments.ListBySourceAsync(DeploymentSourceKind.MapPackage, "mixed", Arg.Any<CancellationToken>())
            .Returns(
            [
                BuildDeployment("dep-retired", DeploymentSource.FromMapPackage("mixed"), status: DeploymentStatus.Retired),
                BuildDeployment("dep-active", DeploymentSource.FromMapPackage("mixed"))
            ]);

        var resource = new MapPackageResource(_deployments, _jobService, NullLogger<MapPackageResource>.Instance);
        var result = await resource.ReadAsync(
            McpTestFactory.AuthenticatedHttpContext(),
            "honua://map-packages/mixed",
            CancellationToken.None);

        var body = McpTestFactory.ParseJson(result.Contents[0].Text);
        body.GetProperty("deploymentCount").GetInt32().Should().Be(1);
        body.GetProperty("deploymentResourceUris")[0].GetString()
            .Should().Be("honua://deployments/dep-active");
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/read honua://app-packages/{packageId}")]
    public async Task AppPackageResource_Read_ReverseLooksUpDeployments()
    {
        _deployments.ListBySourceAsync(DeploymentSourceKind.AppPackage, "app-9", Arg.Any<CancellationToken>())
            .Returns([BuildDeployment("dep-a", DeploymentSource.FromAppPackage("app-9"))]);

        var resource = new AppPackageResource(_deployments, _jobService, NullLogger<AppPackageResource>.Instance);
        var result = await resource.ReadAsync(
            McpTestFactory.AuthenticatedHttpContext(),
            "honua://app-packages/app-9",
            CancellationToken.None);

        var body = McpTestFactory.ParseJson(result.Contents[0].Text);
        body.GetProperty("packageKind").GetString().Should().Be("app_package");
        body.GetProperty("packageId").GetString().Should().Be("app-9");
        body.GetProperty("deploymentCount").GetInt32().Should().Be(1);
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/read honua://app-packages/{packageId}")]
    public async Task AppPackageResource_OnlySupersededDeployments_ThrowsNotFound()
    {
        _deployments.ListBySourceAsync(DeploymentSourceKind.AppPackage, "superseded", Arg.Any<CancellationToken>())
            .Returns(
            [
                BuildDeployment("dep-s", DeploymentSource.FromAppPackage("superseded"), status: DeploymentStatus.Superseded)
            ]);

        var resource = new AppPackageResource(_deployments, _jobService, NullLogger<AppPackageResource>.Instance);
        var act = async () => await resource.ReadAsync(
            McpTestFactory.AuthenticatedHttpContext(),
            "honua://app-packages/superseded",
            CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingNotFoundException>();
    }

    [UnitTest]
    public void PackageResources_CanHandle_MatchOnlyBarePackageUris()
    {
        var map = new MapPackageResource(_deployments, _jobService, NullLogger<MapPackageResource>.Instance);
        var app = new AppPackageResource(_deployments, _jobService, NullLogger<AppPackageResource>.Instance);

        map.CanHandle("honua://map-packages/pkg").Should().BeTrue();
        map.CanHandle("honua://map-packages/").Should().BeFalse();
        map.CanHandle("honua://map-packages/pkg/parts").Should().BeFalse();
        map.CanHandle("honua://app-packages/pkg").Should().BeFalse();

        app.CanHandle("honua://app-packages/pkg").Should().BeTrue();
        app.CanHandle("honua://app-packages/").Should().BeFalse();
        app.CanHandle("honua://map-packages/pkg").Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // PromotionSurfaceIndexResource
    // ------------------------------------------------------------------

    [UnitTest]
    [Endpoint("POST /mcp resources/read honua://published-services")]
    public async Task PromotionIndex_PublishedServices_EmitsCappedListWithTruncatedFlag()
    {
        var records = Enumerable.Range(0, 3)
            .Select(i => BuildPublishedService($"svc-{i}", intentId: $"intent-{i}"))
            .ToList();
        _services.ListActiveAsync(Arg.Any<CancellationToken>()).Returns(records);

        var resource = new PromotionSurfaceIndexResource(
            _services, _deployments, _jobService,
            NullLogger<PromotionSurfaceIndexResource>.Instance, pageSize: 2);

        var result = await resource.ReadAsync(
            McpTestFactory.AuthenticatedHttpContext(),
            "honua://published-services",
            CancellationToken.None);

        var body = McpTestFactory.ParseJson(result.Contents[0].Text);
        body.GetProperty("count").GetInt32().Should().Be(2);
        body.GetProperty("truncated").GetBoolean().Should().BeTrue();
        body.GetProperty("items").GetArrayLength().Should().Be(2);
        body.GetProperty("items")[0].GetProperty("resourceUri").GetString()
            .Should().Be("honua://published-services/svc-0");
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/read honua://deployments")]
    public async Task PromotionIndex_Deployments_SurfacesSummaryFields()
    {
        var deployment = BuildDeployment("dep-x", DeploymentSource.FromPublishedService("svc-x"));
        _deployments.ListActiveAsync(Arg.Any<CancellationToken>()).Returns([deployment]);

        var resource = new PromotionSurfaceIndexResource(
            _services, _deployments, _jobService,
            NullLogger<PromotionSurfaceIndexResource>.Instance);

        var result = await resource.ReadAsync(
            McpTestFactory.AuthenticatedHttpContext(),
            "honua://deployments",
            CancellationToken.None);

        var body = McpTestFactory.ParseJson(result.Contents[0].Text);
        body.GetProperty("count").GetInt32().Should().Be(1);
        body.GetProperty("truncated").GetBoolean().Should().BeFalse();
        var item = body.GetProperty("items")[0];
        item.GetProperty("deploymentId").GetString().Should().Be("dep-x");
        item.GetProperty("publicationState").GetString().Should().Be("Published");
        item.GetProperty("sourceKind").GetString().Should().Be("PublishedService");
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/read honua://map-packages")]
    public async Task PromotionIndex_MapPackages_GroupsByPackageId()
    {
        _deployments.ListActiveAsync(Arg.Any<CancellationToken>()).Returns(
        [
            BuildDeployment("dep-1", DeploymentSource.FromMapPackage("map-a")),
            BuildDeployment("dep-2", DeploymentSource.FromMapPackage("map-a")),
            BuildDeployment("dep-3", DeploymentSource.FromMapPackage("map-b")),
            BuildDeployment("dep-4", DeploymentSource.FromAppPackage("app-c"))
        ]);

        var resource = new PromotionSurfaceIndexResource(
            _services, _deployments, _jobService,
            NullLogger<PromotionSurfaceIndexResource>.Instance);

        var result = await resource.ReadAsync(
            McpTestFactory.AuthenticatedHttpContext(),
            "honua://map-packages",
            CancellationToken.None);

        var body = McpTestFactory.ParseJson(result.Contents[0].Text);
        body.GetProperty("packageKind").GetString().Should().Be("map_package");
        body.GetProperty("count").GetInt32().Should().Be(2);
        var packageIds = body.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("packageId").GetString()!)
            .ToArray();
        packageIds.Should().Contain(["map-a", "map-b"]);
        // Ensure app-package deployments are not mixed in.
        packageIds.Should().NotContain("app-c");
    }

    [UnitTest]
    public void PromotionIndex_CanHandle_MatchesAllFourRoots()
    {
        var resource = new PromotionSurfaceIndexResource(
            _services, _deployments, _jobService,
            NullLogger<PromotionSurfaceIndexResource>.Instance);

        resource.CanHandle("honua://published-services").Should().BeTrue();
        resource.CanHandle("honua://deployments").Should().BeTrue();
        resource.CanHandle("honua://map-packages").Should().BeTrue();
        resource.CanHandle("honua://app-packages").Should().BeTrue();
        resource.CanHandle("honua://published-services/svc-1").Should().BeFalse();
        resource.CanHandle("honua://jobs").Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // ETag observability
    // ------------------------------------------------------------------

    [UnitTest]
    public void PromotionEtag_PublishedService_IncludesStatusSoStateChangeInvalidates()
    {
        var record = BuildPublishedService("svc-1");
        var suspended = record with { Status = PublishedServiceStatus.Suspended };

        PromotionSurfaceEtag.ForPublishedService(record)
            .Should().NotBe(PromotionSurfaceEtag.ForPublishedService(suspended));
    }

    [UnitTest]
    public void PromotionEtag_Deployment_AdvancesWithNewTransition()
    {
        var before = BuildDeployment("dep-1", DeploymentSource.FromPublishedService("svc-1"));
        var after = before with
        {
            UpdatedAt = before.UpdatedAt.AddMinutes(1),
            Transitions =
            [
                .. before.Transitions,
                new DeploymentTransition
                {
                    From = DeploymentStatus.Active,
                    To = DeploymentStatus.Retired,
                    At = before.UpdatedAt.AddMinutes(1)
                }
            ],
            Status = DeploymentStatus.Retired
        };

        var beforeTag = PromotionSurfaceEtag.ForDeployment(before);
        var afterTag = PromotionSurfaceEtag.ForDeployment(after);
        afterTag.Should().NotBe(beforeTag);
        string.Compare(afterTag, beforeTag, StringComparison.Ordinal).Should().BePositive();
    }

    // ------------------------------------------------------------------
    // Authentication gate
    // ------------------------------------------------------------------

    [UnitTest]
    [Endpoint("POST /mcp resources/read honua://published-services/{serviceId}")]
    public async Task PublishedServiceResource_Anonymous_ThrowsAuthenticationRequired()
    {
        var resource = BuildPublishedServiceResource();
        var act = async () => await resource.ReadAsync(
            McpTestFactory.AnonymousHttpContext(),
            "honua://published-services/svc-1",
            CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
        await _services.DidNotReceiveWithAnyArgs().GetAsync(default!, default);
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/read honua://deployments/{deploymentId}")]
    public async Task DeploymentResource_Anonymous_ThrowsAuthenticationRequired()
    {
        var resource = BuildDeploymentResource();
        var act = async () => await resource.ReadAsync(
            McpTestFactory.AnonymousHttpContext(),
            "honua://deployments/dep-1",
            CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/read honua://published-services")]
    public async Task PromotionIndex_Anonymous_ThrowsAuthenticationRequired()
    {
        var resource = new PromotionSurfaceIndexResource(
            _services, _deployments, _jobService,
            NullLogger<PromotionSurfaceIndexResource>.Instance);

        var act = async () => await resource.ReadAsync(
            McpTestFactory.AnonymousHttpContext(),
            "honua://published-services",
            CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
    }

    // ------------------------------------------------------------------
    // Factories
    // ------------------------------------------------------------------

    private PublishedServiceResource BuildPublishedServiceResource()
        => new(_services, _intents, _deployments, _jobService, NullLogger<PublishedServiceResource>.Instance);

    private DeploymentResource BuildDeploymentResource()
        => new(_deployments, _jobService, NullLogger<DeploymentResource>.Instance);

    private static PublishedServiceRecord BuildPublishedService(
        string serviceId,
        string intentId = "intent-1",
        PublishedServiceStatus status = PublishedServiceStatus.Active)
        => new()
        {
            ServiceId = serviceId,
            IntentId = intentId,
            SourceKind = PublishSourceKind.ResultPackage,
            SourceId = "pkg-source",
            TargetKind = PublishTargetKind.FeatureService,
            Status = status,
            PublishedAt = PublishedAt,
            UpdatedAt = UpdatedAt
        };

    private static PublishIntent BuildIntent(
        string intentId,
        PublishSourceKind sourceKind = PublishSourceKind.ResultPackage,
        string sourceId = "pkg-source")
        => new()
        {
            IntentId = intentId,
            SourceKind = sourceKind,
            SourceId = sourceId,
            TargetKind = PublishTargetKind.FeatureService,
            Status = PublishIntentStatus.Completed,
            CreatedAt = PublishedAt,
            UpdatedAt = UpdatedAt
        };

    private static Deployment BuildDeployment(
        string deploymentId,
        DeploymentSource source,
        IReadOnlyList<DeploymentTransition>? transitions = null,
        DeploymentStatus status = DeploymentStatus.Active)
        => new()
        {
            DeploymentId = deploymentId,
            Source = source,
            Target = new DeploymentTarget
            {
                TargetId = "target-1",
                Kind = DeploymentKind.FeatureService,
                HostingMode = HostingMode.ManagedService
            },
            Status = status,
            PublicationState = DeploymentPublicationState.Published,
            CreatedAt = PublishedAt,
            UpdatedAt = UpdatedAt,
            Transitions = transitions ?? []
        };
}
