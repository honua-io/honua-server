// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerKeyPropertiesRasterInfoTests
{
    private const string Xsi = "http://www.w3.org/2001/XMLSchema-instance";
    private static readonly RasterInfo Raster = new()
    {
        Id = 7, LayerId = 1, Name = "RGB", Width = 64, Height = 32, BandCount = 3,
        PixelType = "8BUI", NoDataValue = 0,
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
}
