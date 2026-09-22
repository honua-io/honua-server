// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.ReadOnlyProviders;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.VectorTileServer.Models;
using Honua.Protocols.GeoServices.VectorTileServer.Services;
using Xunit;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.VectorTileServer;

/// <summary>
/// Unit tests for <see cref="VectorTileServerExtentResolver"/>: the service extent is reported in
/// the tiling scheme's spatial reference whatever spatial reference each resource declares.
/// </summary>
public sealed class VectorTileServerExtentResolverTests
{
    private static readonly VectorTileSpatialReference _tilingSpatialReference =
        VectorTileServerTileInfoBuilder.Build(0).SpatialReference!;

    [Fact]
    public async Task ResolveAsync_MixedResourceSpatialReferences_UnionsInTilingScheme()
    {
        var resources = new[]
        {
            Resource(MetadataV2SpatialReference.Wgs84, -123, 37, -122, 38),
            Resource(MetadataV2SpatialReference.WebMercator, -13700000, 4400000, -13690000, 4410000),
        };

        var extent = await VectorTileServerExtentResolver.ResolveAsync(
            resources,
            MetadataV2SpatialReference.Wgs84,
            _tilingSpatialReference,
            new WellKnownCoordinateTransformService(),
            CancellationToken.None);

        extent.Should().NotBeNull();
        extent!.SpatialReference!.Wkid.Should().Be(102100);
        extent.SpatialReference.LatestWkid.Should().Be(3857);
        extent.Xmin.Should().BeApproximately(-13700000, 0.01);
        extent.Ymin.Should().BeApproximately(4400000, 0.01);
        extent.Xmax.Should().BeApproximately(-13580977.8768, 0.01);
        extent.Ymax.Should().BeApproximately(4579425.8129, 0.01);
    }

    [Fact]
    public async Task ResolveAsync_ResourceWithoutSpatialReference_UsesServiceSpatialReference()
    {
        var transforms = new RecordingTransformService((10, 20, 30, 40));

        var extent = await VectorTileServerExtentResolver.ResolveAsync(
            [Resource(spatialReference: null, 1, 2, 3, 4)],
            new MetadataV2SpatialReference { Srid = 2227 },
            _tilingSpatialReference,
            transforms,
            CancellationToken.None);

        extent.Should().NotBeNull();
        (extent!.Xmin, extent.Ymin, extent.Xmax, extent.Ymax).Should().Be((10d, 20d, 30d, 40d));
        extent.SpatialReference!.Wkid.Should().Be(102100);
        transforms.Calls.Should().Equal((1d, 2d, 3d, 4d, 2227, 3857));
    }

    [Fact]
    public async Task ResolveAsync_BboxThatCannotBeProjected_IsOmittedRatherThanMislabelled()
    {
        var transforms = new RecordingTransformService(result: null);

        var extent = await VectorTileServerExtentResolver.ResolveAsync(
            [Resource(new MetadataV2SpatialReference { Srid = 2227 }, 6000000, 2000000, 6100000, 2100000)],
            MetadataV2SpatialReference.Wgs84,
            _tilingSpatialReference,
            transforms,
            CancellationToken.None);

        extent.Should().BeNull();
    }

    private static MetadataV2Resource Resource(
        MetadataV2SpatialReference? spatialReference,
        double west,
        double south,
        double east,
        double north) => new()
        {
            Metadata = new() { Id = "res-" + west.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            Type = MetadataV2ResourceType.FeatureDataset,
            Spatial = new MetadataV2ResourceSpatial
            {
                SpatialReference = spatialReference,
                GeometryType = MetadataV2GeometryType.Point,
                Bbox = new MetadataV2Bbox { West = west, South = south, East = east, North = north },
            },
        };

    private sealed class RecordingTransformService((double, double, double, double)? result) : ICoordinateTransformService
    {
        public List<(double MinX, double MinY, double MaxX, double MaxY, int FromSrid, int ToSrid)> Calls { get; } = [];

        public ValueTask<(double MinX, double MinY, double MaxX, double MaxY)?> TransformExtentAsync(
            double minX, double minY, double maxX, double maxY,
            int fromSrid, int toSrid,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((minX, minY, maxX, maxY, fromSrid, toSrid));
            return ValueTask.FromResult(result);
        }

        public ValueTask<(double X, double Y)?> TransformPointAsync(
            double x, double y,
            int fromSrid, int toSrid,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
