// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Resources;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Deployment.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Publishing.Abstractions;
using Honua.Core.Features.Publishing.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

[Protocol(TestProtocols.Mcp)]
[Endpoint("POST /mcp resources/read honua://published-services/{serviceId}")]
[Endpoint("POST /mcp resources/read honua://published-services")]
public sealed class McpPublishedCatalogReadbackTests
{
    private const string ServiceName = "roads";
    private const string ServiceUri = "honua://published-services/roads";
    private readonly IPublishedServiceStore _promotions = Substitute.For<IPublishedServiceStore>();
    private readonly IDeploymentStore _deployments = Substitute.For<IDeploymentStore>();
    private readonly IGeoprocessingJobService _jobs = Substitute.For<IGeoprocessingJobService>();

    public McpPublishedCatalogReadbackTests()
    {
        _promotions.ListActiveAsync(Arg.Any<CancellationToken>()).Returns([]);
        _deployments.ListBySourceAsync(DeploymentSourceKind.PublishedService, ServiceName,
            Arg.Any<CancellationToken>()).Returns([]);
    }

    [UnitTest]
    public async Task Read_PersistedCatalogFromFreshProvider_ExposesPublicIdentityWithoutInventedPromotion()
    {
        var graph = Catalog();
        var first = await ReadAsync(graph);
        // A second provider/handler has no process-local publishing receipt.
        var reopened = await ReadAsync(graph);

        reopened.GetProperty("resourceUri").GetString().Should().Be(ServiceUri);
        reopened.GetProperty("serviceId").GetString().Should().Be(ServiceName);
        reopened.GetProperty("metadataServiceId").GetString().Should().Be("service-internal-id");
        reopened.GetProperty("layerIds")[0].GetInt32().Should().Be(3,
            "the public layer index is 3 while the storage layer handle is 741");
        reopened.GetProperty("servingLayerIds")[0].GetInt32().Should().Be(3);
        reopened.GetProperty("etag").GetString().Should().Be(first.GetProperty("etag").GetString());
        reopened.GetProperty("endpoint").GetString().Should().Be("/rest/services/roads/FeatureServer");
        reopened.GetProperty("provenance").TryGetProperty("originatingIntentId", out _).Should().BeFalse();
        reopened.GetProperty("provenance").TryGetProperty("resultPackageId", out _).Should().BeFalse();
    }

    [UnitTest]
    public async Task Index_CatalogPublication_AdvertisesSameReadableUriAndEtag()
    {
        var graph = Catalog();
        var detail = await ReadAsync(graph);
        var context = Context(graph);
        var index = new PromotionSurfaceIndexResource(
            _promotions, _deployments, _jobs, NullLogger<PromotionSurfaceIndexResource>.Instance);

        var result = await index.ReadAsync(context, "honua://published-services", CancellationToken.None);
        var body = McpTestFactory.ParseJson(result.Contents[0].Text);
        body.GetProperty("count").GetInt32().Should().Be(1);
        var item = body.GetProperty("items")[0];
        item.GetProperty("resourceUri").GetString().Should().Be(ServiceUri);
        item.GetProperty("etag").GetString().Should().Be(detail.GetProperty("etag").GetString());
    }

    [UnitTest]
    public async Task Read_GlobalPublishedServiceGrant_DoesNotBypassLayerPolicy()
    {
        var graph = Catalog(new AccessPolicy { AllowedRoles = ["private-road-reader"] });
        var read = async () => await ReadAsync(graph);

        await read.Should().ThrowAsync<GeoprocessingAuthorizationException>();
    }

    [UnitTest]
    public async Task Read_ServiceWithoutPersistedStorageBinding_IsNotAPublishedService()
    {
        var graph = Catalog() with { StorageBindings = [] };
        var read = async () => await ReadAsync(graph);

        await read.Should().ThrowAsync<GeoprocessingNotFoundException>();
    }

    [UnitTest]
    public async Task Read_CollidingPromotionIdentity_RefusesToReturnAnUnrelatedService()
    {
        _promotions.GetAsync(ServiceName, Arg.Any<CancellationToken>()).Returns(new PublishedServiceRecord
        {
            ServiceId = ServiceName,
            IntentId = "another-intent",
            SourceKind = PublishSourceKind.ResultPackage,
            SourceId = "another-package",
            TargetKind = PublishTargetKind.FeatureService,
            Status = PublishedServiceStatus.Active,
            PublishedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch
        });
        var read = async () => await ReadAsync(Catalog());

        await read.Should().ThrowAsync<GeoprocessingValidationException>();
    }

    [UnitTest]
    public async Task Index_RestrictedLayer_DoesNotExposeServiceMetadata()
    {
        var graph = Catalog(new AccessPolicy { AllowedRoles = ["private-road-reader"] });
        var index = new PromotionSurfaceIndexResource(
            _promotions, _deployments, _jobs, NullLogger<PromotionSurfaceIndexResource>.Instance);

        var result = await index.ReadAsync(Context(graph), "honua://published-services", CancellationToken.None);
        var body = McpTestFactory.ParseJson(result.Contents[0].Text);
        body.GetProperty("count").GetInt32().Should().Be(0);
        body.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [UnitTest]
    public async Task Read_StoppedService_RetainsPublicationWithoutClaimingItServes()
    {
        var graph = Catalog();
        graph = graph with
        {
            Services =
            [
                graph.Services[0] with
                {
                    Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Draft },
                    Protocols = []
                }
            ]
        };

        var detail = await ReadAsync(graph);
        detail.GetProperty("status").GetString().Should().Be("Draft");
        detail.GetProperty("featureServerEnabled").GetBoolean().Should().BeFalse();
        detail.GetProperty("serving").GetBoolean().Should().BeFalse();
        detail.GetProperty("layerIds")[0].GetInt32().Should().Be(3);
        detail.GetProperty("servingLayerIds").GetArrayLength().Should().Be(0);

        var index = new PromotionSurfaceIndexResource(
            _promotions, _deployments, _jobs, NullLogger<PromotionSurfaceIndexResource>.Instance);
        var listed = await index.ReadAsync(Context(graph), "honua://published-services", CancellationToken.None);
        McpTestFactory.ParseJson(listed.Contents[0].Text).GetProperty("count").GetInt32().Should().Be(0);
    }

    [UnitTest]
    public async Task Read_TenantScopedService_DoesNotLeakAcrossRequests()
    {
        var graph = Catalog();
        graph = graph with
        {
            Services = [graph.Services[0] with
            {
                Metadata = graph.Services[0].Metadata with { Tenant = "other-tenant" }
            }]
        };

        var read = async () => await ReadAsync(graph);
        await read.Should().ThrowAsync<GeoprocessingNotFoundException>();
    }

    [UnitTest]
    public async Task Read_CrossResourceStorageBinding_RefusesFalsePublication()
    {
        var graph = Catalog();
        graph = graph with
        {
            StorageBindings = [graph.StorageBindings[0] with { ResourceId = "unrelated-resource" }]
        };

        var read = async () => await ReadAsync(graph);
        await read.Should().ThrowAsync<GeoprocessingNotFoundException>();
    }

    [UnitTest]
    public async Task Index_CollidingPromotionIdentity_RefusesUnreadableUri()
    {
        _promotions.GetAsync(ServiceName, Arg.Any<CancellationToken>()).Returns(new PublishedServiceRecord
        {
            ServiceId = ServiceName,
            IntentId = "another-intent",
            SourceKind = PublishSourceKind.ResultPackage,
            SourceId = "another-package",
            TargetKind = PublishTargetKind.FeatureService,
            Status = PublishedServiceStatus.Active,
            PublishedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch
        });
        var index = new PromotionSurfaceIndexResource(
            _promotions, _deployments, _jobs, NullLogger<PromotionSurfaceIndexResource>.Instance);
        var read = async () => await index.ReadAsync(Context(Catalog()),
            "honua://published-services", CancellationToken.None);

        await read.Should().ThrowAsync<GeoprocessingValidationException>();
    }

    [UnitTest]
    public async Task Index_ActivePromotionAndStoppedCatalogCollision_RefusesUnreadableUri()
    {
        var promotion = Promotion();
        _promotions.GetAsync(ServiceName, Arg.Any<CancellationToken>()).Returns(promotion);
        _promotions.ListActiveAsync(Arg.Any<CancellationToken>()).Returns([promotion]);
        var graph = Catalog();
        graph = graph with
        {
            Services = [graph.Services[0] with
            {
                Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Draft },
                Protocols = []
            }]
        };
        var index = new PromotionSurfaceIndexResource(
            _promotions, _deployments, _jobs, NullLogger<PromotionSurfaceIndexResource>.Instance);
        var read = async () => await index.ReadAsync(Context(graph),
            "honua://published-services", CancellationToken.None);

        await read.Should().ThrowAsync<GeoprocessingValidationException>();
    }

    [UnitTest]
    public async Task Read_PromotionWithRestrictedCatalog_RemainsReadableUnderPromotionGrant()
    {
        var promotion = Promotion();
        _promotions.GetAsync(ServiceName, Arg.Any<CancellationToken>()).Returns(promotion);
        _promotions.ListActiveAsync(Arg.Any<CancellationToken>()).Returns([promotion]);
        var graph = Catalog(new AccessPolicy { AllowedRoles = ["private-road-reader"] });
        var index = new PromotionSurfaceIndexResource(
            _promotions, _deployments, _jobs, NullLogger<PromotionSurfaceIndexResource>.Instance);

        var listed = await index.ReadAsync(Context(graph),
            "honua://published-services", CancellationToken.None);
        McpTestFactory.ParseJson(listed.Contents[0].Text).GetProperty("count").GetInt32().Should().Be(1);
        (await ReadAsync(graph)).GetProperty("sourceId").GetString().Should().Be("another-package");
    }

    [UnitTest]
    public async Task Index_UnpublishedServiceWithSameName_DoesNotHidePublishedService()
    {
        var graph = Catalog();
        graph = graph with
        {
            Services =
            [
                graph.Services[0],
                graph.Services[0] with
                {
                    Metadata = graph.Services[0].Metadata with { Id = "unpublished-service" }
                }
            ]
        };
        var index = new PromotionSurfaceIndexResource(
            _promotions, _deployments, _jobs, NullLogger<PromotionSurfaceIndexResource>.Instance);

        var result = await index.ReadAsync(Context(graph), "honua://published-services", CancellationToken.None);
        McpTestFactory.ParseJson(result.Contents[0].Text).GetProperty("count").GetInt32().Should().Be(1);
        (await ReadAsync(graph)).GetProperty("serviceId").GetString().Should().Be(ServiceName);
    }

    [UnitTest]
    public async Task Read_TwoPublishedServicesWithSameName_RefusesAmbiguousIdentity()
    {
        var graph = Catalog();
        graph = graph with
        {
            Services =
            [
                graph.Services[0],
                graph.Services[0] with
                {
                    Metadata = graph.Services[0].Metadata with { Id = "another-published-service" }
                }
            ],
            Publications =
            [
                graph.Publications[0],
                graph.Publications[0] with
                {
                    Metadata = graph.Publications[0].Metadata with { Id = "another-publication" },
                    ServiceId = "another-published-service"
                }
            ]
        };

        var read = async () => await ReadAsync(graph);
        await read.Should().ThrowAsync<GeoprocessingValidationException>();
    }

    [UnitTest]
    public async Task Read_DuplicatePublicLayerIndex_RefusesAmbiguousPublishedLayer()
    {
        var graph = Catalog();
        graph = graph with
        {
            Publications =
            [
                graph.Publications[0],
                graph.Publications[0] with
                {
                    Metadata = graph.Publications[0].Metadata with { Id = "duplicate-layer" }
                }
            ]
        };

        var read = async () => await ReadAsync(graph);
        await read.Should().ThrowAsync<GeoprocessingValidationException>();
    }

    [UnitTest]
    public async Task Read_ForeignTenantDuplicate_DoesNotRevealHiddenGraphShape()
    {
        var graph = Catalog();
        graph = graph with
        {
            Publications =
            [
                graph.Publications[0],
                graph.Publications[0] with
                {
                    Metadata = graph.Publications[0].Metadata with
                    {
                        Id = "foreign-tenant-layer", Tenant = "other-tenant"
                    }
                }
            ]
        };

        (await ReadAsync(graph)).GetProperty("layerIds").GetArrayLength().Should().Be(1);
    }

    [UnitTest]
    public void Uri_ServiceNameEncoding_DistinguishesSlashFromLiteralEscape()
    {
        var slash = McpResourceUris.PublishedServiceUri("roads/area");
        var percent = McpResourceUris.PublishedServiceUri("roads%2Farea");
        var spaced = McpResourceUris.PublishedServiceUri("roads area");

        slash.Should().Be("honua://published-services/roads%2Farea");
        percent.Should().Be("honua://published-services/roads%252Farea");
        spaced.Should().Be("honua://published-services/roads%20area");
        McpResourceUris.TryParsePublishedServiceUri(slash, out var decodedSlash).Should().BeTrue();
        McpResourceUris.TryParsePublishedServiceUri(percent, out var decodedPercent).Should().BeTrue();
        decodedSlash.Should().Be("roads/area");
        decodedPercent.Should().Be("roads%2Farea");
        var resource = new PublishedServiceResource(
            _promotions, _deployments, _jobs, NullLogger<PublishedServiceResource>.Instance);
        resource.CanHandle(slash).Should().BeTrue();
        resource.CanHandle(percent).Should().BeTrue();
        resource.CanHandle("honua://published-services/roads/area").Should().BeFalse();
        McpResourceUris.TryParsePublishedServiceUri(
            "honua://published-services/roads%2Farea/child", out _).Should().BeFalse();
        McpResourceUris.TryParsePublishedServiceUri(
            "honua://published-services/roads%", out _).Should().BeFalse();
    }

    [UnitTest]
    public async Task Read_EncodedNames_RoundTripToDistinctPersistedServices()
    {
        var graph = Catalog();
        graph = graph with
        {
            Services =
            [
                graph.Services[0] with
                {
                    Metadata = graph.Services[0].Metadata with { Name = "roads/area" }
                },
                graph.Services[0] with
                {
                    Metadata = graph.Services[0].Metadata with
                    {
                        Id = "literal-escape-service", Name = "roads%2Farea"
                    }
                }
            ],
            Publications =
            [
                graph.Publications[0],
                graph.Publications[0] with
                {
                    Metadata = graph.Publications[0].Metadata with { Id = "literal-escape-publication" },
                    ServiceId = "literal-escape-service"
                }
            ]
        };
        var resource = new PublishedServiceResource(
            _promotions, _deployments, _jobs, NullLogger<PublishedServiceResource>.Instance);

        var slashUri = McpResourceUris.PublishedServiceUri("roads/area");
        var percentUri = McpResourceUris.PublishedServiceUri("roads%2Farea");
        var slash = await resource.ReadAsync(Context(graph), slashUri, CancellationToken.None);
        var percent = await resource.ReadAsync(Context(graph), percentUri, CancellationToken.None);

        McpTestFactory.ParseJson(slash.Contents[0].Text).GetProperty("serviceId").GetString()
            .Should().Be("roads/area");
        McpTestFactory.ParseJson(percent.Contents[0].Text).GetProperty("serviceId").GetString()
            .Should().Be("roads%2Farea");
    }

    private async Task<System.Text.Json.JsonElement> ReadAsync(MetadataV2Graph graph)
    {
        var resource = new PublishedServiceResource(
            _promotions, _deployments, _jobs, NullLogger<PublishedServiceResource>.Instance);
        var result = await resource.ReadAsync(Context(graph), ServiceUri, CancellationToken.None);
        return McpTestFactory.ParseJson(result.Contents[0].Text);
    }

    private static Microsoft.AspNetCore.Http.DefaultHttpContext Context(MetadataV2Graph graph)
        => McpTestFactory.AuthenticatedHttpContextWithServices(services =>
        {
            services.AddSingleton<IMetadataV2GraphProvider>(new TestMetadataV2GraphProvider(graph));
            services.AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>();
            services.AddOptions<RbacOptions>();
        });

    private static MetadataV2Graph Catalog(AccessPolicy? policy = null)
        => new TestMetadataV2GraphBuilder()
            .AddResource("road-data", "Roads", accessPolicy: policy)
            .AddStorageBinding("road-binding", "road-data", "public.roads", storageLayerId: 741)
            .AddService("service-internal-id", ServiceName,
                route: "/rest/services/roads/FeatureServer", protocols: ServiceProtocols.All)
            .AddPublication("road-publication", "service-internal-id", "road-data",
                layerIndex: 3, storageBindingId: "road-binding",
                publicationType: MetadataV2PublicationType.EsriFeatureLayer)
            .Build();

    private static PublishedServiceRecord Promotion() => new()
    {
        ServiceId = ServiceName,
        IntentId = "another-intent",
        SourceKind = PublishSourceKind.ResultPackage,
        SourceId = "another-package",
        TargetKind = PublishTargetKind.FeatureService,
        Status = PublishedServiceStatus.Active,
        PublishedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch
    };
}
