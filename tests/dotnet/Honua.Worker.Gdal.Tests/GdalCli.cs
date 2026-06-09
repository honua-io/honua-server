// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

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
    /// Base64-encodes UTF-8 text for use as a durable step-input payload.
    /// </summary>
    public static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// Allocates a unique scratch directory path under the OS temp root for an
    /// isolated executor run. The directory is created lazily by the executor.
    /// </summary>
    public static string NewScratch(string suite)
        => Path.Combine(Path.GetTempPath(), suite, Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Best-effort recursive cleanup of a scratch directory; swallows the
    /// transient <see cref="IOException"/> that can surface when GDAL output
    /// handles are still settling.
    /// </summary>
    public static void CleanupScratch(string scratch)
    {
        try
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    /// <summary>
    /// Synthesizes a 16×16 single-band Float32 GeoTIFF DEM at the given scratch
    /// path using <c>gdal_create</c> (when present) or <c>gdal_translate</c>
    /// as a fallback. Returns the raw GeoTIFF bytes ready to be base64-encoded
    /// onto the durable spec.
    /// </summary>
    public static async Task<byte[]> GenerateSampleDemAsync(string scratch)
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
            await RunOrThrowAsync("gdal_create", args, scratch).ConfigureAwait(false);
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
            await RunOrThrowAsync("gdal_translate", new[] { "-of", "GTiff", "-a_nodata", "0", vrtPath, demPath }, scratch)
                .ConfigureAwait(false);
        }
        return File.ReadAllBytes(demPath);
    }

    private static async Task RunOrThrowAsync(string tool, IReadOnlyList<string> args, string scratch)
    {
        var runner = new ProcessGdalCommandRunner(NullLogger<ProcessGdalCommandRunner>.Instance);
        var result = await runner.RunAsync(tool, args, scratch, CancellationToken.None).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Failed to synthesize sample DEM via {tool}: exit={result.ExitCode}; stderr={result.StandardError}");
        }
    }
}

internal sealed class GdalCliFactAttribute : FactAttribute
{
    public GdalCliFactAttribute(string tool)
    {
        if (!GdalCli.Available(tool))
        {
            Skip = $"GDAL CLI tool '{tool}' is not available on PATH.";
        }
    }
}
