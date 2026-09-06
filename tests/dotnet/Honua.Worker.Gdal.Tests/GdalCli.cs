// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.TestKit.Constants;
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
    /// Gets a value indicating whether the environment requires the GDAL CLI to be
    /// present, so a missing tool must fail the test instead of skipping it.
    /// </summary>
    /// <remarks>
    /// Set by the GDAL-capable CI job. See <see cref="GdalCliFactAttribute"/>.
    /// </remarks>
    public static bool RequireCli => string.Equals(
        Environment.GetEnvironmentVariable(GdalCliFactAttribute.RequireEnvironmentVariable),
        "true",
        StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// Elevation burned into <see cref="GenerateSampleDemAsync"/>'s 16x16 Float32 raster. The
    /// surface is constant, which is what makes an analytical slope/aspect oracle possible
    /// (honua-server#4400).
    /// </summary>
    public const double SampleDemConstantElevation = 100.0;

    /// <summary>
    /// Reads band-1 statistics from a GeoTIFF with <c>gdalinfo -json -stats</c>, so a test can
    /// assert produced cell values instead of magic bytes. Returns <c>null</c> when
    /// <c>gdalinfo</c> is unavailable and the CLI is not required.
    /// </summary>
    public static async Task<(double Minimum, double Maximum)?> TryReadBandStatisticsAsync(
        byte[] geoTiff,
        string scratch)
    {
        if (!Available("gdalinfo") && !RequireCli)
        {
            return null;
        }

        Directory.CreateDirectory(scratch);
        var path = Path.Join(scratch, $"stats-{Guid.NewGuid():N}.tif");
        await File.WriteAllBytesAsync(path, geoTiff).ConfigureAwait(false);

        var stdout = await RunCapturingAsync("gdalinfo", ["-json", "-stats", path], scratch).ConfigureAwait(false);
        using var document = JsonDocument.Parse(stdout);
        var band = document.RootElement.GetProperty("bands").EnumerateArray().First();
        var metadata = band.GetProperty("metadata").GetProperty(string.Empty);

        var minimum = double.Parse(
            metadata.GetProperty("STATISTICS_MINIMUM").GetString()!, CultureInfo.InvariantCulture);
        var maximum = double.Parse(
            metadata.GetProperty("STATISTICS_MAXIMUM").GetString()!, CultureInfo.InvariantCulture);
        return (minimum, maximum);
    }

    private static async Task<string> RunCapturingAsync(string tool, IReadOnlyList<string> args, string scratch)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo(tool)
        {
            WorkingDirectory = scratch,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{tool}'.");
        var stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"'{tool}' exited {process.ExitCode}: {stderr}");
        }

        return stdout;
    }

    private static async Task RunOrThrowAsync(string tool, IReadOnlyList<string> args, string scratch)
    {
        var runner = new ProcessGdalCommandRunner(
            Microsoft.Extensions.Options.Options.Create(new GdalHardeningOptions()),
            Microsoft.Extensions.Options.Options.Create(new AwsS3Options()),
            Microsoft.Extensions.Options.Options.Create(new AzureBlobOptions()),
            NullLogger<ProcessGdalCommandRunner>.Instance);
        var result = await runner.RunAsync(tool, args, scratch, CancellationToken.None).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Failed to synthesize sample DEM via {tool}: exit={result.ExitCode}; stderr={result.StandardError}");
        }
    }
}

/// <summary>
/// Fact attribute for real-GDAL integration tests that skip when the requested
/// GDAL CLI tool is not available on the host — unless the environment demands
/// the tooling, in which case the test runs and fails.
/// </summary>
/// <remarks>
/// Skipping is the right default on a dev box, but it made these cases invisible
/// in CI: the tool was absent, every case reported "skipped", and
/// <c>dotnet test</c> still exited 0, so the only coverage of the real
/// <c>gdaldem</c> / <c>ogr2ogr</c> command lines was green-by-absence (#3271).
/// Setting <c>HONUA_REQUIRE_GDAL_CLI=true</c> — as the GDAL-capable CI job does —
/// suppresses the skip, so a runner that lost its GDAL install fails loudly
/// instead of silently dropping the coverage. This mirrors the TestKit
/// <c>RequiredEnvironmentFactAttribute</c> / <c>CloudTestAttribute</c> pattern of
/// letting the environment decide whether a gated case skips.
/// </remarks>
[TraitDiscoverer("Honua.Worker.Gdal.Tests.GdalCliFactDiscoverer", "Honua.Worker.Gdal.Tests")]
public sealed class GdalCliFactAttribute : FactAttribute, ITraitAttribute
{
    /// <summary>
    /// Environment variable that turns "GDAL CLI missing" from a skip into a failure.
    /// </summary>
    public const string RequireEnvironmentVariable = "HONUA_REQUIRE_GDAL_CLI";

    /// <summary>
    /// Initializes a new instance of the <see cref="GdalCliFactAttribute"/> class.
    /// </summary>
    /// <param name="tool">The GDAL CLI tool required by the test.</param>
    public GdalCliFactAttribute(string tool)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);

        if (GdalCli.Available(tool) || GdalCli.RequireCli)
        {
            return;
        }

        Skip = $"GDAL CLI tool '{tool}' is not available on PATH. "
            + $"Set {RequireEnvironmentVariable}=true to fail instead of skipping.";
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
