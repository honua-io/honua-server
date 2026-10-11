// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.Protocols.GeoServices.GeometryService.Services;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices;

public sealed class ProjectedDatumTransformationTests
{
    private static readonly DatumTransformationSelection Nadcon = new()
    {
        Name = "NAD_1927_To_NAD_1983_NADCON",
        FromSrid = 4267,
        ToSrid = 4269,
        Wkid = 1241,
        ProjPipeline = "+proj=pipeline +step +proj=hgridshift +grids=us_noaa_conus.tif"
    };

    [UnitTest]
    public void SelectProjectDatumTransformation_ExplicitValue_KeepsProjectedEndpoints()
    {
        var selected = GeometryServiceHandler.SelectProjectDatumTransformation(
            "1241", Nadcon, inputSrid: 26717, outputSrid: 26917, geodeticInputSrid: 4267, geodeticOutputSrid: 4269);

        selected.Should().BeSameAs(Nadcon);
    }

    [UnitTest]
    public void SelectProjectDatumTransformation_ImplicitProjected_DropsCatalogDefault()
    {
        var selected = GeometryServiceHandler.SelectProjectDatumTransformation(
            null, Nadcon, inputSrid: 26717, outputSrid: 26917, geodeticInputSrid: 4267, geodeticOutputSrid: 4269);

        selected.Should().BeNull();
    }

    [UnitTest]
    public void SelectProjectDatumTransformation_GeographicNoop_IsKept()
    {
        var noop = Nadcon with { Name = "identity", FromSrid = 4269, ToSrid = 4326, ProjPipeline = "+proj=noop" };

        var selected = GeometryServiceHandler.SelectProjectDatumTransformation(
            null, noop, inputSrid: 4269, outputSrid: 4326, geodeticInputSrid: 4269, geodeticOutputSrid: 4326);

        selected.Should().BeSameAs(noop);
    }

    [UnitTest]
    public void SelectProjectDatumTransformation_ExplicitGeographic_IsKept()
    {
        var selected = GeometryServiceHandler.SelectProjectDatumTransformation(
            "1241", Nadcon, inputSrid: 4267, outputSrid: 4269, geodeticInputSrid: 4267, geodeticOutputSrid: 4269);

        selected.Should().BeSameAs(Nadcon);
    }

    [UnitTest]
    public void SelectDatumResolutionSrids_ExplicitValue_UsesGeodeticBases()
    {
        var (fromSrid, toSrid) = FeatureServerQueryHandler.SelectDatumResolutionSrids(
            layerSrid: 26717,
            outputSrid: 26917,
            geodeticLayerSrid: 4267,
            geodeticOutputSrid: 4269,
            datumTransformationValue: "1241");

        fromSrid.Should().Be(4267);
        toSrid.Should().Be(4269);
    }

    [UnitTest]
    public void SelectDatumResolutionSrids_OmittedValue_KeepsProjectedSrids()
    {
        var (fromSrid, toSrid) = FeatureServerQueryHandler.SelectDatumResolutionSrids(
            layerSrid: 26717,
            outputSrid: 26917,
            geodeticLayerSrid: 4267,
            geodeticOutputSrid: 4269,
            datumTransformationValue: null);

        fromSrid.Should().Be(26717);
        toSrid.Should().Be(26917);
    }

    [UnitTest]
    public void SelectDatumResolutionSrids_WhitespaceValue_KeepsProjectedSrids()
    {
        var (fromSrid, toSrid) = FeatureServerQueryHandler.SelectDatumResolutionSrids(
            layerSrid: 26717,
            outputSrid: 26917,
            geodeticLayerSrid: 4267,
            geodeticOutputSrid: 4269,
            datumTransformationValue: " ");

        fromSrid.Should().Be(26717);
        toSrid.Should().Be(26917);
    }
}
