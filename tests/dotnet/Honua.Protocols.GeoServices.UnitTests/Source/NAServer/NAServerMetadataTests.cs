// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.GeoServices.NAServer;
using Honua.Routing.Features.Routing.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.NAServer;

/// <summary>Checks routing metadata against the supported canonical and REST utility contracts.</summary>
public sealed class NAServerMetadataTests
{
    /// <summary>Esri's published FindRoutes parameter names, which arcpy.nax matches on.</summary>
    private static readonly string[] EsriFindRoutesParameters =
    [
        "Stops", "Measurement_Units", "Travel_Mode",
        "Reorder_Stops_to_Find_Optimal_Routes", "Output_Routes", "Solve_Succeeded"
    ];

    [global::Xunit.Trait("Tier", "Fast")]
    [Fact]
    public void GetTravelModes_ProviderWithoutNamedModes_HasNoModesOrDefault()
    {
        var capabilities = new RoutingProviderCapabilities(SupportsRoute: false, SupportsServiceArea: false);
        var document = NAServerMetadata.BuildGetTravelModesResult(NetworkDataset.Default, capabilities);

        document["results"]![0]!["value"]!["features"]!.AsArray().Should().BeEmpty();
        document["results"]![1]!["value"]!.GetValue<string>().Should().BeEmpty();
    }

    [global::Xunit.Trait("Tier", "Fast")]
    [Fact]
    public void RouteLayer_ProviderWithoutNamedModes_HasNoModesOrDefault()
    {
        var document = NAServerMetadata.BuildLayerResource(
            "Route", new RoutingProviderCapabilities(), NetworkDataset.Default, new RoutingConfiguration());

        document.Should().NotBeNull();
        document!["supportedTravelModes"]!.AsArray().Should().BeEmpty();
        document["defaultTravelMode"]!.GetValue<string>().Should().BeEmpty();
    }

    [global::Xunit.Trait("Tier", "Fast")]
    [Fact]
    public void GetToolInfo_LocationAllocation_UsesItsOwnConfiguredLimits()
    {
        var configuration = new RoutingConfiguration
        {
            MaxFacilities = 31,
            MaxStops = 43,
            MaxLocationAllocationFacilities = 7,
            MaxDemandPoints = 11,
        };
        var document = NAServerMetadata.BuildGetToolInfoResult(
            "asyncLocationAllocation", "SolveLocationAllocation",
            new RoutingProviderCapabilities(SupportsLocationAllocation: true), configuration);
        var limits = document["results"]![0]!["value"]!["serviceLimits"]!;

        limits["maximumFacilities"]!.GetValue<int>().Should().Be(7);
        limits["maximumFacilitiesToFind"]!.GetValue<int>().Should().Be(7);
        limits["maximumDemandPoints"]!.GetValue<int>().Should().Be(11);
    }

    [UnitTest]
    public void FindRoutes_IsResolvedByName_AndAdvertisesEsriParameterNames()
    {
        // arcpy.nax does not call NAServer/Route/solve. It resolves Esri's ready-to-use
        // routing tool on the utility service and refuses the stand-alone binding when it
        // is absent: "Portal .../NAServer/FindRoutes is not configured with the 'Route'
        // web tool" (#5192). The parameter names are matched on, so they are pinned.
        NAServerMetadata.IsUtilityTask(NAServerMetadata.FindRoutesTask).Should().BeTrue();

        var info = NAServerMetadata.BuildUtilityTaskInfo(NAServerMetadata.FindRoutesTask);
        info["name"]!.GetValue<string>().Should().Be("FindRoutes");
        info["executionType"]!.GetValue<string>().Should().Be("esriExecutionTypeSynchronous",
            "the task projects the synchronous NAServer route solve, so there is no job to poll");

        var names = info["parameters"]!.AsArray()
            .Select(p => p!["name"]!.GetValue<string>()).ToArray();
        names.Should().Contain(EsriFindRoutesParameters,
            "arcpy.nax matches Esri's published parameter names when it resolves the tool");

        var stops = info["parameters"]!.AsArray()
            .Single(p => p!["name"]!.GetValue<string>() == "Stops")!;
        stops["dataType"]!.GetValue<string>().Should().Be("GPFeatureRecordSetLayer");
        stops["parameterType"]!.GetValue<string>().Should().Be("esriGPParameterTypeRequired");

        var units = info["parameters"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == "Measurement_Units")!;
        units["choiceList"]!.AsArray().Select(v => v!.GetValue<string>()).Should().Equal("Minutes");
        var reorder = info["parameters"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == "Reorder_Stops_to_Find_Optimal_Routes")!;
        reorder["defaultValue"]!.GetValue<bool>().Should().BeFalse();
    }

    [UnitTest]
    public void GenerateServiceAreas_AdvertisesItsBoundedPointAndPolygonContract()
    {
        NAServerMetadata.IsUtilityTask("generateserviceareas").Should().BeTrue();
        NAServerMetadata.IsRoutingWebTool("GENERATESERVICEAREAS").Should().BeTrue();
        var info = NAServerMetadata.BuildUtilityTaskInfo(NAServerMetadata.GenerateServiceAreasTask);
        info["executionType"]!.GetValue<string>().Should().Be("esriExecutionTypeSynchronous");
        var parameters = info["parameters"]!.AsArray().ToDictionary(parameter => parameter!["name"]!.GetValue<string>());
        parameters.Keys.Should().Equal("Facilities", "Break_Values", "Break_Units", "Travel_Direction", "Travel_Mode", "Service_Areas", "Solve_Succeeded");
        parameters["Facilities"]!["dataType"]!.GetValue<string>().Should().Be("GPFeatureRecordSetLayer");
        parameters["Facilities"]!["parameterType"]!.GetValue<string>().Should().Be("esriGPParameterTypeRequired");
        parameters["Facilities"]!["defaultValue"]!["geometryType"]!.GetValue<string>().Should().Be("esriGeometryPoint");
        parameters["Break_Units"]!["choiceList"]!.AsArray().Select(value => value!.GetValue<string>()).Should().Equal("Minutes");
        parameters["Travel_Direction"]!["choiceList"]!.AsArray().Select(value => value!.GetValue<string>())
            .Should().Equal("Away From Facility", "Towards Facility");
        parameters["Service_Areas"]!["defaultValue"]!["geometryType"]!.GetValue<string>().Should().Be("esriGeometryPolygon");
        parameters["Service_Areas"]!["defaultValue"]!["fields"]!.AsArray()
            .Select(field => field!["name"]!.GetValue<string>()).Should().Equal("ObjectID", "FacilityID", "FacilityOID", "Name", "FromBreak", "ToBreak");
        parameters["Solve_Succeeded"]!["dataType"]!.GetValue<string>().Should().Be("GPBoolean");
    }

    [UnitTest]
    public void RemainingRoutingWebTools_AreNotAdvertisedAsExecutableUtilities()
    {
        foreach (var name in new[] { "FindClosestFacilities", "GenerateOriginDestinationCostMatrix", "SolveLocationAllocation" })
        {
            NAServerMetadata.IsUtilityTask(name).Should().BeFalse();
            NAServerMetadata.IsRoutingWebTool(name).Should().BeFalse();
        }
    }
}
