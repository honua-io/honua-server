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
    /// Edge length of <see cref="GenerateSampleDemAsync"/>'s square sample DEM, in cells.
    /// </summary>
    public const int SampleDemSize = 16;

    /// <summary>
    /// Elevation burned into <see cref="GenerateSampleDemAsync"/>'s Float32 raster. The
    /// surface is constant, which is what makes an analytical slope/aspect oracle possible
    /// (honua-server#4400).
    /// </summary>
    public const double SampleDemConstantElevation = 100.0;

    /// <summary>
    /// NoData value stamped on the sample DEM. It deliberately differs from
    /// <see cref="SampleDemConstantElevation"/> so every cell of the raster is valid, and it
    /// matches <c>gdaldem</c>'s own default output NoData.
    /// </summary>
    public const double SampleDemNoData = -9999.0;

    /// <summary>
    /// Synthesizes a <see cref="SampleDemSize"/>-square single-band Float32 GeoTIFF DEM at the
    /// given scratch path using <c>gdal_create</c> (when present) or <c>gdal_translate</c>
    /// as a fallback. Every cell carries <see cref="SampleDemConstantElevation"/> and none of
    /// them are NoData. Returns the raw GeoTIFF bytes ready to be base64-encoded onto the
    /// durable spec.
    /// </summary>
    public static async Task<byte[]> GenerateSampleDemAsync(string scratch)
    {
        Directory.CreateDirectory(scratch);
        var demPath = Path.Join(scratch, "sample-dem.tif");
        var size = SampleDemSize.ToString(CultureInfo.InvariantCulture);
        var elevation = SampleDemConstantElevation.ToString(CultureInfo.InvariantCulture);
        var noData = SampleDemNoData.ToString(CultureInfo.InvariantCulture);
        if (Available("gdal_create"))
        {
            var args = new[]
            {
                "-outsize", size, size,
                "-bands", "1",
                "-ot", "Float32",
                "-burn", elevation,
                "-a_nodata", noData,
                "-of", "GTiff",
                demPath,
            };
            await RunOrThrowAsync("gdal_create", args, scratch).ConfigureAwait(false);
        }
        else
        {
            // Fallback: gdal_translate over a tiny source-less VRT. Such a band reads as all
            // zeroes, so -scale rewrites that constant onto the declared elevation and -a_nodata
            // parks NoData on a value the raster never takes. Emitting the zeroes verbatim under
            // NoData=0 -- as this fallback first did -- would hand gdaldem a DEM with no valid
            // cells at all, leaving the statistics oracle downstream nothing to compute
            // (honua-server#4400).
            var vrtPath = Path.Join(scratch, "sample.vrt");
            File.WriteAllText(vrtPath, $$"""
                <VRTDataset rasterXSize="{{size}}" rasterYSize="{{size}}">
                  <VRTRasterBand dataType="Float32" band="1">
                    <ColorInterp>Gray</ColorInterp>
                  </VRTRasterBand>
                </VRTDataset>
                """);
            var args = new[]
            {
                "-of", "GTiff",
                "-ot", "Float32",
                "-scale", "0", "1", elevation, elevation,
                "-a_nodata", noData,
                vrtPath,
                demPath,
            };
            await RunOrThrowAsync("gdal_translate", args, scratch).ConfigureAwait(false);
        }
        return File.ReadAllBytes(demPath);
    }

    /// <summary>
    /// Band-1 extrema together with the coverage they were computed over, so a value oracle can
    /// show the extrema describe the whole raster rather than a handful of surviving cells.
    /// </summary>
    /// <param name="Width">Raster width, in cells.</param>
    /// <param name="Height">Raster height, in cells.</param>
    /// <param name="Minimum">Smallest valid (non-NoData) cell value.</param>
    /// <param name="Maximum">Largest valid (non-NoData) cell value.</param>
    /// <param name="ValidPercent">Percentage of cells that are not NoData.</param>
    public readonly record struct BandStatistics(
        int Width,
        int Height,
        double Minimum,
        double Maximum,
        double ValidPercent);

    /// <summary>
    /// Reads band-1 statistics and raster shape from a GeoTIFF with <c>gdalinfo -json -stats</c>,
    /// so a test can assert produced cell values instead of magic bytes. Throws when
    /// <c>gdalinfo</c> cannot produce them; callers gate on <c>gdalinfo</c> through
    /// <see cref="GdalCliFactAttribute"/> so an absent tool skips the case rather than reaching
    /// here.
    /// </summary>
    public static async Task<BandStatistics> ReadBandStatisticsAsync(byte[] geoTiff, string scratch)
    {
        Directory.CreateDirectory(scratch);
        var path = Path.Join(scratch, $"stats-{Guid.NewGuid():N}.tif");
        await File.WriteAllBytesAsync(path, geoTiff).ConfigureAwait(false);

        var stdout = await RunCapturingAsync("gdalinfo", ["-json", "-stats", path], scratch).ConfigureAwait(false);
        using var document = JsonDocument.Parse(stdout);
        var size = document.RootElement.GetProperty("size");
        var band = document.RootElement.GetProperty("bands").EnumerateArray().First();

        // gdalinfo drops the default metadata domain entirely when it has nothing to put in it,
        // which is exactly what an all-NoData raster produces. Treat that as a missing oracle
        // rather than letting GetProperty throw a bare KeyNotFoundException.
        if (!band.GetProperty("metadata").TryGetProperty(string.Empty, out var metadata))
        {
            throw new InvalidOperationException(
                "gdalinfo -stats reported no band metadata; the raster has no valid cells to reconcile against.");
        }

        return new BandStatistics(
            size[0].GetInt32(),
            size[1].GetInt32(),
            ReadStatistic(metadata, "STATISTICS_MINIMUM"),
            ReadStatistic(metadata, "STATISTICS_MAXIMUM"),
            ReadStatistic(metadata, "STATISTICS_VALID_PERCENT"));
    }

    private static double ReadStatistic(JsonElement metadata, string key)
    {
        if (!metadata.TryGetProperty(key, out var raw))
        {
            throw new InvalidOperationException(
                $"gdalinfo -stats reported no {key}; the raster has no valid cells to reconcile against.");
        }

        return raw.ValueKind == JsonValueKind.Number
            ? raw.GetDouble()
            : double.Parse(raw.GetString()!, CultureInfo.InvariantCulture);
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

    /// <summary>
    /// Runs the real <c>pdal</c> CLI, throwing on a non-zero exit. Used to author genuinely
    /// compressed point-cloud inputs for the real-PDAL execution proof (honua-server#4401).
    /// </summary>
    public static Task RunPdalAsync(IReadOnlyList<string> args, string scratch)
        => RunOrThrowAsync("pdal", args, scratch);

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
                $"Failed to run {tool}: exit={result.ExitCode}; stderr={result.StandardError}");
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
    /// <param name="additionalTools">
    /// Any further CLI tools the case needs, including the auxiliary ones it only uses to build
    /// its oracle. A test that reads its assertion inputs back through <c>gdalinfo</c> has to
    /// declare it here: otherwise a host carrying the primary tool but not the auxiliary one runs
    /// the case and reports a missing package as a bogus regression, instead of skipping cleanly
    /// the way this attribute promises (honua-server#4400).
    /// </param>
    public GdalCliFactAttribute(string tool, params string[] additionalTools)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        ArgumentNullException.ThrowIfNull(additionalTools);

        var required = additionalTools.Prepend(tool).ToArray();
        if (Array.Exists(required, string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("GDAL CLI tool names must be non-empty.", nameof(additionalTools));
        }

        // HONUA_REQUIRE_GDAL_CLI still wins: the CI job that owns this coverage must fail on a
        // missing tool -- primary or auxiliary -- rather than quietly skip.
        if (GdalCli.RequireCli)
        {
            return;
        }

        var missing = Array.Find(required, candidate => !GdalCli.Available(candidate));
        if (missing is not null)
        {
            Skip = $"GDAL CLI tool '{missing}' is not available on PATH. "
                + $"Set {RequireEnvironmentVariable}=true to fail instead of skipping.";
        }
    }
}

/// <summary>
/// Marks a test that shells out to the real PDAL CLI (honua-server#4401).
/// </summary>
/// <remarks>
/// PDAL had never been executed by any test in this repository: a repo-wide grep for
/// <c>PdalCliFact</c> / <c>HONUA_REQUIRE_PDAL</c> returned nothing, no workflow installed PDAL on
/// a runner, and the only invocation anywhere was <c>pdal --version</c> inside the container
/// handoff test. <c>pcloud.translate</c>'s GA claim therefore rested entirely on argument
/// assertions against <c>FakeGdalCommandRunner</c>. This mirrors
/// <see cref="GdalCliFactAttribute"/>: it skips on a dev box without PDAL, and
/// <c>HONUA_REQUIRE_PDAL_CLI=true</c> — which the worker-image lane sets — turns a missing PDAL
/// into a failure so the coverage cannot silently disappear.
/// </remarks>
[TraitDiscoverer("Honua.Worker.Gdal.Tests.PdalCliFactDiscoverer", "Honua.Worker.Gdal.Tests")]
public sealed class PdalCliFactAttribute : FactAttribute, ITraitAttribute
{
    /// <summary>
    /// Environment variable that turns "PDAL CLI missing" from a skip into a failure.
    /// </summary>
    public const string RequireEnvironmentVariable = "HONUA_REQUIRE_PDAL_CLI";

    /// <summary>Whether the lane demands real PDAL rather than tolerating a skip.</summary>
    public static bool RequireCli => string.Equals(
        Environment.GetEnvironmentVariable(RequireEnvironmentVariable),
        "true",
        StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="PdalCliFactAttribute"/> class.
    /// </summary>
    public PdalCliFactAttribute()
    {
        if (GdalCli.Available("pdal") || RequireCli)
        {
            return;
        }

        Skip = "PDAL CLI tool 'pdal' is not available on PATH. "
            + $"Set {RequireEnvironmentVariable}=true to fail instead of skipping.";
    }
}

/// <summary>
/// Emits integration-test traits for <see cref="PdalCliFactAttribute"/>.
/// </summary>
public sealed class PdalCliFactDiscoverer : ITraitDiscoverer
{
    /// <inheritdoc />
    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
    {
        return
        [
            new KeyValuePair<string, string>("Category", "Integration"),
            new KeyValuePair<string, string>("Category", "PDAL"),
            new KeyValuePair<string, string>("Tier", Tiers.Integration)
        ];
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
