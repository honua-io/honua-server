// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

internal static class GdalCli
{
    public static bool RequireCli => string.Equals(
        Environment.GetEnvironmentVariable(GdalCliFactAttribute.RequireEnvironmentVariable),
        "true",
        StringComparison.OrdinalIgnoreCase);

    public static bool Available(string tool)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        return pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Join(dir, tool)));
    }

    public static byte[] DecodeDataUri(string dataUri)
    {
        var comma = dataUri.IndexOf(',', StringComparison.Ordinal);
        return Convert.FromBase64String(dataUri[(comma + 1)..]);
    }

    public static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    public static string RasterBase64(string label)
        => Convert.ToBase64String([.. TiffStandInMagic, .. Encoding.UTF8.GetBytes(label)]);

    private static readonly byte[] TiffStandInMagic = [0x49, 0x49, 0x2A, 0x00, 0x00, 0x00, 0x00, 0x00];

    public static string NewScratch(string suite)
        => Path.Join(Path.GetTempPath(), suite, Guid.NewGuid().ToString("N"));

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

    public const int SampleDemSize = 16;

    public const double SampleDemConstantElevation = 100.0;

    public const double SampleDemNoData = -9999.0;

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

    public readonly record struct BandStatistics(
        int Width,
        int Height,
        double Minimum,
        double Maximum,
        double ValidPercent);

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

    public static string? WorkerImage
    {
        get
        {
            var image = Environment.GetEnvironmentVariable("HONUA_WORKER_IMAGE");
            return string.IsNullOrWhiteSpace(image) ? null : image;
        }
    }

    public static IGdalCommandRunner CreatePdalRunner()
        => WorkerImage is { } image
            ? new DockerGdalCommandRunner(
                new ProcessDockerCommandInvoker(NullLogger<ProcessDockerCommandInvoker>.Instance),
                Microsoft.Extensions.Options.Options.Create(new GdalContainerExecutionOptions
                {
                    Image = image,
                    User = Environment.GetEnvironmentVariable("HONUA_GDAL_PROOF_USER") ?? "1001:1001",
                }),
                Microsoft.Extensions.Options.Options.Create(new GdalHardeningOptions()),
                Microsoft.Extensions.Options.Options.Create(new AwsS3Options()),
                Microsoft.Extensions.Options.Options.Create(new AzureBlobOptions()),
                NullLogger<DockerGdalCommandRunner>.Instance)
            : CreateHostRunner();

    public static Task RunPdalAsync(IReadOnlyList<string> args, string scratch)
        => RunOrThrowAsync("pdal", args, scratch, CreatePdalRunner());

    private static ProcessGdalCommandRunner CreateHostRunner()
        => new(
            Microsoft.Extensions.Options.Options.Create(new GdalHardeningOptions()),
            Microsoft.Extensions.Options.Options.Create(new AwsS3Options()),
            Microsoft.Extensions.Options.Options.Create(new AzureBlobOptions()),
            NullLogger<ProcessGdalCommandRunner>.Instance);

    private static async Task RunOrThrowAsync(
        string tool,
        IReadOnlyList<string> args,
        string scratch,
        IGdalCommandRunner? runner = null)
    {
        runner ??= CreateHostRunner();
        var result = await runner.RunAsync(tool, args, scratch, CancellationToken.None).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Failed to run {tool}: exit={result.ExitCode}; stderr={result.StandardError}");
        }
    }
}

[TraitDiscoverer("Honua.Worker.Gdal.Tests.GdalCliFactDiscoverer", "Honua.Worker.Gdal.Tests")]
public sealed class GdalCliFactAttribute : FactAttribute, ITraitAttribute
{
    public const string RequireEnvironmentVariable = "HONUA_REQUIRE_GDAL_CLI";

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

[TraitDiscoverer("Honua.Worker.Gdal.Tests.PdalCliFactDiscoverer", "Honua.Worker.Gdal.Tests")]
public sealed class PdalCliFactAttribute : FactAttribute, ITraitAttribute
{
    public const string RequireEnvironmentVariable = "HONUA_REQUIRE_PDAL_CLI";

    public static bool RequireCli => string.Equals(
        Environment.GetEnvironmentVariable(RequireEnvironmentVariable),
        "true",
        StringComparison.OrdinalIgnoreCase);

    public PdalCliFactAttribute()
    {
        // The image lane has no host PDAL — no Ubuntu release packages one — so the proof
        // runs the tool inside the named worker image. Treat that image as availability.
        if (GdalCli.Available("pdal") || GdalCli.WorkerImage is not null || RequireCli)
        {
            return;
        }

        Skip = "PDAL is available neither on PATH nor through a HONUA_WORKER_IMAGE container. "
            + $"Set {RequireEnvironmentVariable}=true to fail instead of skipping.";
    }
}

public sealed class PdalCliFactDiscoverer : ITraitDiscoverer
{
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

public sealed class GdalCliFactDiscoverer : ITraitDiscoverer
{
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
