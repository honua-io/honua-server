// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
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
        extent.Should().NotBeNull();
        if (extent is not { } combined)
        {
            return;
        }

        combined.SpatialReference.Should().Be(4326, "the first resource's CRS anchors the dataset extent");
        combined.MinX.Should().Be(-10);
        combined.MinY.Should().Be(0);
        combined.MaxX.Should().Be(20);
        combined.MaxY.Should().Be(40);
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
        extent.Should().NotBeNull();
        if (extent is not { } combined)
        {
            return;
        }

        combined.MinX.Should().Be(-10);
        combined.MinY.Should().Be(-5);
        combined.MaxX.Should().Be(20);
        combined.MaxY.Should().Be(10);
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
}
