// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using System.Security.Claims;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Protocols.Ogc.Api.Maps.Models;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.Ogc.Api.Maps.Handlers;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Maps;

/// <summary>
/// Tests for <see cref="Honua.Protocols.Ogc.Api.Maps.Handlers.OgcMapsRenderingHandler"/>.
///
/// TODO(#1035 cutover 86/N): the rendering handler was ported to Metadata v2 in the
/// same slice but its (very large) v1-only test class was left to a follow-up port
/// per the cutover strategy (do the source port, leave a focused TODO for the test
/// fixtures, and move on). The original v1 test class verified the collection-,
/// dataset-, and styled-map paths against the v1 catalog graph;
/// rewriting them on the V2 TestMetadataV2GraphBuilder is tracked under task #55
/// (Port test fixtures off v1).
/// </summary>
[Protocol(TestProtocols.OgcApiMaps)]
public class OgcMapsRenderingHandlerTests
{
    [UnitTheory]
    [InlineData("read:catalog/roads", true)]
    [InlineData("read:catalog", true)]
    [InlineData("read:catalog/parcels", false)]
    [InlineData("admin:read", false)]
    public async Task DatasetMap_ScopedKey_RendersOnlyItsGrantedLayer(string permission, bool allowed)
    {
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("resource", "roads", MetadataV2ResourceType.FeatureDataset)
            .AddStorageBinding("binding", "resource", "roads", storageLayerId: 1)
            .AddService("service", "catalog", protocols: ["OGC-API-Maps"])
            .AddPublication("publication", "service", "resource", layerIndex: 1)
            .BuildProvider();
        var renderer = Substitute.For<IRasterMapRenderer>();
        renderer.RenderDatasetMapAsync(Arg.Any<int[]>(), Arg.Any<MapRenderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RasterResult { Data = [1], ContentType = "image/png", Width = 1, Height = 1 });
        using var services = new ServiceCollection().AddLogging()
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>().BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Role, permission == "admin:read" ? AdminApiKeyPermission.ScopedAdminRole : LayerScopedWriteKey.ScopedKeyRole),
                 new Claim("permission", permission)], "ApiKey"))
        };
        var handler = new OgcMapsRenderingHandler(graph, renderer, Substitute.For<IOgcStyleProjection>(),
            NullLogger<OgcMapsRenderingHandler>.Instance);

        await handler.RenderDatasetMapAsync([], new OgcMapRequest { Bbox = "-158,20,-156,22", Width = 1, Height = 1 }, context);

        await renderer.Received(allowed ? 1 : 0).RenderDatasetMapAsync(
            Arg.Is<int[]>(layers => layers.SequenceEqual(new[] { 1 })), Arg.Any<MapRenderRequest>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task CollectionMap_QueryGrant_RendersWhenCoarsePolicyDenies()
    {
        var renderer = Substitute.For<IRasterMapRenderer>();
        renderer.RenderCollectionMapAsync(Arg.Any<int>(), Arg.Any<MapRenderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RasterResult { Data = [1], ContentType = "image/png", Width = 1, Height = 1 });
        var handler = new OgcMapsRenderingHandler(
            RestrictedMapsProvider(),
            renderer,
            Substitute.For<IOgcStyleProjection>(),
            NullLogger<OgcMapsRenderingHandler>.Instance);

        await handler.RenderCollectionMapAsync(
            1,
            new OgcMapRequest { Bbox = "-158,20,-156,22", Width = 1, Height = 1 },
            CreateGrantContext());

        await renderer.Received(1).RenderCollectionMapAsync(1, Arg.Any<MapRenderRequest>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task DatasetMap_QueryGrant_RendersExplicitCollectionWhenCoarsePolicyDenies()
    {
        var renderer = Substitute.For<IRasterMapRenderer>();
        renderer.RenderDatasetMapAsync(Arg.Any<int[]>(), Arg.Any<MapRenderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RasterResult { Data = [1], ContentType = "image/png", Width = 1, Height = 1 });
        var handler = new OgcMapsRenderingHandler(
            RestrictedMapsProvider(),
            renderer,
            Substitute.For<IOgcStyleProjection>(),
            NullLogger<OgcMapsRenderingHandler>.Instance);

        await handler.RenderDatasetMapAsync(
            [1],
            new OgcMapRequest { Bbox = "-158,20,-156,22", Width = 1, Height = 1 },
            CreateGrantContext());

        await renderer.Received(1).RenderDatasetMapAsync(
            Arg.Is<int[]>(layers => layers.SequenceEqual(new[] { 1 })),
            Arg.Any<MapRenderRequest>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task DatasetMap_QueryGrant_RendersDiscoveredCollectionWhenCoarsePolicyDenies()
    {
        var renderer = Substitute.For<IRasterMapRenderer>();
        renderer.RenderDatasetMapAsync(Arg.Any<int[]>(), Arg.Any<MapRenderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RasterResult { Data = [1], ContentType = "image/png", Width = 1, Height = 1 });
        var handler = new OgcMapsRenderingHandler(
            RestrictedMapsProvider(),
            renderer,
            Substitute.For<IOgcStyleProjection>(),
            NullLogger<OgcMapsRenderingHandler>.Instance);

        await handler.RenderDatasetMapAsync(
            [],
            new OgcMapRequest { Bbox = "-158,20,-156,22", Width = 1, Height = 1 },
            CreateGrantContext());

        await renderer.Received(1).RenderDatasetMapAsync(
            Arg.Is<int[]>(layers => layers.SequenceEqual(new[] { 1 })),
            Arg.Any<MapRenderRequest>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task DatasetMap_ForeignTenantLayer_IsNotRendered()
    {
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("own", "own-layer", MetadataV2ResourceType.FeatureDataset)
            .AddStorageBinding("own-binding", "own", "own", storageLayerId: 1)
            .AddService("own-service", "own-service", protocols: ["OGC-API-Maps"])
            .AddPublication("own-publication", "own-service", "own", layerIndex: 1)
            .AddResource("foreign", "foreign-layer", MetadataV2ResourceType.FeatureDataset, accessPolicy: new AccessPolicy { AllowAnonymous = true })
            .AddStorageBinding("foreign-binding", "foreign", "foreign", storageLayerId: 2)
            .AddService("foreign-service", "foreign-service", protocols: ["OGC-API-Maps"])
            .AddPublication("foreign-publication", "foreign-service", "foreign", layerIndex: 2)
            .Build();
        graph = StampResourceTenant(graph, "foreign", "tenant-b");
        var renderer = Substitute.For<IRasterMapRenderer>();
        renderer.RenderDatasetMapAsync(Arg.Any<int[]>(), Arg.Any<MapRenderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RasterResult { Data = [1], ContentType = "image/png", Width = 1, Height = 1 });
        var handler = new OgcMapsRenderingHandler(
            new TestMetadataV2GraphProvider(graph),
            renderer,
            Substitute.For<IOgcStyleProjection>(),
            NullLogger<OgcMapsRenderingHandler>.Instance);

        await handler.RenderDatasetMapAsync(
            [],
            new OgcMapRequest { Bbox = "-158,20,-156,22", Width = 1, Height = 1 },
            CreateTenantContext("tenant-a"));

        await renderer.Received(1).RenderDatasetMapAsync(
            Arg.Is<int[]>(layers => layers.SequenceEqual(new[] { 1 })),
            Arg.Any<MapRenderRequest>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task DatasetMap_OnlyForeignTenantLayers_ReturnsNotFound()
    {
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("foreign", "foreign-layer", MetadataV2ResourceType.FeatureDataset, accessPolicy: new AccessPolicy { AllowAnonymous = true })
            .AddStorageBinding("foreign-binding", "foreign", "foreign", storageLayerId: 2)
            .AddService("foreign-service", "foreign-service", protocols: ["OGC-API-Maps"])
            .AddPublication("foreign-publication", "foreign-service", "foreign", layerIndex: 2)
            .Build();
        graph = StampResourceTenant(graph, "foreign", "tenant-b");
        var renderer = Substitute.For<IRasterMapRenderer>();
        var handler = new OgcMapsRenderingHandler(
            new TestMetadataV2GraphProvider(graph),
            renderer,
            Substitute.For<IOgcStyleProjection>(),
            NullLogger<OgcMapsRenderingHandler>.Instance);

        var result = await handler.RenderDatasetMapAsync(
            [],
            new OgcMapRequest { Bbox = "-158,20,-156,22", Width = 1, Height = 1 },
            CreateTenantContext("tenant-a"));

        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        await renderer.DidNotReceive().RenderDatasetMapAsync(
            Arg.Any<int[]>(),
            Arg.Any<MapRenderRequest>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public void IsStyleAssociatedWithResource_DoesNotTreatMatchingResourceNameAsAssociation()
    {
        const string styleId = "roads";
        var style = MetadataV2StyleResourceFactory.BuildStyleResource(
            styleId,
            "{\"version\":8,\"layers\":[]}",
            title: null,
            description: null,
            drawingInfoJson: null,
            styleVersion: 1,
            createdAt: DateTimeOffset.UnixEpoch,
            updatedAt: DateTimeOffset.UnixEpoch);
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-roads", Name = styleId },
            StyleResourceIds = []
        };

        var unassociatedSnapshot = new MetadataV2GraphSnapshot(
            new MetadataV2Graph { Resources = [resource, style] },
            "\"test\"",
            DateTimeOffset.UnixEpoch);

        OgcMapsRenderingHandler.IsStyleAssociatedWithResource(
            unassociatedSnapshot,
            resource,
            styleId).Should().BeFalse();

        var associatedResource = resource with { StyleResourceIds = [style.Metadata.Id] };
        var associatedSnapshot = new MetadataV2GraphSnapshot(
            new MetadataV2Graph { Resources = [associatedResource, style] },
            "\"test\"",
            DateTimeOffset.UnixEpoch);

        OgcMapsRenderingHandler.IsStyleAssociatedWithResource(
            associatedSnapshot,
            associatedResource,
            styleId).Should().BeTrue();
    }

    [global::Xunit.Trait("Tier", "Fast")]
    [Fact]
    public void IsStyleAssociatedWithResource_ReferencedDataResource_ReturnsFalse()
    {
        const string styleId = "roads";
        var referencedDataResource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "not-a-style", Name = styleId },
            Type = MetadataV2ResourceType.FeatureDataset
        };
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-roads", Name = "collection" },
            StyleResourceIds = [referencedDataResource.Metadata.Id]
        };
        var snapshot = new MetadataV2GraphSnapshot(
            new MetadataV2Graph { Resources = [resource, referencedDataResource] },
            "\"test\"",
            DateTimeOffset.UnixEpoch);

        OgcMapsRenderingHandler.IsStyleAssociatedWithResource(snapshot, resource, styleId)
            .Should().BeFalse();
    }

    [global::Xunit.Trait("Tier", "Fast")]
    [Fact]
    public void IsStyleAssociatedWithResource_MissingReferencedResource_ReturnsFalse()
    {
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-roads", Name = "roads" },
            StyleResourceIds = ["missing-style"]
        };
        var snapshot = new MetadataV2GraphSnapshot(
            new MetadataV2Graph { Resources = [resource] },
            "\"test\"",
            DateTimeOffset.UnixEpoch);

        OgcMapsRenderingHandler.IsStyleAssociatedWithResource(snapshot, resource, "missing")
            .Should().BeFalse();
    }

    // #5048: a mixed-CRS dataset returned HTTP 500 from the dataset-map route because the
    // default viewport was combined through the synchronous transformer, which throws for
    // CRS pairs the canonical transform service resolves. These cover the combination itself;
    // the endpoint's status codes and landing metadata are covered by the Maps integration
    // suite.
    [UnitTest]
    public async Task BuildDatasetExtentAsync_MixedCrs_CombinesThroughTheCanonicalTransformService()
    {
        var geographic = ResourceWithBounds("geographic", 4326, -10, 0, 20, 30);
        var projected = ResourceWithBounds("projected", 27700, 100000, 10000, 200000, 20000);
        // The canonical service is the only thing that can resolve 27700 -> 4326 here.
        var transform = new StubCoordinateTransformService((-5, 10, 5, 40));

        var (extent, transformUnavailable) = await OgcMapsResourceResolver.BuildDatasetExtentAsync(
            [geographic, projected], transform, CancellationToken.None);

        transformUnavailable.Should().BeFalse();
        var bounds = extent ?? throw new InvalidOperationException("dataset extent was not produced");
        bounds.SpatialReference.Should().Be(4326, "the first resource's CRS anchors the dataset extent");
        bounds.MinX.Should().Be(-10);
        bounds.MinY.Should().Be(0);
        bounds.MaxX.Should().Be(20);
        bounds.MaxY.Should().Be(40);
    }

    [UnitTest]
    public async Task BuildDatasetExtentAsync_UntransformablePair_ReportsUnavailableInsteadOfThrowing()
    {
        var geographic = ResourceWithBounds("geographic", 4326, -10, 0, 20, 30);
        var projected = ResourceWithBounds("projected", 27700, 100000, 10000, 200000, 20000);
        var transform = new StubCoordinateTransformService(null);

        var (extent, transformUnavailable) = await OgcMapsResourceResolver.BuildDatasetExtentAsync(
            [geographic, projected], transform, CancellationToken.None);

        transformUnavailable.Should().BeTrue();
        extent.Should().BeNull("an untransformable dataset must not be given an invented extent");
    }

    [UnitTest]
    public async Task BuildDatasetExtentAsync_SingleCrs_NeedsNoTransformService()
    {
        var west = ResourceWithBounds("west", 4326, -10, 0, 0, 10);
        var east = ResourceWithBounds("east", 4326, 5, -5, 20, 8);

        var (extent, transformUnavailable) = await OgcMapsResourceResolver.BuildDatasetExtentAsync(
            [west, east], coordinateTransformService: null, CancellationToken.None);

        transformUnavailable.Should().BeFalse();
        var bounds = extent ?? throw new InvalidOperationException("dataset extent was not produced");
        bounds.MinX.Should().Be(-10);
        bounds.MinY.Should().Be(-5);
        bounds.MaxX.Should().Be(20);
        bounds.MaxY.Should().Be(10);
    }

    [UnitTest]
    public async Task BuildDatasetExtentAsync_NoDeclaredBounds_ReturnsNoExtentAndNoFailure()
    {
        var undeclared = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-undeclared", Name = "undeclared" },
            Type = MetadataV2ResourceType.FeatureDataset
        };

        var (extent, transformUnavailable) = await OgcMapsResourceResolver.BuildDatasetExtentAsync(
            [undeclared], coordinateTransformService: null, CancellationToken.None);

        extent.Should().BeNull();
        transformUnavailable.Should().BeFalse("a resource that declares no bounds is not a transform failure");
    }

    [UnitTest]
    public async Task BuildDatasetExtentAsync_CachesResourceTransformsOnlyForTheSameSnapshot()
    {
        var geographic = ResourceWithBounds("geographic", 4326, -10, 0, 20, 30);
        var projected = ResourceWithBounds("projected", 27700, 100000, 10000, 200000, 20000);
        var snapshot = new MetadataV2GraphSnapshot(
            new MetadataV2Graph { Resources = [geographic, projected] }, "revision-one", DateTimeOffset.UnixEpoch);
        var transform = new StubCoordinateTransformService((-5, 10, 5, 40));

        var first = await OgcMapsResourceResolver.BuildDatasetExtentAsync(
            [geographic, projected], transform, CancellationToken.None, snapshot);
        var repeated = await OgcMapsResourceResolver.BuildDatasetExtentAsync(
            [geographic, projected], transform, CancellationToken.None, snapshot);

        repeated.Should().Be(first);
        transform.ExtentCalls.Should().Be(1);

        var changedProjected = ResourceWithBounds("projected", 27700, 300000, 10000, 400000, 20000);
        var nextSnapshot = new MetadataV2GraphSnapshot(
            new MetadataV2Graph { Resources = [geographic, changedProjected] }, "revision-two", DateTimeOffset.UnixEpoch);
        await OgcMapsResourceResolver.BuildDatasetExtentAsync(
            [geographic, changedProjected], transform, CancellationToken.None, nextSnapshot);

        transform.ExtentCalls.Should().Be(2, "a changed metadata snapshot must recompute the transform");
    }

    [UnitTest]
    public async Task BuildDatasetExtentAsync_RetriesUnavailableTransformForTheSameSnapshot()
    {
        var geographic = ResourceWithBounds("geographic", 4326, -10, 0, 20, 30);
        var projected = ResourceWithBounds("projected", 27700, 100000, 10000, 200000, 20000);
        var snapshot = new MetadataV2GraphSnapshot(
            new MetadataV2Graph { Resources = [geographic, projected] }, "revision-one", DateTimeOffset.UnixEpoch);
        var transform = new StubCoordinateTransformService(null);

        var unavailable = await OgcMapsResourceResolver.BuildDatasetExtentAsync(
            [geographic, projected], transform, CancellationToken.None, snapshot);
        transform.Result = (-5, 10, 5, 40);
        var recovered = await OgcMapsResourceResolver.BuildDatasetExtentAsync(
            [geographic, projected], transform, CancellationToken.None, snapshot);

        unavailable.TransformUnavailable.Should().BeTrue();
        recovered.TransformUnavailable.Should().BeFalse();
        recovered.Extent.Should().NotBeNull();
        transform.ExtentCalls.Should().Be(2);
    }

    private static TestMetadataV2GraphProvider RestrictedMapsProvider()
    {
        var restricted = new AccessPolicy { AllowedRoles = ["gis-admin"] };
        return new TestMetadataV2GraphBuilder()
            .AddResource("resource", "roads", MetadataV2ResourceType.FeatureDataset, accessPolicy: restricted)
            .AddStorageBinding("binding", "resource", "roads", storageLayerId: 1)
            .AddService("service", "catalog", protocols: ["OGC-API-Maps"])
            .AddPublication("publication", "service", "resource", layerIndex: 1)
            .BuildProvider();
    }

    private static DefaultHttpContext CreateGrantContext()
    {
        var services = new ServiceCollection().AddLogging()
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
            .AddSingleton<IOptions<RbacOptions>>(Options.Create(new RbacOptions()))
            .AddSingleton<IPermissionResolver>(new AllowQueryResolver())
            .BuildServiceProvider();
        return new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "analyst")], "Test"))
        };
    }

    private static DefaultHttpContext CreateTenantContext(string tenantId)
    {
        var services = new ServiceCollection().AddLogging()
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
            .AddSingleton<ITenantContext>(new FixedTenantContext(tenantId))
            .BuildServiceProvider();
        return new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "tenant-user")], "Test"))
        };
    }

    private static MetadataV2Graph StampResourceTenant(MetadataV2Graph graph, string resourceId, string tenant)
        => graph with
        {
            Resources = graph.Resources.Select(resource => resource.Metadata.Id == resourceId
                ? resource with { Metadata = resource.Metadata with { Tenant = tenant } }
                : resource).ToArray()
        };

    private static MetadataV2Resource ResourceWithBounds(
        string id, int srid, double west, double south, double east, double north)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = $"res-{id}", Name = id },
            Type = MetadataV2ResourceType.FeatureDataset,
            Spatial = new MetadataV2ResourceSpatial
            {
                SpatialReference = new MetadataV2SpatialReference { Srid = srid },
                GeometryType = MetadataV2GeometryType.Point,
                PrimaryGeometryField = "shape",
                Bbox = new MetadataV2Bbox { West = west, South = south, East = east, North = north }
            }
        };

    private sealed class StubCoordinateTransformService(
        (double MinX, double MinY, double MaxX, double MaxY)? transformedExtent) : ICoordinateTransformService
    {
        public (double MinX, double MinY, double MaxX, double MaxY)? Result { get; set; } = transformedExtent;

        public int ExtentCalls { get; private set; }

        public ValueTask<(double MinX, double MinY, double MaxX, double MaxY)?> TransformExtentAsync(
            double minX,
            double minY,
            double maxX,
            double maxY,
            int fromSrid,
            int toSrid,
            CancellationToken cancellationToken = default)
        {
            ExtentCalls++;
            return ValueTask.FromResult(Result);
        }

        public ValueTask<(double X, double Y)?> TransformPointAsync(
            double x,
            double y,
            int fromSrid,
            int toSrid,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<(double X, double Y)?>(null);
    }

    private sealed class AllowQueryResolver : IPermissionResolver
    {
        public PermissionDecision Authorize(
            EffectivePermissions effectivePermissions,
            string service,
            string? layer,
            AuthorizationOperation operation)
            => PermissionDecision.NoMatch();

        public Task<PermissionDecision> AuthorizeAsync(
            string userId,
            IReadOnlyList<string> roles,
            string service,
            string? layer,
            AuthorizationOperation operation,
            bool isAuthenticated,
            CancellationToken cancellationToken = default)
            => Task.FromResult(operation == AuthorizationOperation.Query
                ? PermissionDecision.Allow(new PermissionGrant
                {
                    Service = service,
                    Layer = layer ?? "*",
                    Operation = "query"
                })
                : PermissionDecision.NoMatch());
    }

    private sealed class FixedTenantContext(string tenantId) : ITenantContext
    {
        public string? TenantId { get; } = tenantId;

        public TenantContextSource Source => TenantContextSource.Claim;

        public bool RequireTenantId(out string outTenantId, out string? reason)
        {
            outTenantId = TenantId!;
            reason = null;
            return true;
        }
    }
}
