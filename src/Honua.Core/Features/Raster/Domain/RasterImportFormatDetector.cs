// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

// Authorization and the worker must share this detector, or an inactive target can be denied while a newly supported one skips its mutation gate.
public static class RasterImportFormatDetector
{
    private static readonly IReadOnlyList<string> SupportedExtensionValues =
        Array.AsReadOnly<string>([".tif", ".tiff", ".png", ".jpg", ".jpeg"]);

    public static IReadOnlyList<string> SupportedExtensions => SupportedExtensionValues;

    public static SupportedRasterFormat? DetectFormat(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var extension = Path.GetExtension(fileName);
        if (string.Equals(extension, ".tif", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".tiff", StringComparison.OrdinalIgnoreCase))
        {
            return SupportedRasterFormat.GeoTiff;
        }

        if (string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase))
        {
            return SupportedRasterFormat.PngWorldFile;
        }

        return string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase)
                ? SupportedRasterFormat.JpegWorldFile
                : null;
    }
}
