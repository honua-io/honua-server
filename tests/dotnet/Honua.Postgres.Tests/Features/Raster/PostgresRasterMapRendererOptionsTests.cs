// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Postgres.Features.Raster;

namespace Honua.Postgres.Tests.Features.Raster;

public sealed class PostgresRasterMapRendererOptionsTests
{
    [Theory]
    [InlineData(1, "QUALITY=10")]
    [InlineData(10, "QUALITY=10")]
    [InlineData(100, "QUALITY=100")]
    public void BuildGdalCreationOptions_JpegFormat_ClampsQualityToGdalRange(
        int quality,
        string expectedOption)
    {
        var options = PostgresRasterMapRenderer.BuildGdalCreationOptions(
            "JPEG",
            CreateRequest(RasterFormat.JPEG, quality));

        options.Should().Equal(expectedOption);
    }

    [Fact]
    public void BuildGdalCreationOptions_GtiffFormat_ClampsJpegQualityToGdalRange()
    {
        var options = PostgresRasterMapRenderer.BuildGdalCreationOptions(
            "GTiff",
            CreateRequest(RasterFormat.TIFF, 1));

        options.Should().Equal("COMPRESS=JPEG", "JPEG_QUALITY=10");
    }

    private static MapRenderRequest CreateRequest(RasterFormat format, int quality)
        => new()
        {
            BoundingBox = [-180d, -90d, 180d, 90d],
            Width = 256,
            Height = 256,
            Format = format,
            Quality = quality
        };
}
