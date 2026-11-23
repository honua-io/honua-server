// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Protocols.Ogc.Classic.Wfs20.Services;
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

    [UnitTheory]
    [InlineData("urn:ogc:def:crs:EPSG:6.9:25831", 25831)]
    [InlineData("urn:x-ogc:def:crs:EPSG::4326", 4326)]
    [InlineData("urn:ogc:def:crs:OGC:1.3:CRS84", 4326)]
    [InlineData("urn:ogc:def:crs:OGC:2:84", 4326)]
    [InlineData("CRS:84", 4326)]
    public void ParseBboxFilter_WithVersionedUrnsAndCrs84Aliases_ResolvesExpectedSrid(string crs, int expectedSrid)
    {
        var resource = CreateResource();

        var spatialFilter = InvokeParseBboxFilter($"0,0,10,10,{crs}", resource);

        spatialFilter.Srid.Should().Be(expectedSrid);
    }

    [UnitTheory]
    [InlineData("urn:ogc:def:crs:OGC:1.3:CRS84", true)]
    [InlineData("urn:ogc:def:crs:OGC:2:84", true)]
    [InlineData("CRS:84", true)]
    [InlineData("urn:x-ogc:def:crs:EPSG::4326", false)]
    [InlineData("urn:ogc:def:crs:EPSG:6.9:25831", false)]
    public void IsCrs84Request_RecognizesAllCrs84VariantsAndDistinguishesEpsgUrns(string srsName, bool expected)
    {
        Wfs20Handler.IsCrs84Request(srsName).Should().Be(expected);
    }

    private static SpatialFilter InvokeParseBboxFilter(string bbox, MetadataV2Object resource)
    {
        var method = typeof(Wfs20Handler).GetMethod(
            "ParseBboxFilterForResource",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        // Third argument is the optional wfsVersion; null exercises the WFS 2.0 path
        // (CRS-derived axis order), matching these tests' expectations.
        var result = method!.Invoke(null, [bbox, resource, null]);
        result.Should().NotBeNull();

        return (SpatialFilter)result!;
    }

    private static MetadataV2Object CreateResource()
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res.wfs-layer", Name = "wfs-layer" },
            Type = MetadataV2ObjectType.FeatureDataset,
            Spatial = new MetadataV2ObjectSpatial
            {
                GeometryType = MetadataV2GeometryType.Point,
                SpatialReference = MetadataV2SpatialReference.Wgs84
            }
        };
}
