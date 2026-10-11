// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml.Linq;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Protocols.GeoServices.ImageServer.Handlers;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Honua.Protocols.GeoServices.ImageServer;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerKeyPropertiesRasterInfoTests
{
    private const string Xsi = "http://www.w3.org/2001/XMLSchema-instance";
    private static readonly RasterInfo Raster = new()
    {
        Id = 7,
        LayerId = 1,
        Name = "RGB",
        Width = 64,
        Height = 32,
        BandCount = 3,
        PixelType = "8BUI",
        NoDataValue = 0,
        Extent = new RasterExtent { XMin = 1, YMin = 2, XMax = 5, YMax = 4, Srid = 4326 }
    };

    [UnitTest]
    public void RasterInfo_UsesTypedZeroBasedBandsAndCanonicalFacts()
    {
        RasterStatistics[] statistics =
        [
            new() { Band = 3, MinValue = 60, MaxValue = 60, MeanValue = 60, StandardDeviation = 0 },
            new() { Band = 1, MinValue = 200, MaxValue = 200, MeanValue = 200, StandardDeviation = 0 }
        ];
        BandHistogram[] histograms = [new() { SourceBand = 1, Size = 1, Min = 200, Max = 200, Counts = [4096] }];
        var value = ImageServerSoapEndpoints.BuildKeyPropertiesRasterInfo(Raster, Raster.Extent!.Value, statistics, histograms);

        value.Attribute(XName.Get("type", Xsi))!.Value.Should().Be("tns:RasterInfo");
        value.Element("BandCount")!.Value.Should().Be("3");
        value.Element("PixelType")!.Value.Should().Be("U8");
        value.Element("PixelSizeX")!.Value.Should().Be("0.0625");
        value.Element("PixelSizeY")!.Value.Should().Be("0.0625");
        value.Elements().Where(element => element.Name.LocalName.StartsWith("RasterStatistics_", StringComparison.Ordinal))
            .Select(element => element.Name.LocalName).Should().Equal("RasterStatistics_0", "RasterStatistics_2");
        value.Element("RasterStatistics_1").Should().BeNull("missing bands must not acquire fabricated statistics");
        var first = value.Element("RasterStatistics_0")!;
        first.Attribute(XName.Get("type", Xsi))!.Value.Should().Be("tns:RasterStatistics");
        first.Element("Mean")!.Value.Should().Be("200");
        value.Element("NoData")!.Element("AnyType")!.Value.Should().Be("0");
        first.Element("Ignores").Should().BeNull("per-source NoData exclusions are not a service-wide ignore list");
        first.Element("Count").Should().BeNull("the observed SOAP type has no count/sum members");
        first.Element("Sum").Should().BeNull();
        var histogram = value.Element("RasterHistogram_0")!;
        histogram.Attribute(XName.Get("type", Xsi))!.Value.Should().Be("tns:RasterHistogram");
        histogram.Element("Counts")!.Attribute(XName.Get("type", Xsi))!.Value.Should().Be("tns:ArrayOfDouble");
        histogram.Element("Counts")!.Element("Double")!.Value.Should().Be("4096");
        statistics[0].Band.Should().Be(3, "serialization must not reorder caller-owned arrays");
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData(-9999d)]
    public void MissingStatisticsAndHistograms_DoNotInventValues(double? noData)
    {
        var value = ImageServerSoapEndpoints.BuildKeyPropertiesRasterInfo(
            Raster with { NoDataValue = noData }, Raster.Extent!.Value, [], []);
        value.Elements().Where(element => element.Name.LocalName.StartsWith("RasterStatistics_", StringComparison.Ordinal)
            || element.Name.LocalName.StartsWith("RasterHistogram_", StringComparison.Ordinal)).Should().BeEmpty();
        var noDataElement = value.Element("NoData")!;
        if (noData.HasValue)
        {
            noDataElement.Element("AnyType")!.Value.Should().Be("-9999");
        }
        else
        {
            noDataElement.Attribute(XName.Get("nil", Xsi))!.Value.Should().Be("true");
        }
    }

    [UnitTest]
    public void SparseOutOfOrderHistograms_PreserveCanonicalBandIdsWithoutRenumbering()
    {
        RasterHistogram[] canonical = [
            new() { Band = 3, BinCount = 1, Min = 30, Max = 39, Counts = [300] },
            new() { Band = 1, BinCount = 1, Min = 10, Max = 19, Counts = [100] }
        ];
        var value = ImageServerSoapEndpoints.BuildKeyPropertiesRasterInfo(Raster, Raster.Extent!.Value, [],
            canonical.Select(ImageServerHistogramProjection.Project).ToArray());
        value.Elements().Where(element => element.Name.LocalName.StartsWith("RasterHistogram_", StringComparison.Ordinal))
            .Select(element => element.Name.LocalName).Should().Equal("RasterHistogram_0", "RasterHistogram_2");
        value.Element("RasterHistogram_1").Should().BeNull();
        value.Element("RasterHistogram_2")!.Element("Counts")!.Element("Double")!.Value.Should().Be("300");
    }

    [UnitTheory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(4)]
    public void InvalidOrUndeclaredBandIds_AreNotAdvertised(int band)
    {
        var histogram = ImageServerHistogramProjection.Project(
            new RasterHistogram { Band = band, BinCount = 1, Min = 10, Max = 19, Counts = [100] });
        var value = ImageServerSoapEndpoints.BuildKeyPropertiesRasterInfo(
            Raster, Raster.Extent!.Value,
            [new RasterStatistics { Band = band, MinValue = 10, MaxValue = 19 }], [histogram]);
        value.Elements().Where(element => element.Name.LocalName.StartsWith("RasterStatistics_", StringComparison.Ordinal)
            || element.Name.LocalName.StartsWith("RasterHistogram_", StringComparison.Ordinal)).Should().BeEmpty();
    }

    [UnitTest]
    public void NullStatistics_DoNotBecomeZeroValuedStatistics()
    {
        var value = ImageServerSoapEndpoints.BuildKeyPropertiesRasterInfo(
            Raster, Raster.Extent!.Value, [new RasterStatistics { Band = 1 }], []);
        value.Element("RasterStatistics_0").Should().BeNull();
    }

    [UnitTest]
    public void ExtractBand_ReordersPropertiesStatisticsAndHistogramsTogether()
    {
        var operation = ExtractBandOperation("<Int>2</Int><Int>0</Int><Int>2</Int>");
        ImageServerSoapEndpoints.TryReadKeyPropertiesBands(operation, 3, out var bands, out var error, out _)
            .Should().BeTrue(error);
        bands.Should().Equal(3, 1, 3);
        var properties = ImageServerSoapEndpoints.BuildSelectedKeyProperties(Raster, bands);
        properties.BandCount.Should().Be(3);
        properties.BandProperties.Select(band => band.BandName).Should().Equal("Band_3", "Band_1", "Band_3");
        var value = ImageServerSoapEndpoints.BuildKeyPropertiesRasterInfo(Raster, Raster.Extent!.Value,
            [new() { Band = 1, MinValue = 10, MaxValue = 19 }, new() { Band = 3, MinValue = 30, MaxValue = 39 }],
            [new() { SourceBand = 3, Size = 1, Counts = [300] }, new() { SourceBand = 1, Size = 1, Counts = [100] }], bands);
        value.Element("BandCount")!.Value.Should().Be("3");
        value.Element("RasterStatistics_0")!.Element("Min")!.Value.Should().Be("30");
        value.Element("RasterStatistics_1")!.Element("Min")!.Value.Should().Be("10");
        value.Element("RasterStatistics_2")!.Element("Min")!.Value.Should().Be("30");
        value.Element("RasterHistogram_0")!.Element("Counts")!.Element("Double")!.Value.Should().Be("300");
        value.Element("RasterHistogram_1")!.Element("Counts")!.Element("Double")!.Value.Should().Be("100");
    }

    [UnitTheory]
    [InlineData(256, true)]
    [InlineData(257, false)]
    [InlineData(87000, false)]
    public void ExtractBand_OutputCardinalityIsBoundedIncludingDuplicates(int count, bool accepted)
    {
        var operation = ExtractBandOperation(string.Concat(Enumerable.Repeat("<Int>0</Int>", count)));
        ImageServerSoapEndpoints.TryReadKeyPropertiesBands(operation, 3, out var bands, out _, out var status)
            .Should().Be(accepted);
        if (accepted)
        {
            bands.Should().HaveCount(count).And.OnlyContain(band => band == 1);
        }
        else
        {
            bands.Should().BeNull();
            status.Should().Be(StatusCodes.Status400BadRequest);
        }
    }

    [UnitTest]
    public void NestedIdentity_PreservesInnerExtractBandSelectionWithoutFlatteningArguments()
    {
        var inner = ExtractBandOperation("<Int>2</Int><Int>0</Int><Int>2</Int>").Element("RenderingRule")!;
        var operation = new XElement("GetKeyPropertiesX",
            new XElement("RenderingRule", new XElement("FunctionName", "Identity"),
                new XElement("Arguments", new XElement("PropertySetProperty",
                    new XElement("Key", "Raster"), new XElement("Value", inner.Elements())))));
        ImageServerSoapEndpoints.TryReadKeyPropertiesBands(operation, 3, out var bands, out var error, out _)
            .Should().BeTrue(error);
        bands.Should().Equal(3, 1, 3);
    }

    [UnitTest]
    public void NestedExtractBand_ComposesOuterIndexIntoInnerSelection()
    {
        var inner = ExtractBandOperation("<Int>2</Int><Int>0</Int>").Element("RenderingRule")!;
        var operation = XElement.Parse("""
            <GetKeyPropertiesX xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <RenderingRule><FunctionName>ExtractBand</FunctionName><Arguments>
                <PropertySetProperty><Key>BandIds</Key><Value xsi:type="tns:ArrayOfInt"><Int>1</Int></Value></PropertySetProperty>
              </Arguments></RenderingRule>
            </GetKeyPropertiesX>
            """);
        operation.Element("RenderingRule")!.Element("Arguments")!.Add(
            new XElement("PropertySetProperty",
                new XElement("Key", "Raster"),
                new XElement("Value", inner.Elements())));

        ImageServerSoapEndpoints.TryReadKeyPropertiesBands(operation, 3, out var bands, out var error, out _)
            .Should().BeTrue(error);
        bands.Should().Equal(1);
    }

    [UnitTheory]
    [InlineData("Stretch", "<PropertySetProperty><Key>StretchType</Key><Value>5</Value></PropertySetProperty><PropertySetProperty><Key>Statistics</Key><Value xsi:type='tns:ArrayOfDouble'><Double>0</Double><Double>255</Double></Value></PropertySetProperty>")]
    [InlineData("Colormap", "<PropertySetProperty><Key>Colormap</Key><Value xsi:type='tns:ArrayOfArrayOfDouble'><ArrayOfDouble><Double>0</Double><Double>0</Double><Double>0</Double><Double>0</Double></ArrayOfDouble><ArrayOfDouble><Double>255</Double><Double>255</Double><Double>255</Double><Double>255</Double></ArrayOfDouble></Value></PropertySetProperty>")]
    [InlineData("Clip", "<PropertySetProperty><Key>Extent</Key><Value xsi:type='tns:EnvelopeN'><XMin>0</XMin><YMin>0</YMin><XMax>1</XMax><YMax>1</YMax></Value></PropertySetProperty>")]
    public void StructuredPixelTransform_IsClassifiedAsUnsupportedInsteadOfMalformed(string function, string arguments)
    {
        var operation = XElement.Parse($"<GetKeyPropertiesX xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance'><RenderingRule><FunctionName>{function}</FunctionName><Arguments>{arguments}</Arguments></RenderingRule></GetKeyPropertiesX>");
        ImageServerSoapEndpoints.TryReadKeyPropertiesBands(operation, 3, out _, out _, out var status)
            .Should().BeFalse();
        status.Should().Be(StatusCodes.Status501NotImplemented);
    }

    [UnitTest]
    public void ExcessivelyNestedStructuredRule_IsRejectedBeforeMetadataReads()
    {
        var function = new XElement("FunctionName", "Identity");
        var rule = new XElement("RenderingRule", function);
        for (var depth = 0; depth < 9; depth++)
        {
            rule = new XElement("RenderingRule", new XElement("FunctionName", "Identity"),
                new XElement("Arguments", new XElement("PropertySetProperty",
                    new XElement("Key", "Raster"), new XElement("Value", rule.Elements()))));
        }

        ImageServerSoapEndpoints.TryReadKeyPropertiesBands(new XElement("GetKeyPropertiesX", rule), 3,
            out _, out _, out var status).Should().BeFalse();
        status.Should().Be(StatusCodes.Status400BadRequest);
    }

    [UnitTheory]
    [InlineData("<Int>3</Int>")]
    [InlineData("<Int>-1</Int>")]
    [InlineData("<Int>0.5</Int>")]
    [InlineData("<Int><Nested>0</Nested></Int>")]
    [InlineData("")]
    public void InvalidExtractBand_IsRejectedInsteadOfReturningSourceMetadata(string values)
    {
        ImageServerSoapEndpoints.TryReadKeyPropertiesBands(ExtractBandOperation(values), 3,
            out _, out _, out var status).Should().BeFalse();
        status.Should().Be(StatusCodes.Status400BadRequest);
    }

    [UnitTheory]
    [InlineData("<GetKeyPropertiesX />")]
    [InlineData("<GetKeyPropertiesX xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance'><RenderingRule xsi:nil='true'/></GetKeyPropertiesX>")]
    [InlineData("<GetKeyPropertiesX><RenderingRule><FunctionName>Identity</FunctionName></RenderingRule></GetKeyPropertiesX>")]
    public void AbsentNilAndIdentityRenderingRule_PreserveSourceBands(string xml)
    {
        ImageServerSoapEndpoints.TryReadKeyPropertiesBands(XElement.Parse(xml), 3, out var bands, out _, out _)
            .Should().BeTrue();
        bands.Should().BeNull();
    }

    [UnitTest]
    public void PixelTransformingRenderingRule_IsExplicitlyUnsupported()
    {
        var operation = XElement.Parse("<GetKeyPropertiesX><RenderingRule><FunctionName>Slope</FunctionName></RenderingRule></GetKeyPropertiesX>");
        ImageServerSoapEndpoints.TryReadKeyPropertiesBands(operation, 3, out _, out _, out var status)
            .Should().BeFalse();
        status.Should().Be(StatusCodes.Status501NotImplemented);
    }

    [UnitTheory]
    [InlineData(3, 4, 12, -5, 5, 13)]
    [InlineData(0, 2, -3, 0, 2, 3)]
    [InlineData(-3, -4, 12, -5, 5, 13)]
    public void AffinePixelSizes_UseColumnAndRowVectorLengths(double scaleX, double skewY,
        double skewX, double scaleY, double expectedX, double expectedY)
    {
        var raster = Raster with { GeoTransform = [0, scaleX, skewX, 0, skewY, scaleY] };
        var value = ImageServerSoapEndpoints.BuildKeyPropertiesRasterInfo(raster, Raster.Extent!.Value, [], []);
        double.Parse(value.Element("PixelSizeX")!.Value, System.Globalization.CultureInfo.InvariantCulture).Should().Be(expectedX);
        double.Parse(value.Element("PixelSizeY")!.Value, System.Globalization.CultureInfo.InvariantCulture).Should().Be(expectedY);
        var properties = ImageServerSoapEndpoints.BuildSelectedKeyProperties(raster, null);
        properties.LowCellSize.Should().Be(Math.Min(expectedX, expectedY));
        properties.HighCellSize.Should().Be(Math.Max(expectedX, expectedY));
    }

    [UnitTest]
    public async Task HistogramReader_UsesCallerPublicationPolicyWithoutStorageIdReResolution()
    {
        var graph = Substitute.For<IMetadataV2GraphProvider>();
        var store = Substitute.For<IRasterStore>();
        RasterInfo[] rasters = [Raster, Raster with { Id = 8 }];
        store.GetMosaicHistogramsAsync(1, Arg.Is<long[]>(ids => ids.SequenceEqual(new long[] { 7, 8 })),
            RasterMergeStrategy.Min, null, 256, Arg.Any<RasterIdentifyRendering?>(), Arg.Any<CancellationToken>())
            .Returns([new RasterHistogram { Band = 3, BinCount = 1, Min = 30, Max = 39, Counts = [300] }]);
        var handler = new ImageServerRasterMetadataHandler(graph, store, NullLogger<ImageServerRasterMetadataHandler>.Instance);
        var result = await handler.ReadHistogramsAsync(1, rasters,
            RasterMergeStrategy.Min, CancellationToken.None);
        result.Should().ContainSingle().Which.SourceBand.Should().Be(3);
        await graph.DidNotReceive().GetCurrentAsync(Arg.Any<CancellationToken>());
        await store.DidNotReceive().ListRastersAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().GetMosaicHistogramsAsync(Arg.Any<int>(), Arg.Any<long[]>(), RasterMergeStrategy.Newest,
            Arg.Any<int[]?>(), Arg.Any<int>(), Arg.Any<RasterIdentifyRendering?>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task HistogramReader_RequestsOnlySelectedBandsAndDoesNotProjectUnselectedInvalidHistogram()
    {
        var graph = Substitute.For<IMetadataV2GraphProvider>();
        var store = Substitute.For<IRasterStore>();
        store.GetHistogramsAsync(1, Raster.Id, Arg.Is<int[]>(bands => bands.SequenceEqual(new[] { 3 })),
            256, Arg.Any<RasterIdentifyRendering?>(), Arg.Any<CancellationToken>())
            .Returns([
                new RasterHistogram { Band = 1, BinCount = 2, Min = 10, Max = 19, Counts = [100] },
                new RasterHistogram { Band = 3, BinCount = 1, Min = 30, Max = 39, Counts = [300] }
            ]);
        var handler = new ImageServerRasterMetadataHandler(graph, store, NullLogger<ImageServerRasterMetadataHandler>.Instance);
        var result = await handler.ReadHistogramsAsync(1, new[] { Raster }, RasterMergeStrategy.Min,
            CancellationToken.None, [3]);
        result.Should().ContainSingle().Which.SourceBand.Should().Be(3);
        await store.Received(1).GetHistogramsAsync(1, Raster.Id,
            Arg.Is<int[]>(bands => bands.SequenceEqual(new[] { 3 })), 256,
            Arg.Any<RasterIdentifyRendering?>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public void StatisticsBudgetKey_SeparatesBandSelectionsFromAllBands()
    {
        var all = ImageServerStatisticsBudget.CreateStatisticsOperationKey(null, 1, [7], RasterMergeStrategy.Min);
        var first = ImageServerStatisticsBudget.CreateStatisticsOperationKey(null, 1, [7], RasterMergeStrategy.Min, [1]);
        var third = ImageServerStatisticsBudget.CreateStatisticsOperationKey(null, 1, [7], RasterMergeStrategy.Min, [3]);
        first.Should().NotBe(all).And.NotBe(third);
    }

    private static XElement ExtractBandOperation(string values)
        => XElement.Parse($"""
            <GetKeyPropertiesX xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <RenderingRule><FunctionName>ExtractBand</FunctionName><Arguments>
                <PropertySetProperty><Key>BandIds</Key><Value xsi:type="tns:ArrayOfInt">{values}</Value></PropertySetProperty>
              </Arguments></RenderingRule>
            </GetKeyPropertiesX>
            """);

}
