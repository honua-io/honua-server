// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Postgres.Features.Raster;
using Honua.TestKit.Attributes;

namespace Honua.Postgres.Tests.Features.Raster;

[Collection("Unit")]
public sealed class PostgresRasterStoreExportOptionsTests
{
    [UnitTest]
    public void BuildExportCreationOptions_JpegFormat_UsesRequestedQuality()
    {
        var options = PostgresRasterStore.BuildExportCreationOptions(
            new RasterQuery { Quality = 82 },
            "JPEG");

        options.Should().Equal("QUALITY=82");
    }

    [Theory]
    [InlineData(0, "QUALITY=10")]
    [InlineData(1, "QUALITY=10")]
    [InlineData(10, "QUALITY=10")]
    [InlineData(100, "QUALITY=100")]
    public void BuildExportCreationOptions_JpegFormat_ClampsQualityToGdalRange(
        int quality,
        string expectedOption)
    {
        var options = PostgresRasterStore.BuildExportCreationOptions(
            new RasterQuery { Quality = quality },
            "JPEG");

        options.Should().Equal(expectedOption);
    }

    [UnitTest]
    public void BuildExportCreationOptions_TiffJpegCompression_UsesCompressionAndQuality()
    {
        var options = PostgresRasterStore.BuildExportCreationOptions(
            new RasterQuery
            {
                TiffCompression = TiffCompression.JPEG,
                Quality = 67
            },
            "GTiff");

        options.Should().Equal("COMPRESS=JPEG", "JPEG_QUALITY=67");
    }

    [Fact]
    public void BuildExportCreationOptions_TiffJpegCompression_ClampsQualityToGdalRange()
    {
        var options = PostgresRasterStore.BuildExportCreationOptions(
            new RasterQuery
            {
                TiffCompression = TiffCompression.JPEG,
                Quality = 1
            },
            "GTiff");

        options.Should().Equal("COMPRESS=JPEG", "JPEG_QUALITY=10");
    }

    [UnitTest]
    public void BuildExportCreationOptions_TiffLz77Compression_UsesDeflate()
    {
        var options = PostgresRasterStore.BuildExportCreationOptions(
            new RasterQuery { TiffCompression = TiffCompression.Deflate },
            "GTiff");

        options.Should().Equal("COMPRESS=DEFLATE");
    }
}
