// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.GeoServices.NAServer;
using Honua.Routing.Features.Routing.Domain;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.NAServer;

public sealed class NAServerMetadataTests
{
    [Fact]
    public void GetTravelModes_ProviderWithoutNamedModes_HasNoModesOrDefault()
    {
        var capabilities = new RoutingProviderCapabilities(SupportsRoute: false, SupportsServiceArea: false);
        var document = NAServerMetadata.BuildGetTravelModesResult(NetworkDataset.Default, capabilities);

        document["results"]![0]!["value"]!["features"]!.AsArray().Should().BeEmpty();
        document["results"]![1]!["value"]!.GetValue<string>().Should().BeEmpty();
    }

    [Fact]
    public void RouteLayer_ProviderWithoutNamedModes_HasNoModesOrDefault()
    {
        var document = NAServerMetadata.BuildLayerResource(
            "Route", new RoutingProviderCapabilities(), NetworkDataset.Default, new RoutingConfiguration());

        document.Should().NotBeNull();
        document!["supportedTravelModes"]!.AsArray().Should().BeEmpty();
        document["defaultTravelMode"]!.GetValue<string>().Should().BeEmpty();
    }

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
}
