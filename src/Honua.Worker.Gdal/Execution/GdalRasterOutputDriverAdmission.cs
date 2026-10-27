// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Admission for <c>conversion.raster-format</c> output drivers (#3307).
/// Resolves the catalog <c>targetFormat</c> and rejects a driver that
/// <see cref="GdalRuntimeHardening"/> will place in <c>GDAL_SKIP</c>, before
/// <c>gdal_translate</c> can fail with a raw driver error.
/// </summary>
internal static class GdalRasterOutputDriverAdmission
{
    /// <summary>
    /// Resolved <c>gdal_translate -of</c> target: driver short name, scratch
    /// extension, artifact content type, and whether <c>COMPRESS</c> is meaningful.
    /// </summary>
    internal readonly record struct Target(
        string Driver,
        string Extension,
        string ContentType,
        bool SupportsCompression);

    private static readonly Target GeoTiffTarget =
        new("GTiff", "tif", "image/tiff; application=geotiff", SupportsCompression: true);

    private static readonly Target JpegTarget =
        new("JPEG", "jpg", "image/jpeg", SupportsCompression: false);

    // Maps the catalog's targetFormat enum onto the gdal_translate -of driver name,
    // the scratch output extension, and the published artifact content type.
    //
    // Mirrors ProcessPlanValidator.RasterFormatValues, which is itself built from
    // ProcessValueDomains.RasterFormat — the same array the catalog publishes as
    // targetFormat's AllowedValues. Every alias the plan validator accepts and the
    // catalog advertises must therefore normalize onto a driver here: the GeoTIFF /
    // TIFF / TIF spellings all mean the GTiff driver and JPG means JPEG. Without the
    // aliases an OGC Processes or GPServer client that picks an advertised value
    // passes catalog and schema validation and then has its job killed at the CLI
    // boundary with an invalid-format failure (#3048 review). COG stays a distinct
    // entry because it is a real, separate GDAL driver rather than a spelling of
    // GTiff. GdalRasterConversionExecutorTests pins the correspondence by driving the
    // executor with every value of ProcessValueDomains.RasterFormat.
    private static readonly FrozenDictionary<string, Target> Formats =
        new Dictionary<string, Target>(StringComparer.OrdinalIgnoreCase)
        {
            ["GTiff"] = GeoTiffTarget,
            ["GeoTIFF"] = GeoTiffTarget,
            ["TIFF"] = GeoTiffTarget,
            ["TIF"] = GeoTiffTarget,
            ["COG"] = new Target("COG", "tif", "image/tiff; application=geotiff", SupportsCompression: true),
            ["PNG"] = new Target("PNG", "png", "image/png", SupportsCompression: false),
            ["JPEG"] = JpegTarget,
            ["JPG"] = JpegTarget,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    // Rendered from Formats so the rejection message can never advertise a set the
    // executor does not actually accept.
    private static readonly string AllowedFormatList =
        string.Join(", ", Formats.Keys.Order(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Admits <paramref name="targetFormat"/> or explains why it cannot be written.
    /// An unknown value keeps the historical allowed-set error. A resolved driver
    /// present in <see cref="GdalRuntimeHardening.EffectiveSkipDrivers"/> is rejected
    /// with the driver short name and <c>GdalWorker:Hardening:SkipDrivers</c>.
    /// </summary>
    /// <param name="targetFormat">Raw <c>targetFormat</c> input, including aliases.</param>
    /// <param name="hardening">The hardening options the GDAL subprocess will inherit.</param>
    /// <param name="target">The resolved driver when admission succeeds.</param>
    /// <param name="error">Empty when admitted; otherwise a client-facing reason.</param>
    /// <returns><see langword="true"/> when the driver may be passed to <c>-of</c>.</returns>
    public static bool TryAdmit(
        string? targetFormat,
        GdalHardeningOptions hardening,
        out Target target,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(hardening);
        target = new Target(string.Empty, string.Empty, string.Empty, SupportsCompression: false);

        if (string.IsNullOrWhiteSpace(targetFormat))
        {
            error = "Invalid raster-format inputs: missing required input 'targetFormat'.";
            return false;
        }

        if (!Formats.TryGetValue(targetFormat.Trim(), out target))
        {
            error =
                $"Invalid raster-format inputs: 'targetFormat' value '{targetFormat}' is not in the allowed set " +
                $"({AllowedFormatList}).";
            return false;
        }

        foreach (var skipped in GdalRuntimeHardening.EffectiveSkipDrivers(hardening))
        {
            // GDAL resolves GDAL_SKIP through GetDriverByName, which is case-insensitive.
            if (!string.Equals(skipped, target.Driver, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            error =
                $"Invalid raster-format inputs: output GDAL driver '{target.Driver}' is disabled by " +
                $"{GdalHardeningOptions.SectionName}:SkipDrivers (configured as '{skipped}'). " +
                $"Remove '{skipped}' from that setting to allow conversion.raster-format to write this format.";
            return false;
        }

        error = "";
        return true;
    }
}
