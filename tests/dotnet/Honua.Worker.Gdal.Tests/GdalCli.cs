// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Worker.Gdal.Tests;

/// <summary>
/// Shared helpers for the integration tests that opt into hitting the real GDAL
/// CLI tooling when it is installed on the host (worker image / dev box) and
/// skip cleanly when it is not (lean CI agents).
/// </summary>
internal static class GdalCli
{
    /// <summary>
    /// Returns whether the given GDAL CLI tool is reachable on PATH.
    /// </summary>
    public static bool Available(string tool)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(Path.Combine(dir, tool)))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Decodes the payload from a canonical <c>data:&lt;type&gt;;base64,&lt;payload&gt;</c>
    /// URI emitted by the worker executors.
    /// </summary>
    public static byte[] DecodeDataUri(string dataUri)
    {
        var comma = dataUri.IndexOf(',', StringComparison.Ordinal);
        return Convert.FromBase64String(dataUri[(comma + 1)..]);
    }

    /// <summary>
    /// Synthesizes a 16×16 single-band Float32 GeoTIFF DEM at the given scratch
    /// path using <c>gdal_create</c> (when present) or <c>gdal_translate</c>
    /// as a fallback. Returns the raw GeoTIFF bytes ready to be base64-encoded
    /// onto the durable spec.
    /// </summary>
    public static byte[] GenerateSampleDem(string scratch)
    {
        Directory.CreateDirectory(scratch);
        var demPath = Path.Combine(scratch, "sample-dem.tif");
        if (Available("gdal_create"))
        {
            var args = new[]
            {
                "-outsize", "16", "16",
                "-bands", "1",
                "-ot", "Float32",
                "-burn", "100",
                "-of", "GTiff",
                demPath,
            };
            RunOrThrow("gdal_create", args, scratch);
        }
        else
        {
            // Fallback: gdal_translate over a tiny VRT. The VRT holds a 16×16
            // constant-value Float32 dataset so gdaldem has finite slope inputs.
            var vrtPath = Path.Combine(scratch, "sample.vrt");
            File.WriteAllText(vrtPath, """
                <VRTDataset rasterXSize="16" rasterYSize="16">
                  <VRTRasterBand dataType="Float32" band="1">
                    <ColorInterp>Gray</ColorInterp>
                    <NoDataValue>0</NoDataValue>
                  </VRTRasterBand>
                </VRTDataset>
                """);
            RunOrThrow("gdal_translate", new[] { "-of", "GTiff", "-a_nodata", "0", vrtPath, demPath }, scratch);
        }
        return File.ReadAllBytes(demPath);
    }

    private static void RunOrThrow(string tool, IReadOnlyList<string> args, string scratch)
    {
        var runner = new ProcessGdalCommandRunner(NullLogger<ProcessGdalCommandRunner>.Instance);
        var result = runner.RunAsync(tool, args, scratch, CancellationToken.None).GetAwaiter().GetResult();
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Failed to synthesize sample DEM via {tool}: exit={result.ExitCode}; stderr={result.StandardError}");
        }
    }
}
