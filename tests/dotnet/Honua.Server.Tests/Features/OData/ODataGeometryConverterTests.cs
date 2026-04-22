// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.OData.Models;
using Honua.Server.Features.OData.Services;
using Microsoft.Extensions.Options;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Features.OData;

public sealed class ODataGeometryConverterTests
{
    private readonly Honua.Server.Features.Infrastructure.Services.GeometryService _geometryService = new(Options.Create(new LimitsOptions()));

    [Fact]
    public void ConvertGeometryToWkb_WithNorthEastAxisOrder_PreservesCoordinateOrder()
    {
        var geometry = new ODataSpatialGeometry
        {
            Type = "Point",
            CoordinatesJson = "[10, 20]",
            Crs = new ODataSpatialCrs
            {
                Type = "name",
                Properties = new ODataSpatialCrsProperties
                {
                    Name = "EPSG:4269"
                }
            }
        };

        var crsDefinition = new CrsDefinition(
            "http://www.opengis.net/def/crs/EPSG/0/4269",
            4269,
            AxisOrder.NorthEast,
            true);

        var result = ODataGeometryConverter.ConvertGeometryToWkb(
            _geometryService,
            geometry,
            defaultSrid: 4269,
            crsDefinition);

        result.IsSuccess.Should().BeTrue();
        result.Wkb.Should().NotBeNull();

        var parsed = new WKBReader().Read(result.Wkb!);
        parsed.Coordinate.X.Should().Be(10);
        parsed.Coordinate.Y.Should().Be(20);
    }
}
