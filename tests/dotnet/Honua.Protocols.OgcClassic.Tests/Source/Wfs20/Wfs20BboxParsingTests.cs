// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Protocols.Ogc.Classic.Wfs20.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

[Protocol(TestProtocols.Wfs20)]
public sealed class Wfs20BboxParsingTests
{
    [UnitTest]
    public void ParseBboxFilter_WithExplicitCrs_ParsesFiveTokenForm()
    {
        var resource = CreateResource();

        var spatialFilter = InvokeParseBboxFilter("0,0,1000,1000,EPSG:3857", resource);

        spatialFilter.Srid.Should().Be(3857);
        spatialFilter.SpatialRelationship.Should().Be(SpatialRelationship.Intersects);
        new WKBReader().Read(spatialFilter.Geometry).Should().BeOfType<Polygon>();
    }

    [UnitTest]
    public void ParseBboxFilter_WithDatelineCrossingGeographicBounds_ReturnsMultipolygon()
    {
        var resource = CreateResource();

        var spatialFilter = InvokeParseBboxFilter("170,-10,-170,10,CRS84", resource);

        spatialFilter.Srid.Should().Be(4326);
        spatialFilter.SpatialRelationship.Should().Be(SpatialRelationship.Intersects);
        new WKBReader().Read(spatialFilter.Geometry).Should().BeOfType<MultiPolygon>();
    }

    private static SpatialFilter InvokeParseBboxFilter(string bbox, MetadataV2Resource resource)
    {
        var method = typeof(Wfs20Handler).GetMethod(
            "ParseBboxFilterForResource",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        var result = method!.Invoke(null, [bbox, resource]);
        result.Should().NotBeNull();

        return (SpatialFilter)result!;
    }

    private static MetadataV2Resource CreateResource()
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res.wfs-layer", Name = "wfs-layer" },
            Type = MetadataV2ResourceType.FeatureDataset,
            Spatial = new MetadataV2ResourceSpatial
            {
                GeometryType = MetadataV2GeometryType.Point,
                SpatialReference = MetadataV2SpatialReference.Wgs84
            }
        };
}
