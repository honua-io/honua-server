// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.ReadOnlyProviders;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Protocols.GeoServices.MapServer;
using Honua.Protocols.GeoServices.MapServer.Models;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.MapServer;

/// <summary>
/// A 4326 layer whose latitude reaches ±90 has no finite Web Mercator equivalent.
/// The cached MapServer builder must still advertise that sublayer in the 3857
/// cache square, with Query intact and the tile cache left on (#5556).
/// </summary>
public sealed class MapServerCachedLayerExtentTests
{
    [UnitTest]
    public async Task PolarWgs84Layer_CachedMapExtent_StaysInsideTheAdvertisedSquare()
    {
        var (service, publication, resource, snapshot) = Layer(-180, -90, 180, 90);
        var transforms = new WellKnownCoordinateTransformService();

        var extent = await MapServerEndpoints.ResolveCachedExtentAsync(
            service, [resource], transforms, CancellationToken.None);
        var layer = MapServerEndpoints.MapLayerToMapServerLayerResponse(
            service, publication, resource, snapshot, maxRecordCount: 2000, extent);
        var map = MapServerEndpoints.MapServiceToMapServerResponse(
            service,
            [new MapServerEndpoints.MapServerMetadataLayerDescriptor(0, 0, "points", publication, resource)],
            extent,
            maxRecordCount: 2000,
            maxTileZoom: 0,
            maxExportTilesCount: 100000,
            timeInfo: null,
            supportedExtensions: "");

        var limit = SpatialConstants.WebMercatorExtent;
        map.SingleFusedMapCache.Should().BeTrue();
        map.TileInfo.Should().NotBeNull();
        map.SpatialReference!.Wkid.Should().Be(MapServerEndpoints.CachedMapSpatialReferenceId);
        map.SpatialReference.LatestWkid.Should().Be(MapServerEndpoints.CachedMapSpatialReferenceId);
        map.FullExtent!.SpatialReference.Wkid.Should().Be(MapServerEndpoints.CachedMapSpatialReferenceId);

        layer.Type.Should().Be("Feature Layer");
        layer.Capabilities.Should().Be("Map,Query,Data");
        layer.SupportsStatistics.Should().BeTrue();
        layer.SourceSpatialReference!.Wkid.Should().Be(4326);
        layer.SpatialReference!.Wkid.Should().Be(map.SpatialReference.Wkid);
        layer.SpatialReference.LatestWkid.Should().Be(map.SpatialReference.LatestWkid);
        layer.Extent.Should().NotBeNull();
        layer.Extent!.SpatialReference.Wkid.Should().Be(map.FullExtent.SpatialReference.Wkid);

        AssertInsideSquare(layer.Extent, limit);
        AssertInsideSquare(map.FullExtent, limit);
        layer.Extent.Xmin.Should().Be(map.FullExtent.Xmin);
        layer.Extent.Ymin.Should().Be(map.FullExtent.Ymin);
        layer.Extent.Xmax.Should().Be(map.FullExtent.Xmax);
        layer.Extent.Ymax.Should().Be(map.FullExtent.Ymax);
        layer.Extent.Ymax.Should().BeGreaterThan(limit - 1);
        layer.Extent.Xmax.Should().BeGreaterThan(limit - 1);
    }

    [UnitTest]
    public async Task RegionalWgs84Layer_CachedMapExtent_StaysTheProjectedBbox()
    {
        var (service, publication, resource, snapshot) = Layer(-123, 37, -122, 38);
        var extent = await MapServerEndpoints.ResolveCachedExtentAsync(
            service, [resource], new WellKnownCoordinateTransformService(), CancellationToken.None);
        var layer = MapServerEndpoints.MapLayerToMapServerLayerResponse(
            service, publication, resource, snapshot, maxRecordCount: 2000, extent);

        var limit = SpatialConstants.WebMercatorExtent;
        layer.Capabilities.Should().Be("Map,Query,Data");
        layer.SpatialReference!.Wkid.Should().Be(MapServerEndpoints.CachedMapSpatialReferenceId);
        layer.SourceSpatialReference!.Wkid.Should().Be(4326);
        AssertInsideSquare(layer.Extent!, limit);
        layer.Extent!.Xmin.Should().BeApproximately(-13692297.3676, 0.01);
        layer.Extent.Ymin.Should().BeApproximately(4439106.7873, 0.01);
        layer.Extent.Xmax.Should().BeApproximately(-13580977.8768, 0.01);
        layer.Extent.Ymax.Should().BeApproximately(4579425.8129, 0.01);
    }

    private static void AssertInsideSquare(EsriExtent extent, double limit)
    {
        double.IsFinite(extent.Xmin).Should().BeTrue();
        double.IsFinite(extent.Ymin).Should().BeTrue();
        double.IsFinite(extent.Xmax).Should().BeTrue();
        double.IsFinite(extent.Ymax).Should().BeTrue();
        extent.Xmin.Should().BeGreaterThanOrEqualTo(-limit);
        extent.Ymin.Should().BeGreaterThanOrEqualTo(-limit);
        extent.Xmax.Should().BeLessThanOrEqualTo(limit);
        extent.Ymax.Should().BeLessThanOrEqualTo(limit);
        extent.Xmin.Should().BeLessThanOrEqualTo(extent.Xmax);
        extent.Ymin.Should().BeLessThanOrEqualTo(extent.Ymax);
    }

    private static (MetadataV2Service Service, MetadataV2Publication Publication, MetadataV2Resource Resource, MetadataV2GraphSnapshot Snapshot)
        Layer(double west, double south, double east, double north)
    {
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "svc-test", Name = "test" },
            Protocols = [ServiceProtocols.MapServer],
            SpatialReference = MetadataV2SpatialReference.WebMercator,
        };
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-points", Name = "points" },
            Type = MetadataV2ResourceType.FeatureDataset,
            Spatial = new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.Wgs84,
                GeometryType = MetadataV2GeometryType.Point,
                Bbox = new MetadataV2Bbox { West = west, South = south, East = east, North = north },
            },
        };
        var publication = new MetadataV2Publication
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "pub-points", Name = "points" },
            ServiceId = service.Metadata.Id,
            ResourceId = resource.Metadata.Id,
            LayerIndex = 0,
            PublicationType = MetadataV2PublicationType.EsriFeatureLayer,
        };
        var snapshot = new MetadataV2GraphSnapshot(
            new MetadataV2Graph
            {
                Services = [service],
                Resources = [resource],
                Publications = [publication],
            },
            "\"test\"",
            DateTimeOffset.UnixEpoch);
        return (service, publication, resource, snapshot);
    }
}
