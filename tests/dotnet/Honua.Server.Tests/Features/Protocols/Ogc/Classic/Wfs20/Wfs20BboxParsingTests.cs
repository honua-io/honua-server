// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Protocols.Ogc.Classic.Wfs20.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using CatalogGeometryType = Honua.Core.Features.Catalog.Domain.GeometryType;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

[Protocol(TestProtocols.Wfs20)]
public sealed class Wfs20BboxParsingTests
{
    [UnitTest]
    public void ParseBboxFilter_WithExplicitCrs_ParsesFiveTokenForm()
    {
        var layer = LayerDefinition.CreateBasic(1, "wfs-layer", CatalogGeometryType.Point, SpatialReference.WGS84);

        var spatialFilter = InvokeParseBboxFilter("0,0,1000,1000,EPSG:3857", layer);

        spatialFilter.Srid.Should().Be(3857);
        spatialFilter.SpatialRelationship.Should().Be(SpatialRelationship.Intersects);
        new WKBReader().Read(spatialFilter.Geometry).Should().BeOfType<Polygon>();
    }

    [UnitTest]
    public void ParseBboxFilter_WithDatelineCrossingGeographicBounds_ReturnsMultipolygon()
    {
        var layer = LayerDefinition.CreateBasic(1, "wfs-layer", CatalogGeometryType.Point, SpatialReference.WGS84);

        var spatialFilter = InvokeParseBboxFilter("170,-10,-170,10,CRS84", layer);

        spatialFilter.Srid.Should().Be(4326);
        spatialFilter.SpatialRelationship.Should().Be(SpatialRelationship.Intersects);
        new WKBReader().Read(spatialFilter.Geometry).Should().BeOfType<MultiPolygon>();
    }

    private static SpatialFilter InvokeParseBboxFilter(string bbox, LayerDefinition layer)
    {
        var method = typeof(Wfs20Handler).GetMethod(
            "ParseBboxFilter",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        var result = method!.Invoke(null, [bbox, layer]);
        result.Should().NotBeNull();

        return (SpatialFilter)result!;
    }
}
