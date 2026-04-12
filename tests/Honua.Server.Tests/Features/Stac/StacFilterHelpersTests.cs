// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Stac.Services;
using Honua.TestKit.Attributes;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Features.Stac;

public sealed class StacFilterHelpersTests
{
    [UnitTest]
    public void ParseBbox_WithDatelineCrossingBBox_ReturnsMultiPolygonFilter()
    {
        var filter = StacFilterHelpers.ParseBbox("170,-10,-170,10");

        filter.Should().NotBeNull();

        var geometry = new WKBReader().Read(filter!.Value.Geometry);
        geometry.Should().BeOfType<MultiPolygon>();

        var multiPolygon = (MultiPolygon)geometry;
        multiPolygon.NumGeometries.Should().Be(2);
    }

    [UnitTest]
    public void ParseBbox_WithThreeDimensionalBBox_ReturnsNull()
    {
        var filter = StacFilterHelpers.ParseBbox("170,-10,-170,10,5,6");

        filter.Should().BeNull();
    }
}
