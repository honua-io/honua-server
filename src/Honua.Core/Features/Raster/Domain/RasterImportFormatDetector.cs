// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

/// <summary>
/// Canonical filename-based format detection for the built-in PostGIS raster importer.
/// </summary>
/// <remarks>
/// Submission-time authorization and worker-side import must use this same detector. If they
/// disagree, an inactive target can be falsely denied or a newly supported target can bypass
/// its layer mutation gate.
/// </remarks>
public static class RasterImportFormatDetector
{
    private static readonly IReadOnlyList<string> SupportedExtensionValues =
        Array.AsReadOnly<string>([".tif", ".tiff", ".png", ".jpg", ".jpeg"]);

    /// <summary>Gets the filename extensions supported by the built-in raster importer.</summary>
    public static IReadOnlyList<string> SupportedExtensions => SupportedExtensionValues;

    /// <summary>Detects a supported raster import format from a filename extension.</summary>
    /// <param name="fileName">Original source filename.</param>
    /// <returns>The supported format, or <see langword="null"/> when the extension is unsupported.</returns>
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
