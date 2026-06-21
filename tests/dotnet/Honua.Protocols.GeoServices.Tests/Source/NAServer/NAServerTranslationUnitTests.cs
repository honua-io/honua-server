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
    [Operation(Operations.Directions)]
    public void MapRoute_UnsolvedResult_EmitsNoSolveMessageAndEmptyRoutes()
    {
        // An empty geometry => Solved=false. The mapper must surface the no-solve
        // path: empty route features plus an informational message (not a route).
        var unsolved = new RouteSolveResult(string.Empty, 0, 0, []);

        var response = NAServerResultMapping.MapRoute(
            unsolved, outSrid: 4326, includeRoutes: true, includeDirections: true);

        response.Routes.Features.Should().BeEmpty();
        response.Messages.Should().NotBeNull();
        response.Messages.Should().ContainSingle();
        response.Messages![0].Description.Should().Contain("No route");
        response.Directions.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Directions)]
    public void BuildRouteSolveRequest_TooManyStops_Throws()
    {
        // Two stops over a cap of 1 must be rejected (DoS guard).
        var caps = new NAServerInputCaps(MaxStops: 1, MaxFacilities: 10, MaxBreaks: 10, MaxBarriers: 10);
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["stops"] = "-157.85,21.30;-157.86,21.31;-157.87,21.32",
        };

        var act = () => NAServerParameterTranslation.BuildRouteSolveRequest(parameters, caps);

        act.Should().Throw<NAServerParameterTranslation.NAServerParameterException>()
            .WithMessage("*exceeds the maximum*");
    }

    [UnitTest]
    [Operation(Operations.ServiceArea)]
    public void BuildServiceAreaSolveRequest_TooManyBreaks_Throws()
    {
        var caps = new NAServerInputCaps(MaxStops: 10, MaxFacilities: 10, MaxBreaks: 2, MaxBarriers: 10);
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["facilities"] = "-157.85,21.30",
            ["defaultBreaks"] = "5,10,15",
        };

        var act = () => NAServerParameterTranslation.BuildServiceAreaSolveRequest(parameters, caps);

        act.Should().Throw<NAServerParameterTranslation.NAServerParameterException>()
            .WithMessage("*exceeds the maximum*");
    }

    [UnitTest]
    [Operation(Operations.ServiceArea)]
    public void BuildServiceAreaSolveRequest_TooManyFacilities_Throws()
    {
        var caps = new NAServerInputCaps(MaxStops: 10, MaxFacilities: 1, MaxBreaks: 10, MaxBarriers: 10);
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["facilities"] = "-157.85,21.30;-157.86,21.31",
            ["defaultBreaks"] = "5",
        };

        var act = () => NAServerParameterTranslation.BuildServiceAreaSolveRequest(parameters, caps);

        act.Should().Throw<NAServerParameterTranslation.NAServerParameterException>()
            .WithMessage("*exceeds the maximum*");
    }

    [UnitTest]
    [Operation(Operations.Directions)]
    public void BuildRouteSolveRequest_InSrFallsBackToOutSr()
    {
        // outSR=3857, no inSR => InSrid mirrors OutSrid (3857).
        var parametersNoInSr = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["stops"] = "0,0;100,100",
            ["outSR"] = "3857",
        };
        var requestNoInSr = NAServerParameterTranslation.BuildRouteSolveRequest(parametersNoInSr);
        requestNoInSr.OutSrid.Should().Be(3857);
        requestNoInSr.InSrid.Should().Be(3857);

        // Explicit inSR overrides the fallback.
        var parametersWithInSr = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["stops"] = "0,0;1,1",
            ["outSR"] = "3857",
            ["inSR"] = "4326",
        };
        var requestWithInSr = NAServerParameterTranslation.BuildRouteSolveRequest(parametersWithInSr);
        requestWithInSr.OutSrid.Should().Be(3857);
        requestWithInSr.InSrid.Should().Be(4326);
    }

    [UnitTest]
    [Operation(Operations.ServiceArea)]
    public void MapServiceArea_GeoJsonCcwOuterRing_IsNormalizedToClockwiseForEsri()
    {
        // GeoJSON outer rings are CCW (RFC 7946). Esri expects CW outer rings, so
        // the mapper must reverse this ring's winding. The unit square below is
        // wound counter-clockwise (positive signed area).
        const string ccwPolygon =
            "{\"type\":\"Polygon\",\"coordinates\":[[[0,0],[1,0],[1,1],[0,1],[0,0]]]}";
        var result = new ServiceAreaSolveResult(
        [
            new ServiceAreaPolygon(FacilityId: 0, FromBreak: 0, ToBreak: 5, ccwPolygon),
        ]);

        var response = NAServerResultMapping.MapServiceArea(result, outSrid: 4326);

        var ring = response.SaPolygons.Features[0].Geometry!.Rings[0];
        ring.Should().HaveCount(5);

        // Signed shoelace area must be negative (clockwise) after normalization.
        var area = 0.0;
        for (var i = 0; i < ring.Length - 1; i++)
        {
            area += (ring[i][0] * ring[i + 1][1]) - (ring[i + 1][0] * ring[i][1]);
        }

        area.Should().BeLessThan(0, "Esri outer rings must be clockwise");
    }

    [UnitTest]
    [Operation(Operations.Directions)]
    public void ParseTravelMode_BareToken_ReturnsToken()
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["travelMode"] = " walking ",
        };

        NAServerParameterTranslation.ParseTravelMode(parameters).Should().Be("walking");
    }

    [UnitTest]
    [Operation(Operations.Directions)]
    public void ParseTravelMode_EsriObject_UsesName()
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["travelMode"] = """{ "name": "Driving Time", "type": "AUTOMOBILE" }""",
        };

        NAServerParameterTranslation.ParseTravelMode(parameters).Should().Be("Driving Time");
    }

    [UnitTest]
    [Operation(Operations.Directions)]
    public void ParseTravelMode_Absent_ReturnsNull()
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        NAServerParameterTranslation.ParseTravelMode(parameters).Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Directions)]
    public void ParseBarriers_PointFeatureSet_ProducesPointGeoJson()
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["barriers"] = """
            { "features": [ { "geometry": { "x": -157.85, "y": 21.30 } } ] }
            """,
        };

        var barriers = NAServerParameterTranslation.ParseBarriers(parameters, NAServerInputCaps.Default);

        barriers.Should().ContainSingle();
        barriers[0].Kind.Should().Be(RouteBarrierKind.Point);
        barriers[0].GeometryGeoJson.Should().Contain("\"type\":\"Point\"");
        barriers[0].GeometryGeoJson.Should().Contain("-157.85");
    }

    [UnitTest]
    [Operation(Operations.Directions)]
    public void ParseBarriers_PolylineAndPolygon_ProduceLineAndPolygonGeoJson()
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["polylineBarriers"] = """
            { "features": [ { "geometry": { "paths": [ [ [0,0], [1,1] ] ] } } ] }
            """,
            ["polygonBarriers"] = """
            { "features": [ { "geometry": { "rings": [ [ [0,0], [1,0], [1,1], [0,0] ] ] } } ] }
            """,
        };

        var barriers = NAServerParameterTranslation.ParseBarriers(parameters, NAServerInputCaps.Default);

        barriers.Should().HaveCount(2);
        var line = barriers.Should().ContainSingle(b => b.Kind == RouteBarrierKind.Line).Subject;
        line.GeometryGeoJson.Should().Contain("\"type\":\"MultiLineString\"");
        var polygon = barriers.Should().ContainSingle(b => b.Kind == RouteBarrierKind.Polygon).Subject;
        polygon.GeometryGeoJson.Should().Contain("\"type\":\"Polygon\"");
    }

    [UnitTest]
    [Operation(Operations.Directions)]
    public void ParseBarriers_OverCap_Throws()
    {
        var caps = new NAServerInputCaps(MaxStops: 10, MaxFacilities: 10, MaxBreaks: 10, MaxBarriers: 1);
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["barriers"] = """
            { "features": [
                { "geometry": { "x": 0, "y": 0 } },
                { "geometry": { "x": 1, "y": 1 } }
            ] }
            """,
        };

        var act = () => NAServerParameterTranslation.ParseBarriers(parameters, caps);

        act.Should().Throw<NAServerParameterTranslation.NAServerParameterException>()
            .WithMessage("*exceeds the maximum*");
    }

    [UnitTest]
    [Operation(Operations.Directions)]
    public void BuildRouteSolveRequest_ThreadsBarriersAndTravelMode()
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["stops"] = "-157.85,21.30;-157.86,21.31",
            ["travelMode"] = "driving",
            ["barriers"] = """{ "features": [ { "geometry": { "x": -157.855, "y": 21.305 } } ] }""",
        };

        var request = NAServerParameterTranslation.BuildRouteSolveRequest(parameters);

        request.TravelMode.Should().Be("driving");
        request.Barriers.Should().ContainSingle();
        request.Barriers[0].Kind.Should().Be(RouteBarrierKind.Point);
    }

    [UnitTest]
    [Operation(Operations.ClosestFacility)]
    public void BuildClosestFacilitySolveRequest_ParsesIncidentsFacilitiesTargetCountDirection()
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["incidents"] = "-157.85,21.30;-157.86,21.31",
            ["facilities"] = "-157.90,21.40",
            ["defaultTargetFacilityCount"] = "3",
            ["travelDirection"] = "esriNATravelDirectionFromFacility",
            ["defaultCutoff"] = "15",
        };

        var request = NAServerParameterTranslation.BuildClosestFacilitySolveRequest(parameters);

        request.Incidents.Should().HaveCount(2);
        request.Facilities.Should().ContainSingle();
        request.DefaultTargetFacilityCount.Should().Be(3);
        request.Direction.Should().Be(ClosestFacilityTravelDirection.FromFacility);
        request.Cutoff.Should().Be(15);
    }

    [UnitTest]
    [Operation(Operations.ClosestFacility)]
    public void BuildClosestFacilitySolveRequest_NoFacilities_Throws()
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["incidents"] = "-157.85,21.30",
        };

        var act = () => NAServerParameterTranslation.BuildClosestFacilitySolveRequest(parameters);

        act.Should().Throw<NAServerParameterTranslation.NAServerParameterException>();
    }

    [UnitTest]
    [Operation(Operations.ClosestFacility)]
    public void MapClosestFacility_RanksRoutesAndUses1BasedIds()
    {
        var result = new ClosestFacilitySolveResult(
        [
            new ClosestFacilityRoute(
                IncidentId: 0,
                FacilityId: 2,
                Rank: 1,
                RouteGeometryGeoJson: "{\"type\":\"LineString\",\"coordinates\":[[-157.85,21.30],[-157.86,21.31]]}",
                TotalLengthMeters: 1500,
                TotalTimeMinutes: 5,
                Directions: [new RouteDirectionStep("go", 1500, 5, "straight")]),
        ]);

        var response = NAServerResultMapping.MapClosestFacility(result, outSrid: 4326, includeDirections: true);

        var feature = response.Routes!.Features.Should().ContainSingle().Subject;
        feature.Attributes.IncidentId.Should().Be(1);
        feature.Attributes.FacilityId.Should().Be(3);
        feature.Attributes.FacilityRank.Should().Be(1);
        feature.Geometry!.Paths.Should().HaveCount(1);
        response.Directions.Should().NotBeEmpty();
    }

    [UnitTest]
    [Operation(Operations.OdCostMatrix)]
    public void BuildOdCostMatrixSolveRequest_ParsesOriginsDestinationsCutoffTargetCount()
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["origins"] = "-157.85,21.30;-157.86,21.31",
            ["destinations"] = "-157.90,21.40;-157.91,21.41",
            ["defaultCutoff"] = "30",
            ["defaultTargetDestinationCount"] = "1",
        };

        var request = NAServerParameterTranslation.BuildOdCostMatrixSolveRequest(parameters);

        request.Origins.Should().HaveCount(2);
        request.Destinations.Should().HaveCount(2);
        request.Cutoff.Should().Be(30);
        request.DestinationCount.Should().Be(1);
    }

    [UnitTest]
    [Operation(Operations.OdCostMatrix)]
    public void BuildOdCostMatrixSolveRequest_UnsupportedOutputType_Throws()
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["origins"] = "-157.85,21.30",
            ["destinations"] = "-157.90,21.40",
            ["outputType"] = "esriNAODOutputTrueShape",
        };

        var act = () => NAServerParameterTranslation.BuildOdCostMatrixSolveRequest(parameters);

        act.Should().Throw<NAServerParameterTranslation.NAServerParameterException>()
            .WithMessage("*outputType*");
    }

    [UnitTest]
    [Operation(Operations.OdCostMatrix)]
    public void MapOdCostMatrix_ProducesRankedLinesWith1BasedIds()
    {
        var result = new OdCostMatrixSolveResult(
        [
            new OdLine(OriginId: 0, DestinationId: 1, DestinationRank: 1, TotalCostMinutes: 4.2, TotalLengthMeters: 0),
            new OdLine(OriginId: 0, DestinationId: 0, DestinationRank: 2, TotalCostMinutes: 9.9, TotalLengthMeters: 0),
        ]);

        var response = NAServerResultMapping.MapOdCostMatrix(result);

        response.OdLines.Features.Should().HaveCount(2);
        response.OdLines.Features[0].Attributes.OriginId.Should().Be(1);
        response.OdLines.Features[0].Attributes.DestinationId.Should().Be(2);
        response.OdLines.Features[0].Attributes.DestinationRank.Should().Be(1);
        response.OdLines.Features[0].Attributes.TotalTime.Should().Be(4.2);
    }

    [UnitTest]
    [Operation(Operations.LocationAllocation)]
    public void ParseLocationAllocationProblemType_KnownAndUnknown()
    {
        NAServerParameterTranslation.ParseLocationAllocationProblemType("esriMFPMinimizeImpedance")
            .Should().Be(LocationAllocationProblemType.MinimizeImpedance);
        NAServerParameterTranslation.ParseLocationAllocationProblemType("esriMFPMaximizeCoverage")
            .Should().Be(LocationAllocationProblemType.MaximizeCoverage);

        var act = () => NAServerParameterTranslation.ParseLocationAllocationProblemType("esriMFPTargetMarketShare");
        act.Should().Throw<NAServerParameterTranslation.NAServerParameterException>();
    }

    [UnitTest]
    [Operation(Operations.LocationAllocation)]
    public void ParseDemandPoints_FeatureSetWithWeights_ReadsWeightDefaultingToOne()
    {
        const string featureSet = """
        { "features": [
            { "geometry": { "x": -157.85, "y": 21.30 }, "attributes": { "Weight": 7 } },
            { "geometry": { "x": -157.86, "y": 21.31 } }
        ] }
        """;

        var demand = NAServerParameterTranslation.ParseDemandPoints(featureSet);

        demand.Should().HaveCount(2);
        demand[0].Weight.Should().Be(7);
        demand[1].Weight.Should().Be(1);
    }

    [UnitTest]
    [Operation(Operations.LocationAllocation)]
    public void MapLocationAllocation_EmitsChosenFacilitiesAndAllocations()
    {
        var result = new LocationAllocationSolveResult(
            ChosenFacilityIds: [1],
            Allocations:
            [
                new DemandAllocation(DemandPointId: 0, AllocatedFacilityId: 1, Weight: 3, ImpedanceMinutes: 4),
                new DemandAllocation(DemandPointId: 1, AllocatedFacilityId: -1, Weight: 2, ImpedanceMinutes: double.PositiveInfinity),
            ],
            TotalWeightedImpedance: 12,
            TotalWeightCovered: 3);

        var response = NAServerResultMapping.MapLocationAllocation(result);

        var facility = response.Facilities.Features.Should().ContainSingle().Subject;
        facility.Attributes.FacilityId.Should().Be(2); // 1-based
        facility.Attributes.DemandWeight.Should().Be(3);

        response.DemandPoints.Features.Should().HaveCount(2);
        response.DemandPoints.Features[0].Attributes.FacilityId.Should().Be(2);
        response.DemandPoints.Features[0].Attributes.AllocatedTime.Should().Be(4);
        response.DemandPoints.Features[1].Attributes.FacilityId.Should().Be(0); // unallocated
        response.DemandPoints.Features[1].Attributes.AllocatedTime.Should().Be(-1);
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
