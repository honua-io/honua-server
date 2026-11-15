// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Worker.Gdal.Execution;

// Positive GDAL driver restriction for raster tools that accept the input-format
// flag. Legacy gdal_contour does not accept it (its synopsis is the classic
// executable, not "gdal raster contour"). gdalinfo on an object-store path declared
// through GdalRemoteInputScope, gdaldem, gdal_viewshed, gdal_polygonize.py,
// gdal_grid, ogr2ogr and Python calc scripts are also left unrestricted: those tools
// either do not accept the flag or must open NetCDF/HDF5/Zarr/GRIB. Contour inputs
// are still admitted by the byte guard before the process is spawned.
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

    // gdal_translate and gdalwarp always get the allowlisted input drivers; gdalinfo
    // does too unless an argument is an executor-declared object-store path. Every
    // other tool, including gdal_contour, is returned unchanged.
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
        if (tool is "gdal_translate" or "gdalwarp")
        {
            return true;
        }

        if (!string.Equals(tool, "gdalinfo", StringComparison.Ordinal))
        {
            return false;
        }

        // Only an executor-declared object-store input (a trusted COG read through
        // GdalRemoteInputScope) leaves gdalinfo unrestricted; a job value that merely
        // contains "/vsi" does not.
        return !GdalRemoteInputScope.Classify(arguments).Remote;
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
