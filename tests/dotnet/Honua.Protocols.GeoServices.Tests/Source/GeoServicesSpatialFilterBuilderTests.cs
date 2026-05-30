// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.GeoServices;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit.Attributes;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Features.Protocols.GeoServices;

public sealed class GeoServicesSpatialFilterBuilderTests
{
    [UnitTest]
    public void BuildSpatialFilter_WithDatelineCrossingEnvelope_ReturnsMultiPolygon()
    {
        var filter = GeoServicesSpatialFilterBuilder.BuildSpatialFilter(
            new QueryParameters(),
            new GeoServicesGeometry
            {
                Xmin = 170,
                Ymin = -10,
                Xmax = -170,
                Ymax = 10,
                SpatialReference = new GeoServicesSpatialReference { Wkid = 4326 }
            },
            4326);

        filter.Srid.Should().Be(4326);

        var geometry = new WKBReader().Read(filter.Geometry);
        geometry.Should().BeOfType<MultiPolygon>();

        var multiPolygon = (MultiPolygon)geometry;
        multiPolygon.NumGeometries.Should().Be(2);
    }
}
