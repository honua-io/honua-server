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
        var document = Parse("""{"rasterFunction":"TotallyMadeUpFunction"}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeFalse();
        mapping.IsNotImplemented.Should().BeFalse();
    }

    [UnitTest]
    public void MapRenderingRule_HillshadeDefaults_ResolvesTerrainFunction()
    {
        var document = Parse("""{"rasterFunction":"Hillshade"}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeTrue();
        mapping.Terrain.Should().NotBeNull();
        var terrain = mapping.Terrain!.Value;
        terrain.Method.Should().Be(RasterTerrainMethod.Hillshade);
        // Esri defaults: azimuth 315, altitude 45, z-factor 1, band 1.
        terrain.AzimuthDegrees.Should().Be(315.0);
        terrain.AltitudeDegrees.Should().Be(45.0);
        terrain.Band.Should().Be(1);
    }

    [UnitTest]
    public void MapRenderingRule_HillshadeWithArguments_CapturesAzimuthAltitudeBand()
    {
        var document = Parse(
            """{"rasterFunction":"Hillshade","rasterFunctionArguments":{"Azimuth":270,"Altitude":30,"ZFactor":2,"BandId":2}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeTrue();
        var terrain = mapping.Terrain!.Value;
        terrain.AzimuthDegrees.Should().Be(270);
        terrain.AltitudeDegrees.Should().Be(30);
        terrain.ZFactor.Should().Be(2);
        // 0-based BandId 2 -> 1-based band 3.
        terrain.Band.Should().Be(3);
    }

    [UnitTest]
    public void MapRenderingRule_Slope_ResolvesSlopeTerrain()
    {
        var document = Parse("""{"rasterFunction":"Slope"}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeTrue();
        mapping.Terrain!.Value.Method.Should().Be(RasterTerrainMethod.Slope);
    }

    [UnitTest]
    public void MapRenderingRule_Aspect_ResolvesAspectTerrain()
    {
        var document = Parse("""{"rasterFunction":"Aspect"}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeTrue();
        mapping.Terrain!.Value.Method.Should().Be(RasterTerrainMethod.Aspect);
    }

    [UnitTest]
    public void MapRenderingRule_HillshadeAzimuthOutOfRange_IsInvalid()
    {
        var document = Parse(
            """{"rasterFunction":"Hillshade","rasterFunctionArguments":{"Azimuth":400}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeFalse();
        mapping.IsNotImplemented.Should().BeFalse();
        mapping.Reason.Should().Contain("Azimuth");
    }

    [UnitTest]
    public void MapRenderingRule_BandArithmeticAndTerrainCombined_IsInvalid()
    {
        var document = Parse(
            """{"rasterFunction":"Hillshade","rasterFunctionArguments":{"Raster":{"rasterFunction":"BandArithmetic","rasterFunctionArguments":{"Method":3,"BandIndexes":[0,1]}}}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeFalse();
        mapping.IsNotImplemented.Should().BeFalse();
        mapping.Reason.Should().Contain("cannot combine");
    }

    [UnitTest]
    public void MapRenderingRule_BandArithmeticNdwi_MapsGreenNirOrdering()
    {
        var document = Parse(
            """{"rasterFunction":"BandArithmetic","rasterFunctionArguments":{"Method":9,"BandIndexes":[1,3]}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeTrue();
        var bandArithmetic = mapping.BandArithmetic!.Value;
        bandArithmetic.Method.Should().Be(RasterBandArithmeticMethod.Ndwi);
        // NDWI: [green, NIR] -> green (index 1, 1-based 2) is InfraredBand (rast1),
        // NIR (index 3, 1-based 4) is VisibleBand (rast2).
        bandArithmetic.InfraredBand.Should().Be(2);
        bandArithmetic.VisibleBand.Should().Be(4);
    }

    [UnitTest]
    public void MapRenderingRule_BandArithmeticSavi_MapsVisibleInfraredOrdering()
    {
        var document = Parse(
            """{"rasterFunction":"BandArithmetic","rasterFunctionArguments":{"Method":5,"BandIndexes":[2,3]}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeTrue();
        var bandArithmetic = mapping.BandArithmetic!.Value;
        bandArithmetic.Method.Should().Be(RasterBandArithmeticMethod.Savi);
        // SAVI: [visible, infrared] -> visible (index 2, 1-based 3) is VisibleBand (rast2),
        // infrared (index 3, 1-based 4) is InfraredBand (rast1).
        bandArithmetic.VisibleBand.Should().Be(3);
        bandArithmetic.InfraredBand.Should().Be(4);
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
    public void MapRenderingRule_ColormapByInlineAlgorithmicColorramp_ResolvesGradient()
    {
        var document = Parse(
            """{"rasterFunction":"Colormap","rasterFunctionArguments":{"Colorramp":{"type":"algorithmic","fromColor":[0,0,0],"toColor":[255,255,255],"algorithm":"esriHSVAlgorithm"}}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeTrue();
        mapping.Colormap.Should().NotBeNull();
        mapping.Colormap!.Entries.Should().HaveCountGreaterThan(1);
        // Anchors span the same 0..255 display range the named-ramp path uses, low to high.
        mapping.Colormap.Entries[0].Value.Should().Be(0);
        mapping.Colormap.Entries[^1].Value.Should().Be(255);
        mapping.Colormap.Entries.Should().BeInAscendingOrder(static e => e.Value);
        // The gradient reflects the fromColor -> toColor endpoints (black -> white).
        var first = mapping.Colormap.Entries[0];
        (first.Red, first.Green, first.Blue).Should().Be(((byte)0, (byte)0, (byte)0));
        var last = mapping.Colormap.Entries[^1];
        (last.Red, last.Green, last.Blue).Should().Be(((byte)255, (byte)255, (byte)255));
    }

    [UnitTest]
    public void MapRenderingRule_ColormapByInlineMultipartColorramp_ResolvesConcatenatedGradient()
    {
        var document = Parse(
            """{"rasterFunction":"Colormap","rasterFunctionArguments":{"Colorramp":{"type":"multipart","colorRamps":[{"type":"algorithmic","fromColor":[0,0,0],"toColor":[255,0,0],"algorithm":"esriCIELabAlgorithm"},{"type":"algorithmic","fromColor":[255,0,0],"toColor":[255,255,0],"algorithm":"esriCIELabAlgorithm"}]}}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeTrue();
        mapping.Colormap.Should().NotBeNull();
        mapping.Colormap!.Entries.Should().HaveCountGreaterThan(2);
        mapping.Colormap.Entries[0].Value.Should().Be(0);
        mapping.Colormap.Entries[^1].Value.Should().Be(255);
        mapping.Colormap.Entries.Should().BeInAscendingOrder(static e => e.Value);
    }

    [UnitTest]
    public void MapRenderingRule_ColormapByInlineColorrampWrappingStretch_ResolvesBoth()
    {
        var document = Parse(
            """{"rasterFunction":"Colormap","rasterFunctionArguments":{"Colorramp":{"type":"algorithmic","fromColor":[0,0,0],"toColor":[255,255,255]},"Raster":{"rasterFunction":"Stretch","rasterFunctionArguments":{"StretchType":5}}}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeTrue();
        mapping.Stretch!.Value.StretchType.Should().Be(RasterStretchType.MinMax);
        mapping.Colormap!.Entries.Should().HaveCountGreaterThan(1);
    }

    [UnitTest]
    public void MapRenderingRule_ColormapByRandomColorramp_IsNotImplemented()
    {
        var document = Parse(
            """{"rasterFunction":"Colormap","rasterFunctionArguments":{"Colorramp":{"type":"random","fromColor":[0,0,0],"toColor":[255,255,255]}}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeFalse();
        mapping.IsNotImplemented.Should().BeTrue();
        mapping.Reason.Should().Contain("random");
    }

    [UnitTest]
    public void MapRenderingRule_ColormapByUnknownAlgorithm_IsNotImplemented()
    {
        var document = Parse(
            """{"rasterFunction":"Colormap","rasterFunctionArguments":{"Colorramp":{"type":"algorithmic","fromColor":[0,0,0],"toColor":[255,255,255],"algorithm":"esriMadeUpAlgorithm"}}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeFalse();
        mapping.IsNotImplemented.Should().BeTrue();
        mapping.Reason.Should().Contain("algorithm");
    }

    [UnitTest]
    public void MapRenderingRule_ColormapByInlineColorrampMissingColors_IsInvalid()
    {
        var document = Parse(
            """{"rasterFunction":"Colormap","rasterFunctionArguments":{"Colorramp":{"type":"algorithmic","fromColor":[0,0,0]}}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeFalse();
        mapping.IsNotImplemented.Should().BeFalse();
        mapping.Reason.Should().Contain("toColor");
    }

    [UnitTest]
    public void MapRenderingRule_ColormapByUnknownColorrampType_IsInvalid()
    {
        var document = Parse(
            """{"rasterFunction":"Colormap","rasterFunctionArguments":{"Colorramp":{"type":"totallyMadeUp","fromColor":[0,0,0],"toColor":[255,255,255]}}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeFalse();
        mapping.IsNotImplemented.Should().BeFalse();
        mapping.Reason.Should().Contain("type");
    }

    [UnitTest]
    public void MapRenderingRule_ClipPolygon_ResolvesClipRegionWithSrid()
    {
        var document = Parse(
            """{"rasterFunction":"Clip","rasterFunctionArguments":{"ClippingGeometry":{"rings":[[[0,0],[0,10],[10,10],[10,0],[0,0]]],"spatialReference":{"wkid":3857}}}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeTrue();
        mapping.ClipRegion.Should().NotBeNull();
        var clipRegion = mapping.ClipRegion!.Value;
        clipRegion.Srid.Should().Be(3857);
        clipRegion.Geometry.Should().NotBeEmpty();
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

    [UnitTest]
    public void MapRenderingRule_BandArithmeticNdvi_MapsToOneBasedBands()
    {
        var document = Parse(
            """{"rasterFunction":"BandArithmetic","rasterFunctionArguments":{"Method":3,"BandIndexes":[2,3]}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeTrue();
        mapping.BandArithmetic.Should().NotBeNull();
        var bandArithmetic = mapping.BandArithmetic!.Value;
        // 0-based [2,3] -> 1-based visible=3, infrared=4.
        bandArithmetic.VisibleBand.Should().Be(3);
        bandArithmetic.InfraredBand.Should().Be(4);
        bandArithmetic.Method.Should().Be(RasterBandArithmeticMethod.Ndvi);
    }

    [UnitTest]
    public void MapRenderingRule_BandArithmeticDefaultsToNdviWhenMethodOmitted()
    {
        var document = Parse(
            """{"rasterFunction":"BandArithmetic","rasterFunctionArguments":{"BandIndexes":[0,1]}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeTrue();
        var bandArithmetic = mapping.BandArithmetic!.Value;
        bandArithmetic.Method.Should().Be(RasterBandArithmeticMethod.Ndvi);
        bandArithmetic.VisibleBand.Should().Be(1);
        bandArithmetic.InfraredBand.Should().Be(2);
    }

    [UnitTest]
    public void MapRenderingRule_BandArithmeticAcceptsBandIDsAlias()
    {
        var document = Parse(
            """{"rasterFunction":"BandArithmetic","rasterFunctionArguments":{"Method":3,"BandIDs":[0,1]}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeTrue();
        mapping.BandArithmetic.Should().NotBeNull();
    }

    [UnitTest]
    public void MapRenderingRule_BandArithmeticUnsupportedMethod_IsNotImplemented()
    {
        // Method 1 (e.g. simple band ratio / NDVI variants other than NDVI) is recognized
        // but not implemented in the first cut.
        var document = Parse(
            """{"rasterFunction":"BandArithmetic","rasterFunctionArguments":{"Method":1,"BandIndexes":[0,1]}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeFalse();
        mapping.IsNotImplemented.Should().BeTrue();
    }

    [UnitTest]
    public void MapRenderingRule_BandArithmeticWithoutBandIndexes_IsInvalid()
    {
        var document = Parse(
            """{"rasterFunction":"BandArithmetic","rasterFunctionArguments":{"Method":3}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeFalse();
        mapping.IsNotImplemented.Should().BeFalse();
    }

    [UnitTest]
    public void MapRenderingRule_BandArithmeticWithWrongBandCount_IsInvalid()
    {
        var document = Parse(
            """{"rasterFunction":"BandArithmetic","rasterFunctionArguments":{"Method":3,"BandIndexes":[0,1,2]}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeFalse();
        mapping.IsNotImplemented.Should().BeFalse();
        mapping.Reason.Should().Contain("exactly two");
    }

    [UnitTest]
    public void MapRenderingRule_BandArithmeticWithNegativeIndex_IsInvalid()
    {
        var document = Parse(
            """{"rasterFunction":"BandArithmetic","rasterFunctionArguments":{"Method":3,"BandIndexes":[-1,1]}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeFalse();
        mapping.IsNotImplemented.Should().BeFalse();
    }

    [UnitTest]
    public void MapRenderingRule_StretchWrappingBandArithmetic_ResolvesBoth()
    {
        var document = Parse(
            """{"rasterFunction":"Stretch","rasterFunctionArguments":{"StretchType":5,"Statistics":[[-1,1]],"Raster":{"rasterFunction":"BandArithmetic","rasterFunctionArguments":{"Method":3,"BandIndexes":[0,1]}}}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeTrue();
        mapping.Stretch!.Value.StretchType.Should().Be(RasterStretchType.MinMax);
        mapping.BandArithmetic.Should().NotBeNull();
        var bandArithmetic = mapping.BandArithmetic!.Value;
        bandArithmetic.VisibleBand.Should().Be(1);
        bandArithmetic.InfraredBand.Should().Be(2);
    }
}
