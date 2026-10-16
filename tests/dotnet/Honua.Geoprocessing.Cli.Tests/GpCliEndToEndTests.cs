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
[global::Xunit.Trait("Tier", "Integration")]
public sealed class GpCliEndToEndTests
{
    internal const string CollectionName = "GpCli console capture";

    // The geometry.buffer inputs mirror samples/gp/geometry-buffer-point/fixture.json:
    // buffer POINT(0 0) by 10 units in EPSG:4326. WKB is the base64 of a 2D point at the origin.
    private const string PointWkbBase64 = "AQEAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public async Task RunAsync_ListJson_DistinguishesRegistrationFromDependencyReadiness()
    {
        var (exit, stdout, _) = await InvokeAsync("list", "--json");
        exit.Should().Be(0);
        using var document = JsonDocument.Parse(stdout.Trim());
        var processes = document.RootElement.EnumerateArray().ToArray();
        processes.Should().Contain(process => process.GetProperty("id").GetString() == "geometry.buffer");
        processes.Should().OnlyContain(process => !process.GetProperty("dependenciesVerified").GetBoolean());
        processes.First(process => process.GetProperty("id").GetString() == "geometry.buffer")
            .GetProperty("runtimeProfile").GetString().Should().Be("managed");
    }

    [Fact]
    public async Task RunAsync_ListJson_ReportsRunnabilityPerProcess()
    {
        var (exit, stdout, _) = await InvokeAsync("list", "--json");
        exit.Should().Be(0);
        using var document = JsonDocument.Parse(stdout.Trim());
        var buffer = document.RootElement.EnumerateArray().First(p => p.GetProperty("id").GetString() == "geometry.buffer");
        buffer.GetProperty("executionMode").GetString().Should().Be("managed");
        buffer.GetProperty("runnableHere").GetBoolean().Should().BeTrue();
        buffer.GetProperty("blockedBy").ValueKind.Should().Be(JsonValueKind.Null);
        buffer.GetProperty("prerequisites").EnumerateArray().Should().OnlyContain(p => p.GetProperty("status").GetString() == "satisfied");
        buffer.GetProperty("supportedTargets").EnumerateArray().Select(t => t.GetString()).Should().Contain("local");
        buffer.GetProperty("parameterExamples").GetProperty("distance").GetString().Should().NotBeNullOrEmpty();
        document.RootElement.EnumerateArray().All(p => p.TryGetProperty("executionMode", out var mode) && mode.ValueKind == JsonValueKind.String && p.TryGetProperty("runnableHere", out var runnable) && runnable.ValueKind is JsonValueKind.True or JsonValueKind.False).Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_DescribeJson_IsStableAndCarriesExamples()
    {
        var (exit, stdout, _) = await InvokeAsync("describe", "geometry.buffer", "--json");
        exit.Should().Be(0);
        using var document = JsonDocument.Parse(stdout.Trim());
        var names = document.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        names.Should().ContainInOrder("id", "title", "runtimeProfile", "inputs", "outputs", "example", "executionMode", "prerequisites", "runnableHere", "blockedBy", "supportedTargets", "parameterExamples");
        var (exit2, stdout2, _) = await InvokeAsync("describe", "geometry.buffer", "--json");
        exit2.Should().Be(0);
        stdout2.Should().Be(stdout, "the machine view is deterministic");
        var (exitText, text, _) = await InvokeAsync("describe", "geometry.buffer");
        exitText.Should().Be(0);
        text.Should().Contain("mode        : managed").And.Contain("runnable    : yes").And.Contain("example:");
    }

    [Fact]
    public async Task RunAsync_Init_WritesTheTemplateVerbatimPlusDescriptor()
    {
        var output = Path.Join(Path.GetTempPath(), $"gp-init-{Guid.NewGuid():N}");
        try
        {
            var (exit, stdout, _) = await InvokeAsync("init", "--language", "python", "--name", "parcel-summary", "--output", output);
            exit.Should().Be(0);
            stdout.Should().Contain("created : ");
            var project = Path.Join(output, "parcel-summary");
            foreach (var file in new[] { "process/summary_tool.py", "schema/inputs.schema.json", "fixtures/params.json", "fixtures/input.geojson", "expected/golden.json", "expected/summary.json", "tests/test_golden.py", "requirements.lock", ".vscode/launch.json", ".vscode/tasks.json", "docker/compose.debug.yaml", "README.md", "TEMPLATE-MANIFEST.json", "honua-gp.yaml" })
            {
                File.Exists(Path.Join(project, file)).Should().BeTrue(file);
            }

            var manifest = GpProjectManifest.Load(Path.Join(project, "honua-gp.yaml"));
            manifest.Name.Should().Be("parcel-summary");
            manifest.Language.Should().Be(GpProjectLanguage.Python);
            manifest.Entrypoint.Should().Be("summary_tool:execute");
            manifest.FixtureInputs.Should().ContainKey("features");

            var (again, _, stderr) = await InvokeAsync("init", "--language", "python", "--name", "parcel-summary", "--output", output);
            again.Should().Be(2);
            stderr.Should().Contain("never overwrites");
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunAsync_Init_Dotnet_WritesResponseFileWhenHarnessIsKnown()
    {
        var output = Path.Join(Path.GetTempPath(), $"gp-init-{Guid.NewGuid():N}");
        var harness = GpCustomProjectScaffolder.ResolveHarnessRoot(null, Directory.GetCurrentDirectory());
        try
        {
            var (exit, _, _) = await InvokeAsync("init", "--language", "dotnet", "--name", "parcel-summary", "--output", output);
            exit.Should().Be(0);
            var project = Path.Join(output, "parcel-summary");
            File.Exists(Path.Join(project, "process", "SummaryTool.csproj")).Should().BeTrue();
            File.Exists(Path.Join(project, "process", "packages.lock.json")).Should().BeTrue();
            File.Exists(Path.Join(project, "tests", "SummaryTool.Tests", "GoldenTests.cs")).Should().BeTrue();
            var manifest = GpProjectManifest.Load(Path.Join(project, "honua-gp.yaml"));
            manifest.Build.Should().Be("tests/SummaryTool.Tests/SummaryTool.Tests.csproj");
            if (harness is not null)
            {
                File.ReadAllText(Path.Join(project, "Directory.Build.rsp")).Should().Contain("-p:HonuaServerCheckout=");
                manifest.HarnessRoot.Should().NotBeNullOrEmpty();
            }
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunAsync_TestProject_Python_RunsTheHarnessAndComparesExpected()
    {
        if (!GpCustomProjectTests.PythonAvailable())
        {
            return; // python3 is not on PATH; the project test is exercised where it is (CI Linux).
        }

        var harness = GpCustomProjectScaffolder.ResolveHarnessRoot(null, Directory.GetCurrentDirectory());
        harness.Should().NotBeNull("the tests run inside the checkout, which holds the harnesses");
        var output = Path.Join(Path.GetTempPath(), $"gp-test-{Guid.NewGuid():N}");
        try
        {
            (await InvokeAsync("init", "--language", "python", "--name", "demo-summary", "--output", output)).ExitCode.Should().Be(0);
            var project = Path.Join(output, "demo-summary");

            var (exit, stdout, stderr) = await InvokeAsync("test", "--project", project);
            exit.Should().Be(0, stdout + stderr);
            stdout.Should().Contain("PASS  summary.json").And.Contain("PASS  golden.json").And.Contain("receipt : ").And.Contain("summary : passed");

            // Break the expectation: the first differing value and the receipt path are named.
            File.WriteAllText(Path.Join(project, "expected", "summary.json"), "{\"count\": 3, \"field\": \"value\", \"mean\": 21.0, \"sum\": 60.0}\n");
            var (failExit, failOut, _) = await InvokeAsync("test", "--project", project);
            failExit.Should().Be(1);
            failOut.Should().Contain("FAIL  summary.json: $.mean: expected 21.0, produced 20.0").And.Contain("receipt : ");

            // --update rewrites the expectation and the golden digest, after which the test passes again.
            (await InvokeAsync("test", "--project", project, "--update")).ExitCode.Should().Be(0);
            (await InvokeAsync("test", "--project", project)).ExitCode.Should().Be(0);
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunAsync_TestProject_Dotnet_RunsWhenOptedIn()
    {
        if (Environment.GetEnvironmentVariable("HONUA_GP_DEVKIT_DOTNET_E2E") != "1")
        {
            return; // builds the process, the harness and the SDK; opt in with HONUA_GP_DEVKIT_DOTNET_E2E=1.
        }

        var output = Path.Join(Path.GetTempPath(), $"gp-test-{Guid.NewGuid():N}");
        try
        {
            (await InvokeAsync("init", "--language", "dotnet", "--name", "demo-summary", "--output", output)).ExitCode.Should().Be(0);
            var (exit, stdout, stderr) = await InvokeAsync("test", "--project", Path.Join(output, "demo-summary"));
            exit.Should().Be(0, stdout + stderr);
            stdout.Should().Contain("PASS  summary.json").And.Contain("PASS  golden.json");
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunAsync_DoctorManagedJson_ExecutesManagedProbeWithoutDocker()
    {
        var (exit, stdout, _) = await InvokeAsync("doctor", "--managed-only", "--json");

        exit.Should().Be(0);
        using var document = JsonDocument.Parse(ExtractJson(stdout));
        var root = document.RootElement;
        root.GetProperty("managed").GetString().Should().Be("ready");
        root.GetProperty("nativeWorker").GetString().Should().Be("notChecked");
        root.GetProperty("registeredProcessCount").GetInt32().Should().BeGreaterThan(0);
        root.GetProperty("serviceBacked").GetString().Should().Be("requiresIntegrationStack");
    }

    [Fact]
    public async Task RunAsync_DoctorUnknownOption_ReturnsUsageError()
    {
        var (exit, _, stderr) = await InvokeAsync("doctor", "--unknown");
        exit.Should().Be(2);
        stderr.Should().Contain("Unknown option");
    }

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
    public async Task RunAsync_Run_WritesReceiptAndV1Events()
    {
        var receiptDir = Path.Join(Path.GetTempPath(), $"gp-cli-receipts-{Guid.NewGuid():N}");
        try
        {
            var (exit, stdout, _) = await InvokeAsync(
                "run", "geometry.buffer",
                "--in-process",
                "--param", $"wkb={PointWkbBase64}",
                "--param", "srid=4326",
                "--param", "distance=10",
                "--receipt-dir", receiptDir);

            exit.Should().Be(0);
            stdout.Should().Contain("receipt : ");
            var run = Directory.GetDirectories(receiptDir).Should().ContainSingle().Subject;
            using var receipt = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Join(run, "receipt.json")));
            receipt.RootElement.GetProperty("status").GetString().Should().Be("succeeded");
            receipt.RootElement.GetProperty("processId").GetString().Should().Be("geometry.buffer");
            receipt.RootElement.GetProperty("eventsSchema").GetString().Should().Be(Honua.Geoprocessing.CustomCode.HarnessEvents.Schema);
            var events = Honua.Geoprocessing.CustomCode.HarnessEvents.Parse(await File.ReadAllTextAsync(Path.Join(run, "events.jsonl")));
            events.Should().NotBeEmpty();
            events.Should().OnlyContain(e => e.Version == 1 && e.Attempt == "devkit");
            events[^1].Kind.Should().Be("terminal");
            events[^1].Status.Should().Be("succeeded");
            events.Should().Contain(e => e.Kind == "artifact");
        }
        finally
        {
            if (Directory.Exists(receiptDir))
            {
                Directory.Delete(receiptDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunAsync_Run_NoReceipt_WritesNothing()
    {
        var (exit, stdout, _) = await InvokeAsync(
            "run", "geometry.buffer", "--in-process", "--no-receipt",
            "--param", $"wkb={PointWkbBase64}", "--param", "srid=4326", "--param", "distance=10");

        exit.Should().Be(0);
        stdout.Should().NotContain("receipt : ");
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
