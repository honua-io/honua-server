// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Linq;
using System.Text;
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
    /// Gets the production worker image the GDAL image lane builds and names through
    /// <c>HONUA_WORKER_IMAGE</c>, or <see langword="null"/> when this host is not that lane.
    /// </summary>
    public static string? WorkerImage
    {
        get
        {
            var image = Environment.GetEnvironmentVariable("HONUA_WORKER_IMAGE");
            return string.IsNullOrWhiteSpace(image) ? null : image;
        }
    }

    /// <summary>
    /// Builds the command runner the real-PDAL proof executes through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PDAL cannot be provisioned on the runner with <c>apt-get install pdal</c>: no Ubuntu
    /// release publishes a <c>pdal</c> package, which is precisely why
    /// <c>docker/worker-gdal/Dockerfile</c> compiles the pinned upstream 2.10.2 source into
    /// the image. A host <c>dotnet test</c> would therefore find no <c>pdal</c> on PATH and,
    /// with <c>HONUA_REQUIRE_PDAL_CLI=true</c> refusing to skip, fail on every run.
    /// </para>
    /// <para>
    /// So when the lane names the built worker image, dispatch the tool INTO that image with
    /// the production <see cref="DockerGdalCommandRunner"/> — the same construction
    /// <c>RasterExecutionProofTests</c> uses for the raster proofs. Its identical-path bind
    /// mount (<c>-v ws:ws -w ws</c>) makes the executor's absolute workspace paths resolve to
    /// the same files inside the container, so the executor's code path is unchanged and the
    /// PDAL binary under test is the one the production worker actually ships. Falls back to
    /// the host CLI on a dev box that has PDAL installed.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// Runs the real <c>pdal</c> CLI, throwing on a non-zero exit. Used to author genuinely
    /// compressed point-cloud inputs for the real-PDAL execution proof (honua-server#4401).
    /// </summary>
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
