// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Honua.Geoprocessing.Cli.Tests;

/// <summary>
/// End-to-end coverage for the <c>honua gp</c> command surface (<see cref="GpCli.RunAsync"/>):
/// the GP Devkit umbrella (#2130) Wave-1 exit criterion — the minimal authoring/run/test loop
/// "proven end-to-end with tests". Where the sibling unit tests cover each building block in
/// isolation (the scaffolder, the describe renderer, the publish pipeline), these drive the
/// REAL verb dispatch — argument parsing, the registered managed executor set, the Redis-free
/// <c>GeoprocessingLocalRunner</c>, the planner, the golden test runner, exit codes, and the
/// console output — through the public entry point a developer actually invokes.
///
/// Every scenario stays managed-only (the <c>geometry.buffer</c> op and the managed
/// <c>geometry-buffer-point</c> golden fixture) so the loop is deterministic and needs no
/// Redis, no job store, no Docker, and no GDAL binary on PATH.
/// </summary>
[Collection(GpCliEndToEndTests.CollectionName)]
public sealed class GpCliEndToEndTests
{
    internal const string CollectionName = "GpCli console capture";

    // The geometry.buffer inputs mirror samples/gp/geometry-buffer-point/fixture.json:
    // buffer POINT(0 0) by 10 units in EPSG:4326. WKB is the base64 of a 2D point at the origin.
    private const string PointWkbBase64 = "AQEAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public async Task RunAsync_NoArgs_PrintsUsageAndReturnsUsageExit()
    {
        var (exit, stdout, _) = await InvokeAsync();

        exit.Should().Be(2);
        stdout.Should().Contain("honua gp");
        stdout.Should().Contain("Usage:");
    }

    [Fact]
    public async Task RunAsync_Help_PrintsUsageAndSucceeds()
    {
        var (exit, stdout, _) = await InvokeAsync("--help");

        exit.Should().Be(0);
        stdout.Should().Contain("Usage:");
        stdout.Should().Contain("honua gp list");
    }

    [Fact]
    public async Task RunAsync_UnknownVerb_ReturnsUsageExit()
    {
        var (exit, _, stderr) = await InvokeAsync("frobnicate");

        exit.Should().Be(2);
        stderr.Should().Contain("Unknown command 'frobnicate'.");
    }

    [Fact]
    public async Task RunAsync_List_IncludesRegisteredManagedProcesses()
    {
        var (exit, stdout, _) = await InvokeAsync("list");

        exit.Should().Be(0);
        stdout.Should().Contain("Available geoprocessing processes:");
        // geometry.buffer is part of the canonical managed executor set every host registers.
        stdout.Should().Contain("geometry.buffer");
    }

    [Fact]
    public async Task RunAsync_List_ResolvesManagedAndNativeProcessIdCollision()
    {
        // transform.reproject is claimed by BOTH the managed NTS executor and the native
        // GDAL ogr2ogr executor (registered in the same provider only by the devkit). The
        // managed one must win, while the native executor's DISTINCT gdal.* ids survive, so
        // building the runner must not throw a duplicate-id error.
        var (exit, stdout, _) = await InvokeAsync("list");

        exit.Should().Be(0);
        stdout.Should().Contain("transform.reproject");
        stdout.Should().Contain("gdal.gdalwarp");
    }

    [Fact]
    public async Task RunAsync_Describe_PrintsTypedParametersForKnownProcess()
    {
        var (exit, stdout, _) = await InvokeAsync("describe", "geometry.buffer");

        exit.Should().Be(0);
        stdout.Should().Contain("geometry.buffer");
        // The buffer op's typed schema exposes a distance parameter.
        stdout.Should().Contain("distance");
    }

    [Fact]
    public async Task RunAsync_DescribeJson_EmitsParseableDescriptor()
    {
        var (exit, stdout, _) = await InvokeAsync("describe", "geometry.buffer", "--json");

        exit.Should().Be(0);

        // The --json view must be a single machine-parseable object, not free text.
        var json = ExtractJson(stdout);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public async Task RunAsync_Describe_UnknownProcess_ReturnsUsageExit()
    {
        var (exit, _, stderr) = await InvokeAsync("describe", "geometry.does-not-exist");

        exit.Should().Be(2);
        stderr.Should().Contain("is not registered");
    }

    [Fact]
    public async Task RunAsync_Plan_ValidParams_SucceedsAndEstimatesSizeCost()
    {
        var (exit, stdout, _) = await InvokeAsync(
            "plan", "geometry.buffer",
            "--param", $"wkb={PointWkbBase64}",
            "--param", "srid=4326",
            "--param", "distance=10");

        exit.Should().Be(0);
        stdout.Should().Contain("process      : geometry.buffer");
        stdout.Should().Contain("valid        : yes");
        stdout.Should().Contain("size/cost estimate");
    }

    [Fact]
    public async Task RunAsync_Plan_UnknownProcess_ReturnsUsageExit()
    {
        var (exit, _, stderr) = await InvokeAsync("plan", "geometry.nope", "--param", "x=1");

        exit.Should().Be(2);
        stderr.Should().Contain("is not registered");
    }

    [Fact]
    public async Task RunAsync_Run_ManagedBuffer_SucceedsAndEmitsArtifact()
    {
        var (exit, stdout, _) = await InvokeAsync(
            "run", "geometry.buffer",
            "--in-process",
            "--param", $"wkb={PointWkbBase64}",
            "--param", "srid=4326",
            "--param", "distance=10");

        exit.Should().Be(0);
        stdout.Should().Contain("process : geometry.buffer");
        stdout.Should().Contain("status  :");
        stdout.Should().Contain("artifact:");
    }

    [Fact]
    public async Task RunAsync_Run_WithOut_WritesArtifactBytesToFile()
    {
        var outPath = Path.Join(Path.GetTempPath(), $"gp-cli-out-{Guid.NewGuid():N}.bin");
        try
        {
            var (exit, stdout, _) = await InvokeAsync(
                "run", "geometry.buffer",
                "--in-process",
                "--param", $"wkb={PointWkbBase64}",
                "--param", "srid=4326",
                "--param", "distance=10",
                "--out", outPath);

            exit.Should().Be(0);
            stdout.Should().Contain("wrote   :");
            File.Exists(outPath).Should().BeTrue();
            new FileInfo(outPath).Length.Should().BeGreaterThan(0);
        }
        finally
        {
            if (File.Exists(outPath))
            {
                File.Delete(outPath);
            }
        }
    }

    [Fact]
    public async Task RunAsync_Run_UnknownProcess_ReturnsUsageExit()
    {
        var (exit, _, stderr) = await InvokeAsync("run", "geometry.nope", "--in-process", "--param", "x=1");

        exit.Should().Be(2);
        stderr.Should().Contain("is not registered");
    }

    [Fact]
    public async Task RunAsync_Test_ManagedGoldenFixture_Passes()
    {
        var root = ResolveSamplesGpRoot();

        var (exit, stdout, _) = await InvokeAsync(
            "test", "geometry-buffer-point", "--root", root);

        exit.Should().Be(0);
        stdout.Should().Contain("PASS");
        stdout.Should().Contain("geometry-buffer-point");
        stdout.Should().Contain("0 failed");
    }

    [Fact]
    public async Task RunAsync_Test_UnknownFixture_ReturnsUsageExit()
    {
        var root = ResolveSamplesGpRoot();

        var (exit, _, stderr) = await InvokeAsync(
            "test", "no-such-fixture", "--root", root);

        exit.Should().Be(2);
        stderr.Should().Contain("No GP fixture with id 'no-such-fixture'");
    }

    /// <summary>
    /// The headline umbrella proof: the full devkit loop — list -> describe -> plan -> run -> test —
    /// drives the SAME registered process through every verb and asserts each exits clean. This is the
    /// "minimal CLI proven end-to-end" exit criterion for GP Devkit Wave 1 (#2130).
    /// </summary>
    [Fact]
    public async Task RunAsync_FullDevkitLoop_ListDescribePlanRunTest_AllSucceed()
    {
        var root = ResolveSamplesGpRoot();
        var bufferParams = new[]
        {
            "--param", $"wkb={PointWkbBase64}",
            "--param", "srid=4326",
            "--param", "distance=10",
        };

        var list = await InvokeAsync("list");
        list.ExitCode.Should().Be(0, "the dev loop starts by discovering registered processes");

        var describe = await InvokeAsync("describe", "geometry.buffer");
        describe.ExitCode.Should().Be(0, "the author inspects the typed schema next");

        var plan = await InvokeAsync(new[] { "plan", "geometry.buffer" }.Concat(bufferParams).ToArray());
        plan.ExitCode.Should().Be(0, "the dry-run plan must validate before submit");

        var run = await InvokeAsync(new[] { "run", "geometry.buffer", "--in-process" }.Concat(bufferParams).ToArray());
        run.ExitCode.Should().Be(0, "the local run must succeed against the canonical runtime");

        var test = await InvokeAsync("test", "geometry-buffer-point", "--root", root);
        test.ExitCode.Should().Be(0, "the golden fixture must assert the output");
    }

    /// <summary>
    /// Invokes <see cref="GpCli.RunAsync"/> with the supplied argv while capturing stdout/stderr.
    /// Console redirection is process-global, so this collection disables cross-collection
    /// parallelization (and xUnit serializes the methods within this class).
    /// </summary>
    private static async Task<CliResult> InvokeAsync(params string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var exit = await GpCli.RunAsync(args);
            return new CliResult(exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    /// <summary>
    /// Locates the repo's <c>samples/gp</c> fixtures directory by walking up from the test
    /// assembly's base directory, so the <c>test</c> verb is exercised against the real fixtures
    /// regardless of the runner's working directory.
    /// </summary>
    private static string ResolveSamplesGpRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Join(dir.FullName, "samples", "gp");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the samples/gp fixtures directory above " + AppContext.BaseDirectory);
    }

    private static string ExtractJson(string output)
    {
        var start = output.IndexOf('{', StringComparison.Ordinal);
        var end = output.LastIndexOf('}');
        return start >= 0 && end > start ? output[start..(end + 1)] : output;
    }

    private readonly record struct CliResult(int ExitCode, string StdOut, string StdErr);
}

/// <summary>
/// Serializes the console-capturing GP CLI end-to-end tests against every other test
/// collection in the assembly, since redirecting <see cref="Console.Out"/> is process-global.
/// </summary>
[CollectionDefinition(GpCliEndToEndTests.CollectionName, DisableParallelization = true)]
public sealed class GpCliConsoleCaptureDefinition;
