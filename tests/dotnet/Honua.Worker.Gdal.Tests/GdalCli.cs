// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Linq;
using System.Text;
using System.Text.Json;
using Honua.TestKit.Constants;
using Honua.TestKit.RasterSemantics;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

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
        return pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Join(dir, tool)));
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
        => Path.Join(Path.GetTempPath(), suite, Guid.NewGuid().ToString("N"));

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
        var demPath = Path.Join(scratch, "sample-dem.tif");
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
            var vrtPath = Path.Join(scratch, "sample.vrt");
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

    /// <summary>Creates the canonical 3x3 east-rising plane used by the slope fixture.</summary>
    public static async Task<byte[]> GenerateSemanticPlaneDemAsync(string scratch)
    {
        Directory.CreateDirectory(scratch);
        var gridPath = Path.Join(scratch, "semantic-plane.asc");
        var demPath = Path.Join(scratch, "semantic-plane.tif");
        await File.WriteAllTextAsync(gridPath, """
            ncols 3
            nrows 3
            xllcorner 500000
            yllcorner 2199997
            cellsize 1
            NODATA_value -9999
            0 1 2
            0 1 2
            0 1 2
            """).ConfigureAwait(false);
        await RunOrThrowAsync(
            "gdal_translate",
            ["-q", "-of", "GTiff", "-ot", "Float32", "-a_srs", "EPSG:32604", gridPath, demPath],
            scratch).ConfigureAwait(false);
        return await File.ReadAllBytesAsync(demPath).ConfigureAwait(false);
    }

    /// <summary>Decodes a small GeoTIFF into the provider-neutral semantic snapshot.</summary>
    public static async Task<RasterSemanticSnapshot> InspectSmallRasterAsync(
        byte[] payload,
        string scratch)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Directory.CreateDirectory(scratch);
        var path = Path.Join(scratch, "semantic-inspect.tif");
        await File.WriteAllBytesAsync(path, payload).ConfigureAwait(false);

        var infoResult = await RunOrThrowAsync("gdalinfo", ["-json", path], scratch).ConfigureAwait(false);
        using var info = JsonDocument.Parse(infoResult.StandardOutput);
        var root = info.RootElement;
        var size = root.GetProperty("size");
        var width = size[0].GetInt32();
        var height = size[1].GetInt32();
        if (checked((long)width * height) > 1_048_576)
        {
            throw new InvalidOperationException("Semantic raster inspection is limited to 1,048,576 cells.");
        }

        var geoTransform = root.GetProperty("geoTransform")
            .EnumerateArray()
            .Select(value => value.GetDouble())
            .ToArray();
        var band = root.GetProperty("bands")[0];
        var pixelType = band.GetProperty("type").GetString() switch
        {
            "Byte" => "8BUI",
            "Int16" => "16BSI",
            "UInt16" => "16BUI",
            "Int32" => "32BSI",
            "UInt32" => "32BUI",
            "Float32" => "32BF",
            "Float64" => "64BF",
            var type => throw new InvalidOperationException($"Unsupported GDAL semantic pixel type '{type}'."),
        };
        var colorInterpretation = band.TryGetProperty("colorInterpretation", out var color)
            ? color.GetString()?.ToLowerInvariant() ?? "undefined"
            : "undefined";
        var noData = band.TryGetProperty("noDataValue", out var noDataElement)
            ? noDataElement.GetDouble()
            : (double?)null;
        var srsResult = await RunOrThrowAsync("gdalsrsinfo", ["-o", "epsg", path], scratch).ConfigureAwait(false);
        var sridToken = srsResult.StandardOutput.Trim();
        var separator = sridToken.LastIndexOf(':');
        if (separator < 0 || !int.TryParse(sridToken[(separator + 1)..], out var srid))
        {
            throw new InvalidOperationException($"GDAL did not return a canonical EPSG identifier: '{sridToken}'.");
        }

        var cells = new List<double?>(checked(width * height));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var valueResult = await RunOrThrowAsync(
                    "gdallocationinfo",
                    [
                        "-valonly",
                        "-b",
                        "1",
                        path,
                        x.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        y.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ],
                    scratch).ConfigureAwait(false);
                if (!double.TryParse(
                        valueResult.StandardOutput.Trim(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var value))
                {
                    throw new InvalidOperationException("GDAL returned a non-numeric semantic cell value.");
                }

                cells.Add(noData is { } marker && value.Equals(marker) ? null : value);
            }
        }

        return new RasterSemanticSnapshot
        {
            Grid = new RasterSemanticGrid
            {
                Width = width,
                Height = height,
                Srid = srid,
                Transform = geoTransform,
            },
            Bands =
            [
                new RasterSemanticBand
                {
                    PixelType = pixelType,
                    ColorInterpretation = colorInterpretation,
                    NoData = noData,
                    Cells = cells,
                },
            ],
        };
    }

    /// <summary>Returns the installed GDAL CLI version string.</summary>
    public static async Task<string> VersionAsync(string scratch)
    {
        var result = await RunOrThrowAsync("gdalinfo", ["--version"], scratch).ConfigureAwait(false);
        return result.StandardOutput.Trim();
    }

    private static async Task<GdalCommandResult> RunOrThrowAsync(
        string tool,
        IReadOnlyList<string> args,
        string scratch)
    {
        var runner = new ProcessGdalCommandRunner(
            Microsoft.Extensions.Options.Options.Create(new GdalHardeningOptions()),
            NullLogger<ProcessGdalCommandRunner>.Instance);
        var result = await runner.RunAsync(tool, args, scratch, CancellationToken.None).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Failed to synthesize sample DEM via {tool}: exit={result.ExitCode}; stderr={result.StandardError}");
        }

        return result;
    }
}

/// <summary>
/// Fact attribute for real-GDAL integration tests that skip when the requested
/// GDAL CLI tool is not available on the host.
/// </summary>
[TraitDiscoverer("Honua.Worker.Gdal.Tests.GdalCliFactDiscoverer", "Honua.Worker.Gdal.Tests")]
public sealed class GdalCliFactAttribute : FactAttribute, ITraitAttribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GdalCliFactAttribute"/> class.
    /// </summary>
    /// <param name="tool">The GDAL CLI tool required by the test.</param>
    public GdalCliFactAttribute(string tool)
    {
        if (!GdalCli.Available(tool))
        {
            Skip = $"GDAL CLI tool '{tool}' is not available on PATH.";
        }
    }
}

/// <summary>
/// Emits integration-test traits for <see cref="GdalCliFactAttribute"/>.
/// </summary>
public sealed class GdalCliFactDiscoverer : ITraitDiscoverer
{
    /// <inheritdoc />
    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
    {
        return
        [
            new KeyValuePair<string, string>("Category", "Integration"),
            new KeyValuePair<string, string>("Category", "GDAL"),
            new KeyValuePair<string, string>("Tier", Tiers.Integration)
        ];
    }
}
