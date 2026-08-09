// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Raster;

public sealed class RasterImportFormatDetectorTests
{
    [UnitTest]
    public void DetectFormat_SupportedExtensions_ReturnCanonicalFormats()
    {
        var cases = new Dictionary<string, SupportedRasterFormat>(StringComparer.Ordinal)
        {
            ["imagery.tif"] = SupportedRasterFormat.GeoTiff,
            ["imagery.TIFF"] = SupportedRasterFormat.GeoTiff,
            ["imagery.png"] = SupportedRasterFormat.PngWorldFile,
            ["imagery.jpg"] = SupportedRasterFormat.JpegWorldFile,
            ["imagery.JPEG"] = SupportedRasterFormat.JpegWorldFile,
        };

        foreach (var (fileName, expected) in cases)
        {
            RasterImportFormatDetector.DetectFormat(fileName).Should().Be(expected);
        }
    }

    [UnitTest]
    public void DetectFormat_UnsupportedOrMissingExtension_ReturnsNull()
    {
        RasterImportFormatDetector.DetectFormat("parcels.geojson").Should().BeNull();
        RasterImportFormatDetector.DetectFormat("imagery").Should().BeNull();
        RasterImportFormatDetector.DetectFormat(null).Should().BeNull();
    }
}
