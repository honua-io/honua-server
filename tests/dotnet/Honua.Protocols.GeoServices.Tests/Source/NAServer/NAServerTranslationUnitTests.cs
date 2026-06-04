// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.GeoServices.NAServer;
using Honua.Routing.Features.Routing.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.NAServer;

/// <summary>
/// Focused unit tests for the NAServer parameter translation and result mapping
/// helpers — no HTTP host or database required.
/// </summary>
[Protocol(TestProtocols.NAServer)]
public sealed class NAServerTranslationUnitTests
{
    [UnitTest]
    [Operation(Operations.ServiceArea)]
    public void ParseBreaks_CommaDelimited_ReturnsAscendingDistinctPositiveValues()
    {
        var breaks = NAServerParameterTranslation.ParseBreaks("10,5,5,0,-3,15");

        breaks.Should().Equal(5d, 10d, 15d);
    }

    [UnitTest]
    [Operation(Operations.ServiceArea)]
    public void ParseTravelDirection_EsriTokensAndNumbers_MapToEnum()
    {
        NAServerParameterTranslation.ParseTravelDirection("esriNATravelDirectionToFacility")
            .Should().Be(ServiceAreaTravelDirection.ToFacility);
        NAServerParameterTranslation.ParseTravelDirection("1")
            .Should().Be(ServiceAreaTravelDirection.ToFacility);
        NAServerParameterTranslation.ParseTravelDirection("esriNATravelDirectionFromFacility")
            .Should().Be(ServiceAreaTravelDirection.FromFacility);
        NAServerParameterTranslation.ParseTravelDirection(null)
            .Should().Be(ServiceAreaTravelDirection.FromFacility);
    }

    [UnitTest]
    [Operation(Operations.Directions)]
    public void ParsePoints_DelimitedList_ReturnsOrderedRoutePoints()
    {
        var points = NAServerParameterTranslation.ParsePoints("-157.85,21.3; -157.86,21.31", "stops");

        points.Should().HaveCount(2);
        points[0].Lon.Should().BeApproximately(-157.85, 1e-9);
        points[0].Lat.Should().BeApproximately(21.3, 1e-9);
        points[1].Lon.Should().BeApproximately(-157.86, 1e-9);
    }

    [UnitTest]
    [Operation(Operations.Directions)]
    public void ParsePoints_EsriFeatureSet_ReadsGeometryXy()
    {
        const string featureSet = """
        {
          "spatialReference": { "wkid": 4326 },
          "features": [
            { "geometry": { "x": -157.85, "y": 21.30 } },
            { "geometry": { "x": -157.86, "y": 21.31 } }
          ]
        }
        """;

        var points = NAServerParameterTranslation.ParsePoints(featureSet, "stops");

        points.Should().HaveCount(2);
        points[0].Lon.Should().BeApproximately(-157.85, 1e-9);
        points[1].Lat.Should().BeApproximately(21.31, 1e-9);
    }

    [UnitTest]
    [Operation(Operations.Directions)]
    public void BuildRouteSolveRequest_SingleStop_Throws()
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["stops"] = "-157.85,21.30",
        };

        var act = () => NAServerParameterTranslation.BuildRouteSolveRequest(parameters);

        act.Should().Throw<NAServerParameterTranslation.NAServerParameterException>();
    }

    [UnitTest]
    [Operation(Operations.Directions)]
    public void MapRoute_LineStringResult_ProducesEsriPathsAndAttributes()
    {
        var result = new RouteSolveResult(
            "{\"type\":\"LineString\",\"coordinates\":[[-157.85,21.30],[-157.86,21.31]]}",
            TotalLengthMeters: 1234.5,
            TotalTimeMinutes: 6.7,
            Directions: [new RouteDirectionStep("Go", 1234.5, 6.7, "straight")]);

        var response = NAServerResultMapping.MapRoute(result, outSrid: 4326, includeRoutes: true, includeDirections: true);

        var feature = response.Routes.Features.Should().ContainSingle().Subject;
        feature.Geometry!.Paths.Should().HaveCount(1);
        feature.Geometry.Paths[0].Should().HaveCount(2);
        feature.Geometry.Paths[0][0].Should().Equal(-157.85, 21.30);
        feature.Attributes.TotalLength.Should().Be(1234.5);
        feature.Attributes.TotalTravelTime.Should().Be(6.7);
        feature.Geometry.SpatialReference!.Wkid.Should().Be(4326);
        response.Directions.Should().ContainSingle();
    }

    [UnitTest]
    [Operation(Operations.ServiceArea)]
    public void MapServiceArea_PolygonResult_AssignsFromToBreakAndRings()
    {
        const string polygon = "{\"type\":\"Polygon\",\"coordinates\":[[[0,0],[1,0],[1,1],[0,1],[0,0]]]}";
        var result = new ServiceAreaSolveResult(
        [
            new ServiceAreaPolygon(FacilityId: 0, FromBreak: 0, ToBreak: 5, polygon),
            new ServiceAreaPolygon(FacilityId: 0, FromBreak: 5, ToBreak: 10, polygon),
        ]);

        var response = NAServerResultMapping.MapServiceArea(result, outSrid: 4326);

        response.SaPolygons.Features.Should().HaveCount(2);
        response.SaPolygons.Features[0].Attributes.FromBreak.Should().Be(0);
        response.SaPolygons.Features[0].Attributes.ToBreak.Should().Be(5);
        response.SaPolygons.Features[0].Attributes.FacilityId.Should().Be(0);
        response.SaPolygons.Features[0].Geometry!.Rings.Should().HaveCount(1);
        response.SaPolygons.Features[0].Geometry!.Rings[0].Should().HaveCount(5);
        response.SaPolygons.Features[1].Attributes.FromBreak.Should().Be(5);
        response.SaPolygons.Features[1].Attributes.ToBreak.Should().Be(10);
    }
}
