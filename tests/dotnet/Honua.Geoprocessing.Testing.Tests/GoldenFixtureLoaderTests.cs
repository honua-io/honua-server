// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Geoprocessing.Execution;
using Honua.TestKit.Attributes;

namespace Honua.Geoprocessing.Testing.Tests;

/// <summary>
/// Proves manifest discovery + loading (GP Devkit P6, issue #2127): a <c>fixture.json</c>
/// manifest resolves its golden path, base64-encodes referenced input files into the
/// process inputs, and is then runnable end-to-end through the SDK — fully offline.
/// </summary>
public sealed class GoldenFixtureLoaderTests : IDisposable
{
    private const string PointWkbBase64 = "AQEAAAAAAAAAAAAAAAAAAAAAAAAA";

    private readonly string _scratch =
        Path.Join(Path.GetTempPath(), "honua-gp-loader-tests-" + Guid.NewGuid().ToString("N"));

    public GoldenFixtureLoaderTests() => Directory.CreateDirectory(_scratch);

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
            // Best-effort cleanup: a lingering file handle on the scratch directory must not
            // fail test teardown.
        }
    }

    [UnitTest]
    public async Task Load_ManifestWithInlineInputs_RunsThroughSdk()
    {
        var dir = Path.Join(_scratch, "buffer");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Join(dir, "fixture.json"),
            $$"""
            {
              "id": "buffer-fixture",
              "process": "geometry.buffer",
              "mode": "geometry",
              "tolerance": { "coordinate": 1e-6, "numeric": 1e-6 },
              "inputs": { "wkb": "{{PointWkbBase64}}", "srid": "4326", "distance": "10" },
              "golden": "golden.geojson"
            }
            """);

        var fixture = GoldenFixtureLoader.Load(Path.Join(dir, "fixture.json"));
        fixture.Id.Should().Be("buffer-fixture");
        fixture.ProcessId.Should().Be(GeometryBufferJobExecutor.HandledProcessId);
        fixture.Mode.Should().Be(GoldenComparisonMode.Geometry);
        fixture.Inputs.Should().ContainKey("distance").WhoseValue.Should().Be("10");

        // Generate then assert the golden, proving the loaded fixture is fully runnable.
        var runner = new GpProcessTestRunner([TestExecutors.Buffer()]);
        (await runner.RunAsync(fixture, GoldenUpdateMode.Update)).Outcome.Should().Be(GoldenTestOutcome.Updated);
        (await runner.RunAsync(fixture)).Passed.Should().BeTrue();
    }

    [UnitTest]
    public void Load_ManifestWithInputFile_Base64EncodesContents()
    {
        var dir = Path.Join(_scratch, "convert");
        Directory.CreateDirectory(dir);
        var sourceText = "{\"type\":\"FeatureCollection\",\"features\":[]}";
        File.WriteAllText(Path.Join(dir, "input.geojson"), sourceText);
        File.WriteAllText(Path.Join(dir, "fixture.json"),
            """
            {
              "id": "convert-fixture",
              "process": "gdal.ogr2ogr",
              "mode": "scalarStructural",
              "inputFiles": { "source": "input.geojson" },
              "inputs": { "sourceFormat": "GeoJSON", "targetFormat": "CSV" },
              "golden": "golden.csv"
            }
            """);

        var fixture = GoldenFixtureLoader.Load(Path.Join(dir, "fixture.json"));
        fixture.Inputs.Should().ContainKey("source");
        Encoding.UTF8.GetString(Convert.FromBase64String(fixture.Inputs["source"])).Should().Be(sourceText);
        fixture.GoldenPath.Should().EndWith("golden.csv");
    }

    [UnitTest]
    public void Discover_FindsEveryManifestUnderRoot_OrderedById()
    {
        WriteManifest("z-second", "geometry.buffer");
        WriteManifest("a-first", "geometry.buffer");

        var fixtures = GoldenFixtureLoader.Discover(_scratch);
        fixtures.Select(f => f.Id).Should().ContainInOrder("a-first", "z-second");
    }

    [UnitTest]
    public void ResolveUpdateMode_DefaultsToAssert_WhenEnvUnset()
    {
        // The self-test process does not set HONUA_GP_UPDATE_GOLDENS, so the assertion helper
        // must never silently regenerate a golden.
        Environment.GetEnvironmentVariable(GpGoldenAssert.UpdateEnvironmentVariable).Should().BeNullOrEmpty();
        GpGoldenAssert.UpdateModeEnabled.Should().BeFalse();
        GpGoldenAssert.ResolveUpdateMode().Should().Be(GoldenUpdateMode.Assert);
    }

    private void WriteManifest(string id, string process)
    {
        var dir = Path.Join(_scratch, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Join(dir, "fixture.json"),
            $$"""
            { "id": "{{id}}", "process": "{{process}}", "inputs": {}, "golden": "golden.json" }
            """);
    }
}
