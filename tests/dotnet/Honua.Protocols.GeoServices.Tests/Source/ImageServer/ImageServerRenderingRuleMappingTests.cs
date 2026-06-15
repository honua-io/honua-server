// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.TestKit.Attributes;

namespace Honua.Protocols.GeoServices.Tests.Source.ImageServer;

public sealed class ImageServerRenderingRuleMappingTests
{
    private static RasterFunctionDocument Parse(string json)
        => JsonSerializer.Deserialize(json, ImageServerJsonContext.Default.RasterFunctionDocument)!;

    [UnitTest]
    public void MapRenderingRule_MinMaxStretch_MapsToMinMax()
    {
        var document = Parse(
            """{"rasterFunction":"Stretch","rasterFunctionArguments":{"StretchType":5}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeTrue();
        mapping.Stretch.Should().NotBeNull();
        mapping.Stretch!.Value.StretchType.Should().Be(RasterStretchType.MinMax);
    }

    [UnitTest]
    public void MapRenderingRule_StandardDeviation_CapturesSigmaCount()
    {
        var document = Parse(
            """{"rasterFunction":"Stretch","rasterFunctionArguments":{"StretchType":3,"NumberOfStandardDeviations":3}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeTrue();
        mapping.Stretch!.Value.StretchType.Should().Be(RasterStretchType.StandardDeviation);
        mapping.Stretch!.Value.NumberOfStandardDeviations.Should().Be(3);
    }

    [UnitTest]
    public void MapRenderingRule_PercentClip_CapturesPercentages()
    {
        var document = Parse(
            """{"rasterFunction":"Stretch","rasterFunctionArguments":{"StretchType":6,"MinPercent":0.5,"MaxPercent":1.5}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeTrue();
        mapping.Stretch!.Value.StretchType.Should().Be(RasterStretchType.PercentClip);
        mapping.Stretch!.Value.MinPercent.Should().Be(0.5);
        mapping.Stretch!.Value.MaxPercent.Should().Be(1.5);
    }

    [UnitTest]
    public void MapRenderingRule_StretchWithStatistics_CapturesPerBandBounds()
    {
        var document = Parse(
            """{"rasterFunction":"Stretch","rasterFunctionArguments":{"StretchType":5,"Statistics":[[12,240,128,40],[3,199,90,30]]}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeTrue();
        mapping.Stretch!.Value.StatisticsMin.Should().Equal(12, 3);
        mapping.Stretch!.Value.StatisticsMax.Should().Equal(240, 199);
    }

    [UnitTest]
    public void MapRenderingRule_NestedIdentityStretch_ResolvesInnerStretch()
    {
        var document = Parse(
            """{"rasterFunction":"Identity","rasterFunctionArguments":{"Raster":{"rasterFunction":"Stretch","rasterFunctionArguments":{"StretchType":5}}}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeTrue();
        mapping.Stretch!.Value.StretchType.Should().Be(RasterStretchType.MinMax);
    }

    [UnitTest]
    public void MapRenderingRule_IdentityOnly_IsExecutableNoOp()
    {
        var document = Parse("""{"rasterFunction":"Identity"}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeTrue();
        mapping.Stretch.Should().BeNull();
    }

    [UnitTest]
    public void MapRenderingRule_StretchTypeNone_IsExecutableNoOp()
    {
        var document = Parse(
            """{"rasterFunction":"Stretch","rasterFunctionArguments":{"StretchType":0}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeTrue();
        mapping.Stretch.Should().BeNull();
    }

    [UnitTest]
    public void MapRenderingRule_HistogramEqualizeStretch_IsNotImplemented()
    {
        var document = Parse(
            """{"rasterFunction":"Stretch","rasterFunctionArguments":{"StretchType":4}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeFalse();
        mapping.IsNotImplemented.Should().BeTrue();
    }

    [UnitTest]
    public void MapRenderingRule_ClipWithEmptyRings_IsInvalid()
    {
        var document = Parse(
            """{"rasterFunction":"Clip","rasterFunctionArguments":{"ClippingGeometry":{"rings":[]}}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeFalse();
        mapping.IsNotImplemented.Should().BeFalse();
        mapping.Reason.Should().Contain("Clip geometry");
    }

    [UnitTest]
    public void MapRenderingRule_UnknownFunction_IsInvalid()
    {
        var document = Parse("""{"rasterFunction":"Hillshade"}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeFalse();
        mapping.IsNotImplemented.Should().BeFalse();
    }

    [UnitTest]
    public void MapRenderingRule_StretchWithoutStretchType_IsInvalid()
    {
        var document = Parse("""{"rasterFunction":"Stretch","rasterFunctionArguments":{}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeFalse();
        mapping.IsNotImplemented.Should().BeFalse();
    }

    [UnitTest]
    public void MapRenderingRule_Colormap_MapsExplicitStops()
    {
        var document = Parse(
            """{"rasterFunction":"Colormap","rasterFunctionArguments":{"Colormap":[[0,0,0,0],[255,255,255,255]]}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeTrue();
        mapping.Colormap.Should().NotBeNull();
        mapping.Colormap!.Entries.Should().HaveCount(2);
        mapping.Colormap.Entries[0].Should().Be(new RasterColormapEntry(0, 0, 0, 0, 255));
        mapping.Colormap.Entries[1].Should().Be(new RasterColormapEntry(255, 255, 255, 255, 255));
    }

    [UnitTest]
    public void MapRenderingRule_ColormapWrappingStretch_ResolvesBoth()
    {
        var document = Parse(
            """{"rasterFunction":"Colormap","rasterFunctionArguments":{"Colormap":[[0,0,0,0],[1,255,0,0]],"Raster":{"rasterFunction":"Stretch","rasterFunctionArguments":{"StretchType":5}}}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeTrue();
        mapping.Stretch!.Value.StretchType.Should().Be(RasterStretchType.MinMax);
        mapping.Colormap!.Entries.Should().HaveCount(2);
    }

    [UnitTest]
    public void MapRenderingRule_ColormapByKnownName_ResolvesNamedRamp()
    {
        var document = Parse(
            """{"rasterFunction":"Colormap","rasterFunctionArguments":{"ColorrampName":"Elevation"}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeTrue();
        mapping.Colormap.Should().NotBeNull();
        mapping.Colormap!.Entries.Should().HaveCountGreaterThan(1);
        // Anchors span the 0..255 display range, low to high.
        mapping.Colormap.Entries[0].Value.Should().Be(0);
        mapping.Colormap.Entries[^1].Value.Should().Be(255);
    }

    [UnitTest]
    public void MapRenderingRule_ColormapByKnownName_IsCaseInsensitive()
    {
        var document = Parse(
            """{"rasterFunction":"Colormap","rasterFunctionArguments":{"ColorrampName":"red to green"}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeTrue();
        mapping.Colormap.Should().NotBeNull();
    }

    [UnitTest]
    public void MapRenderingRule_ColormapByUnknownName_IsNotImplemented()
    {
        var document = Parse(
            """{"rasterFunction":"Colormap","rasterFunctionArguments":{"ColorrampName":"Totally Made Up Ramp"}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeFalse();
        mapping.IsNotImplemented.Should().BeTrue();
        mapping.Reason.Should().Contain("ColorrampName");
    }

    [UnitTest]
    public void MapRenderingRule_ColormapByInlineColorrampObject_IsNotImplemented()
    {
        var document = Parse(
            """{"rasterFunction":"Colormap","rasterFunctionArguments":{"Colorramp":{"type":"algorithmic","fromColor":[0,0,0],"toColor":[255,255,255]}}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeFalse();
        mapping.IsNotImplemented.Should().BeTrue();
    }

    [UnitTest]
    public void MapRenderingRule_ClipPolygon_ResolvesClipRegionWithSrid()
    {
        var document = Parse(
            """{"rasterFunction":"Clip","rasterFunctionArguments":{"ClippingGeometry":{"rings":[[[0,0],[0,10],[10,10],[10,0],[0,0]]],"spatialReference":{"wkid":3857}}}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeTrue();
        mapping.ClipRegion.Should().NotBeNull();
        mapping.ClipRegion!.Value.Srid.Should().Be(3857);
        mapping.ClipRegion.Value.Geometry.Should().NotBeEmpty();
    }

    [UnitTest]
    public void MapRenderingRule_ClipExtent_ResolvesClipRegion()
    {
        var document = Parse(
            """{"rasterFunction":"Clip","rasterFunctionArguments":{"Extent":{"xmin":0,"ymin":0,"xmax":5,"ymax":5,"spatialReference":{"wkid":4326}}}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeTrue();
        mapping.ClipRegion.Should().NotBeNull();
    }

    [UnitTest]
    public void MapRenderingRule_ClipInsideType_ResolvesInvertedClip()
    {
        var document = Parse(
            """{"rasterFunction":"Clip","rasterFunctionArguments":{"ClippingType":1,"Extent":{"xmin":0,"ymin":0,"xmax":5,"ymax":5}}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeTrue();
        mapping.ClipRegion.Should().NotBeNull();
        mapping.ClipRegion!.Value.Inverted.Should().BeTrue();
    }

    [UnitTest]
    public void MapRenderingRule_ClipKeepInsideType_ResolvesNonInvertedClip()
    {
        var document = Parse(
            """{"rasterFunction":"Clip","rasterFunctionArguments":{"ClippingType":0,"Extent":{"xmin":0,"ymin":0,"xmax":5,"ymax":5}}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeTrue();
        mapping.ClipRegion!.Value.Inverted.Should().BeFalse();
    }

    [UnitTest]
    public void MapRenderingRule_ClipUnknownType_IsNotImplemented()
    {
        var document = Parse(
            """{"rasterFunction":"Clip","rasterFunctionArguments":{"ClippingType":9,"Extent":{"xmin":0,"ymin":0,"xmax":5,"ymax":5}}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeFalse();
        mapping.IsNotImplemented.Should().BeTrue();
    }

    [UnitTest]
    public void MapRenderingRule_ClipWithoutGeometry_IsInvalid()
    {
        var document = Parse("""{"rasterFunction":"Clip","rasterFunctionArguments":{}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeFalse();
        mapping.IsNotImplemented.Should().BeFalse();
    }

    [UnitTest]
    public void MapRenderingRule_ExtractBand_ShiftsToOneBasedBands()
    {
        var document = Parse(
            """{"rasterFunction":"ExtractBand","rasterFunctionArguments":{"BandIds":[2,1,0]}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeTrue();
        // 0-based [2,1,0] -> 1-based [3,2,1], order preserved.
        mapping.Bands.Should().Equal(3, 2, 1);
    }

    [UnitTest]
    public void MapRenderingRule_ExtractBandWrappingStretch_ResolvesBoth()
    {
        var document = Parse(
            """{"rasterFunction":"ExtractBand","rasterFunctionArguments":{"BandIds":[0,1,2],"Raster":{"rasterFunction":"Stretch","rasterFunctionArguments":{"StretchType":5}}}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeTrue();
        mapping.Bands.Should().Equal(1, 2, 3);
        mapping.Stretch!.Value.StretchType.Should().Be(RasterStretchType.MinMax);
    }

    [UnitTest]
    public void MapRenderingRule_ExtractBandByName_IsNotImplemented()
    {
        var document = Parse(
            """{"rasterFunction":"ExtractBand","rasterFunctionArguments":{"BandNames":["Red","Green"]}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeFalse();
        mapping.IsNotImplemented.Should().BeTrue();
    }

    [UnitTest]
    public void MapRenderingRule_ExtractBandWithEmptyIds_IsInvalid()
    {
        var document = Parse(
            """{"rasterFunction":"ExtractBand","rasterFunctionArguments":{"BandIds":[]}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeFalse();
        mapping.IsNotImplemented.Should().BeFalse();
    }

    [UnitTest]
    public void MapRenderingRule_ExtractBandWithNegativeId_IsInvalid()
    {
        var document = Parse(
            """{"rasterFunction":"ExtractBand","rasterFunctionArguments":{"BandIds":[-1,0]}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeFalse();
        mapping.IsNotImplemented.Should().BeFalse();
    }

    [UnitTest]
    public void MapRenderingRule_ExtractBandWithoutIds_IsInvalid()
    {
        var document = Parse("""{"rasterFunction":"ExtractBand","rasterFunctionArguments":{}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeFalse();
        mapping.IsNotImplemented.Should().BeFalse();
    }
}
