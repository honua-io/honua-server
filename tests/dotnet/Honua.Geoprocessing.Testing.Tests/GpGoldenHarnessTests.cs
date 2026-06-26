// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Geoprocessing.Execution;
using Honua.TestKit.Attributes;

namespace Honua.Geoprocessing.Testing.Tests;

/// <summary>
/// Proves the golden-file GP test harness ITSELF (GP Devkit P6, issue #2127): a passing
/// golden, a deliberately-mismatched golden (failure + a located, useful diff), tolerance
/// behavior (within-tolerance passes, outside fails), and update-mode regeneration — plus
/// the scalar/structural path and the run-failure classification. Everything here runs
/// fully offline over managed/stub executors: no Redis, job store, or control plane.
/// </summary>
public sealed class GpGoldenHarnessTests : IDisposable
{
    // POINT(0 0) WKB — the same payload the durable-runtime buffer tests use.
    private const string PointWkbBase64 = "AQEAAAAAAAAAAAAAAAAAAAAAAAAA";

    private readonly string _scratch =
        Path.Combine(Path.GetTempPath(), "honua-gp-golden-tests-" + Guid.NewGuid().ToString("N"));

    public GpGoldenHarnessTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_scratch))
            {
                Directory.Delete(_scratch, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort scratch cleanup.
        }
    }

    [UnitTest]
    public async Task GeometryGolden_MatchingArtifact_Passes()
    {
        var goldenPath = await GenerateBufferGoldenAsync("match", distance: "10");

        var fixture = BufferFixture("match", distance: "10", goldenPath, GoldenTolerance.Default);
        var result = await new GpProcessTestRunner(BufferExecutors()).RunAsync(fixture);

        result.Passed.Should().BeTrue(result.FormatFailure());
        result.Outcome.Should().Be(GoldenTestOutcome.Matched);
        result.Comparison!.Matched.Should().BeTrue();
    }

    [UnitTest]
    public async Task GeometryGolden_DifferentGeometry_FailsWithLocatedDiff()
    {
        // Golden recorded at distance 10; run at distance 25 → a genuinely different polygon.
        var goldenPath = await GenerateBufferGoldenAsync("mismatch", distance: "10");

        var fixture = BufferFixture("mismatch", distance: "25", goldenPath, GoldenTolerance.Default);
        var result = await new GpProcessTestRunner(BufferExecutors()).RunAsync(fixture);

        result.Passed.Should().BeFalse();
        result.Outcome.Should().Be(GoldenTestOutcome.Mismatch);

        // The diff must be USEFUL: name the failing geometry coordinate and the over-tolerance delta.
        var report = result.FormatFailure();
        report.Should().Contain("geometry coord");
        report.Should().Contain("> tol=");
        result.Comparison!.Differences.Should().NotBeEmpty();
    }

    [UnitTest]
    public async Task GeometryGolden_WithinCoordinateTolerance_Passes()
    {
        // Author a golden whose coordinates are the buffer output nudged by < tolerance.
        var goldenPath = await GenerateBufferGoldenAsync("within-tol", distance: "10");
        PerturbGoldenCoordinates(goldenPath, delta: 5e-7); // below the 1e-6 tolerance

        var fixture = BufferFixture("within-tol", distance: "10", goldenPath, GoldenTolerance.Create(1e-6, 1e-6));
        var result = await new GpProcessTestRunner(BufferExecutors()).RunAsync(fixture);

        result.Passed.Should().BeTrue(result.FormatFailure());
        result.Outcome.Should().Be(GoldenTestOutcome.Matched);
    }

    [UnitTest]
    public async Task GeometryGolden_OutsideCoordinateTolerance_Fails()
    {
        var goldenPath = await GenerateBufferGoldenAsync("outside-tol", distance: "10");
        PerturbGoldenCoordinates(goldenPath, delta: 1e-3); // well above the 1e-6 tolerance

        var fixture = BufferFixture("outside-tol", distance: "10", goldenPath, GoldenTolerance.Create(1e-6, 1e-6));
        var result = await new GpProcessTestRunner(BufferExecutors()).RunAsync(fixture);

        result.Passed.Should().BeFalse();
        result.Outcome.Should().Be(GoldenTestOutcome.Mismatch);
        result.FormatFailure().Should().Contain("geometry coord");
    }

    [UnitTest]
    public async Task UpdateMode_RegeneratesGolden_ThenAsserts()
    {
        var goldenPath = Path.Combine(_scratch, "regen.geojson");
        File.WriteAllText(goldenPath, "{\"type\":\"Feature\",\"geometry\":null,\"properties\":{}}"); // stale

        var fixture = BufferFixture("regen", distance: "10", goldenPath, GoldenTolerance.Default);
        var runner = new GpProcessTestRunner(BufferExecutors());

        // Update mode rewrites the golden and reports success...
        var updated = await runner.RunAsync(fixture, GoldenUpdateMode.Update);
        updated.Passed.Should().BeTrue();
        updated.Outcome.Should().Be(GoldenTestOutcome.Updated);
        File.ReadAllText(goldenPath).Should().Contain("Polygon");

        // ...and the freshly-written golden now asserts clean.
        var asserted = await runner.RunAsync(fixture);
        asserted.Passed.Should().BeTrue(asserted.FormatFailure());
        asserted.Outcome.Should().Be(GoldenTestOutcome.Matched);
    }

    [UnitTest]
    public async Task ScalarStructuralGolden_NumericWithinTolerance_Passes()
    {
        // A scalar/JSON artifact: numbers diverge by < tolerance → still a match.
        var goldenPath = Path.Combine(_scratch, "area.json");
        File.WriteAllText(goldenPath, "{\"area\":100.0000000,\"unit\":\"m2\"}");

        var executor = StubArtifactExecutor.Json("metric.area", "{\"area\":100.0000004,\"unit\":\"m2\"}");
        var fixture = new GoldenFixture(
            "area-scalar", "metric.area",
            new Dictionary<string, string>(StringComparer.Ordinal),
            goldenPath, GoldenComparisonMode.Auto, GoldenTolerance.Create(1e-6, 1e-6));

        var result = await new GpProcessTestRunner([executor]).RunAsync(fixture);
        result.Passed.Should().BeTrue(result.FormatFailure());
    }

    [UnitTest]
    public async Task ScalarStructuralGolden_NumericOutsideTolerance_FailsWithLocatedDiff()
    {
        var goldenPath = Path.Combine(_scratch, "area-bad.json");
        File.WriteAllText(goldenPath, "{\"area\":100.0,\"unit\":\"m2\"}");

        var executor = StubArtifactExecutor.Json("metric.area", "{\"area\":142.5,\"unit\":\"m2\"}");
        var fixture = new GoldenFixture(
            "area-scalar-bad", "metric.area",
            new Dictionary<string, string>(StringComparer.Ordinal),
            goldenPath, GoldenComparisonMode.Auto, GoldenTolerance.Create(1e-6, 1e-6));

        var result = await new GpProcessTestRunner([executor]).RunAsync(fixture);
        result.Passed.Should().BeFalse();
        result.Outcome.Should().Be(GoldenTestOutcome.Mismatch);
        result.FormatFailure().Should().Contain("$.area");
        result.FormatFailure().Should().Contain("> tol=");
    }

    [UnitTest]
    public async Task ScalarStructuralGolden_CsvText_DiffsByLine()
    {
        var goldenPath = Path.Combine(_scratch, "convert.csv");
        File.WriteAllText(goldenPath, "name\na\nb\n");

        var executor = StubArtifactExecutor.Csv("convert.csv", "name\na\nZZZ\n");
        var fixture = new GoldenFixture(
            "csv-convert", "convert.csv",
            new Dictionary<string, string>(StringComparer.Ordinal),
            goldenPath, GoldenComparisonMode.ScalarStructural, GoldenTolerance.Default);

        var result = await new GpProcessTestRunner([executor]).RunAsync(fixture);
        result.Passed.Should().BeFalse();
        result.FormatFailure().Should().Contain("line 3");
    }

    [UnitTest]
    public async Task RunFailure_IsClassified_NotAComparison()
    {
        var goldenPath = Path.Combine(_scratch, "never.json");
        var executor = StubArtifactExecutor.Failing("broken.op", "boom: bad inputs");
        var fixture = new GoldenFixture(
            "broken", "broken.op",
            new Dictionary<string, string>(StringComparer.Ordinal),
            goldenPath, GoldenComparisonMode.Auto, GoldenTolerance.Default);

        var result = await new GpProcessTestRunner([executor]).RunAsync(fixture);
        result.Passed.Should().BeFalse();
        result.Outcome.Should().Be(GoldenTestOutcome.RunFailed);
        result.FormatFailure().Should().Contain("boom: bad inputs");
    }

    [UnitTest]
    public async Task MissingGolden_IsClassified()
    {
        var goldenPath = Path.Combine(_scratch, "absent.geojson");
        var fixture = BufferFixture("absent", distance: "10", goldenPath, GoldenTolerance.Default);

        var result = await new GpProcessTestRunner(BufferExecutors()).RunAsync(fixture);
        result.Passed.Should().BeFalse();
        result.Outcome.Should().Be(GoldenTestOutcome.MissingGolden);
        result.Reason.Should().Contain("update mode");
    }

    private static IEnumerable<IProcessExecutor> BufferExecutors() => [TestExecutors.Buffer()];

    private async Task<string> GenerateBufferGoldenAsync(string id, string distance)
    {
        var goldenPath = Path.Combine(_scratch, id + ".geojson");
        var fixture = BufferFixture(id, distance, goldenPath, GoldenTolerance.Default);
        var generated = await new GpProcessTestRunner(BufferExecutors())
            .RunAsync(fixture, GoldenUpdateMode.Update);
        generated.Outcome.Should().Be(GoldenTestOutcome.Updated);
        return goldenPath;
    }

    private static GoldenFixture BufferFixture(string id, string distance, string goldenPath, GoldenTolerance tolerance)
        => new(
            id,
            GeometryBufferJobExecutor.HandledProcessId,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["wkb"] = PointWkbBase64,
                ["srid"] = "4326",
                ["distance"] = distance,
            },
            goldenPath,
            GoldenComparisonMode.Geometry,
            tolerance);

    /// <summary>
    /// Shifts every coordinate in a GeoJSON golden by <paramref name="delta"/>, so a fixture
    /// run against it diverges by exactly that amount — driving the tolerance boundary tests.
    /// </summary>
    private static void PerturbGoldenCoordinates(string goldenPath, double delta)
    {
        var text = File.ReadAllText(goldenPath);
        var perturbed = ShiftNumbersInCoordinates(text, delta);
        File.WriteAllText(goldenPath, perturbed);
    }

    private static string ShiftNumbersInCoordinates(string geoJson, double delta)
    {
        // The buffer artifact's "coordinates" is the only numeric array; nudge each number
        // by delta so both X and Y move, guaranteeing the comparator sees the shift.
        const string marker = "\"coordinates\":";
        var start = geoJson.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return geoJson;
        }

        var bracketStart = geoJson.IndexOf('[', start);
        var depth = 0;
        var end = bracketStart;
        for (var i = bracketStart; i < geoJson.Length; i++)
        {
            if (geoJson[i] == '[')
            {
                depth++;
            }
            else if (geoJson[i] == ']')
            {
                depth--;
                if (depth == 0)
                {
                    end = i;
                    break;
                }
            }
        }

        var head = geoJson[..bracketStart];
        var body = geoJson[bracketStart..(end + 1)];
        var tail = geoJson[(end + 1)..];

        var shifted = System.Text.RegularExpressions.Regex.Replace(
            body,
            @"-?\d+\.\d+",
            m => (double.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture) + delta)
                .ToString("R", System.Globalization.CultureInfo.InvariantCulture));

        return head + shifted + tail;
    }
}
