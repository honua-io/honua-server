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
    public void MapRenderingRule_ClipFunction_IsNotImplemented()
    {
        var document = Parse(
            """{"rasterFunction":"Clip","rasterFunctionArguments":{"ClippingGeometry":{"rings":[]}}}""");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);

        mapping.Supported.Should().BeFalse();
        mapping.IsNotImplemented.Should().BeTrue();
        mapping.Reason.Should().Contain("Clip");
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
}
