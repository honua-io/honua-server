// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Positive GDAL driver restriction for raster tools that accept <c>-if</c>.
/// Multidim <c>gdalinfo</c> on <c>/vsi</c>, <c>gdaldem</c>, <c>gdal_viewshed</c>,
/// <c>gdal_polygonize.py</c>, <c>gdal_grid</c>, <c>ogr2ogr</c>, and Python calc
/// scripts are left unrestricted: those tools either do not accept <c>-if</c> or
/// must open NetCDF/HDF5/Zarr/GRIB.
/// </summary>
internal static class GdalRasterDriverAllowlist
{
    /// <summary>
    /// Runs <paramref name="tool"/> after prefixing the allowlisted <c>-if</c> drivers
    /// when the tool accepts them.
    /// </summary>
    public static Task<GdalCommandResult> RunAsync(
        IGdalCommandRunner runner,
        string tool,
        IReadOnlyList<string> arguments,
        GdalWorkerOptions options,
        string workingDirectory,
        CancellationToken cancellationToken)
        => runner.RunAsync(tool, Prefix(tool, arguments, options), workingDirectory, cancellationToken);

    /// <summary>
    /// Prefixes <c>-if &lt;driver&gt;</c> for <c>gdal_translate</c>, <c>gdalwarp</c>,
    /// <c>gdal_contour</c>, and non-VSI <c>gdalinfo</c>. Other tools are returned unchanged.
    /// </summary>
    public static IReadOnlyList<string> Prefix(
        string tool,
        IReadOnlyList<string> arguments,
        GdalWorkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(options);

        if (!ShouldRestrict(tool, arguments))
        {
            return arguments;
        }

        var drivers = ResolveDrivers(options);
        if (drivers.Count == 0)
        {
            throw new InvalidOperationException(
                $"GDAL raster input allowlist is empty. Refusing to run '{tool}' without -if drivers.");
        }

        var prefixed = new List<string>(arguments.Count + (drivers.Count * 2));
        foreach (var driver in drivers)
        {
            prefixed.Add("-if");
            prefixed.Add(driver);
        }

        prefixed.AddRange(arguments);
        return prefixed;
    }

    private static bool ShouldRestrict(string tool, IReadOnlyList<string> arguments)
    {
        if (tool is "gdal_translate" or "gdalwarp" or "gdal_contour")
        {
            return true;
        }

        if (!string.Equals(tool, "gdalinfo", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var argument in arguments)
        {
            if (argument.Contains("/vsi", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static List<string> ResolveDrivers(GdalWorkerOptions options)
    {
        var drivers = new List<string>();
        if (options.AllowedRasterInputFormats is null)
        {
            return drivers;
        }

        foreach (var allowed in options.AllowedRasterInputFormats)
        {
            if (string.IsNullOrWhiteSpace(allowed))
            {
                continue;
            }

            var driver = ToGdalDriver(allowed.Trim());
            if (driver is null || drivers.Contains(driver, StringComparer.Ordinal))
            {
                continue;
            }

            drivers.Add(driver);
        }

        return drivers;
    }

    /// <summary>
    /// Maps an allowlist key onto the GDAL driver short name passed to <c>-if</c>.
    /// JPEG2000 uses the open-source JP2OpenJPEG driver.
    /// </summary>
    private static string? ToGdalDriver(string format) => format.ToUpperInvariant() switch
    {
        "TIFF" => "GTiff",
        "PNG" => "PNG",
        "JPEG" => "JPEG",
        "JPEG2000" => "JP2OpenJPEG",
        "GIF" => "GIF",
        "BMP" => "BMP",
        "NITF" => "NITF",
        "HFA" => "HFA",
        _ => null,
    };
}
