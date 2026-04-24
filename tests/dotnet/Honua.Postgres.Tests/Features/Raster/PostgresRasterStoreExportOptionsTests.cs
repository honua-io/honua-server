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

    [UnitTest]
    public void BuildExportCreationOptions_TiffLz77Compression_UsesDeflate()
    {
        var options = PostgresRasterStore.BuildExportCreationOptions(
            new RasterQuery { TiffCompression = TiffCompression.Deflate },
            "GTiff");

        options.Should().Equal("COMPRESS=DEFLATE");
    }
}
