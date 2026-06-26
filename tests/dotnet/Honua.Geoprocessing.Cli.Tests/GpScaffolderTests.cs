// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Geoprocessing.Cli;
using Honua.Geoprocessing.Testing;
using Xunit;

namespace Honua.Geoprocessing.Cli.Tests;

/// <summary>
/// Offline unit tests for the <c>honua gp new</c> scaffolder (GP Devkit P4, issue #2125):
/// id validation + naming derivation, collision detection against the registered set, and
/// the rendered executor/fixture templates. No I/O, no Redis, no control plane.
/// </summary>
public sealed class GpScaffolderTests
{
    [Theory]
    [InlineData("geometry.buffer")]
    [InlineData("analytics.spatial-join")]
    [InlineData("raster.zonal-statistics")]
    [InlineData("a")]
    [InlineData("a.b.c")]
    [InlineData("surface.rugosity-tri")]
    public void TryValidateProcessId_AcceptsCanonicalIds(string id)
    {
        GpScaffolder.TryValidateProcessId(id, out var error).Should().BeTrue(error);
        error.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Geometry.Buffer")]      // uppercase
    [InlineData("geometry.Buffer")]      // uppercase segment
    [InlineData("geometry.")]            // trailing dot -> empty segment
    [InlineData(".buffer")]              // leading dot
    [InlineData("geometry..buffer")]     // double dot
    [InlineData("1geometry.buffer")]     // starts with digit
    [InlineData("geometry.-buffer")]     // leading hyphen
    [InlineData("geometry.buffer-")]     // trailing hyphen
    [InlineData("geometry.buf--fer")]    // double hyphen
    [InlineData("geometry.buffer!")]     // illegal char
    [InlineData("geometry buffer")]      // space
    public void TryValidateProcessId_RejectsInvalidIds(string id)
    {
        GpScaffolder.TryValidateProcessId(id, out var error).Should().BeFalse();
        error.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("geometry.buffer", "GeometryBuffer")]
    [InlineData("analytics.spatial-join", "AnalyticsSpatialJoin")]
    [InlineData("raster.zonal-statistics", "RasterZonalStatistics")]
    [InlineData("a.b.c", "ABC")]
    public void ToTypeStem_DerivesPascalCase(string id, string expected) =>
        GpScaffolder.ToTypeStem(id).Should().Be(expected);

    [Theory]
    [InlineData("geometry.buffer", "geometry-buffer")]
    [InlineData("analytics.spatial-join", "analytics-spatial-join")]
    public void ToFixtureId_ReplacesDotsWithHyphens(string id, string expected) =>
        GpScaffolder.ToFixtureId(id).Should().Be(expected);

    [Fact]
    public void Plan_RejectsCollisionWithExistingProcessId()
    {
        var existing = new[] { "geometry.buffer", "gdal.ogr2ogr" };

        var act = () => GpScaffolder.Plan("geometry.buffer", GpProcessKind.Geometry, existing);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*already registered*");
    }

    [Fact]
    public void Plan_RejectsInvalidProcessId()
    {
        var act = () => GpScaffolder.Plan("Bad.Id", GpProcessKind.Geometry, []);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Plan_Geometry_RendersRegisteredRunnableTestableFiles()
    {
        var plan = GpScaffolder.Plan("geometry.recenter", GpProcessKind.Geometry, []);

        plan.ProcessId.Should().Be("geometry.recenter");
        plan.Kind.Should().Be(GpProcessKind.Geometry);
        plan.FixtureId.Should().Be("geometry-recenter");

        // Executor at the managed execution directory, named *JobExecutor.
        var executor = plan.Files.Single(f => f.RelativePath.EndsWith("GeometryRecenterJobExecutor.cs", StringComparison.Ordinal));
        executor.RelativePath.Should().Be(
            "src/Honua.Geoprocessing/Features/Geoprocessing/Execution/GeometryRecenterJobExecutor.cs");

        // It declares the id + schema and returns a trivial valid result (a geo+json artifact).
        executor.Contents.Should().Contain("HandledProcessId = \"geometry.recenter\"");
        executor.Contents.Should().Contain(": IProcessExecutor");
        executor.Contents.Should().Contain("public IReadOnlySet<string> ProcessIds");
        executor.Contents.Should().Contain("ExecutionJobKind.Geoprocessing");
        executor.Contents.Should().Contain("data:application/geo+json;base64,");
        executor.Contents.Should().Contain("TODO");
        executor.Contents.Should().Contain("JobExecutionResult.Succeeded()");
        // Managed kind does NOT declare a native runtime profile.
        executor.Contents.Should().NotContain("AcceptedRuntimeProfiles");

        // Fixture + golden + note land under samples/gp/<id>/.
        plan.Files.Should().Contain(f => f.RelativePath == "samples/gp/geometry-recenter/fixture.json");
        plan.Files.Should().Contain(f => f.RelativePath == "samples/gp/geometry-recenter/golden.geojson");
        plan.Files.Should().Contain(f => f.RelativePath == "samples/gp/geometry-recenter/README.md");

        // Next-steps walk edit -> run -> test -> plan.
        plan.NextSteps.Should().Contain("honua gp run geometry.recenter");
        plan.NextSteps.Should().Contain("honua gp test geometry-recenter");
        plan.NextSteps.Should().Contain("honua gp plan geometry.recenter");
    }

    [Fact]
    public void Plan_Gdal_RendersNativeExecutorAndScalarFixture()
    {
        var plan = GpScaffolder.Plan("gdal.warp-clip", GpProcessKind.Gdal, []);

        var executor = plan.Files.Single(f => f.RelativePath.EndsWith("NativeJobExecutor.cs", StringComparison.Ordinal));
        executor.RelativePath.Should().Be("src/Honua.Worker.Gdal/Execution/GdalWarpClipNativeJobExecutor.cs");
        executor.Contents.Should().Contain("HandledProcessId = \"gdal.warp-clip\"");
        // Native kind declares the native runtime profile so the claim fence routes it.
        executor.Contents.Should().Contain("AcceptedRuntimeProfiles");
        executor.Contents.Should().Contain("RuntimeProfiles.Native");
        executor.Contents.Should().Contain("data:application/json;base64,");

        // GDAL golden is scalar JSON, asserted structurally.
        plan.Files.Should().Contain(f => f.RelativePath == "samples/gp/gdal-warp-clip/golden.json");
        var manifest = plan.Files.Single(f => f.RelativePath.EndsWith("fixture.json", StringComparison.Ordinal));
        manifest.Contents.Should().Contain("\"mode\": \"scalarStructural\"");
    }

    [Fact]
    public void GeneratedFixture_MatchesP6LoaderShape()
    {
        // The generated fixture.json + golden must load through the real P6 loader so
        // `gp test <id>` works out of the box. Write the rendered files to a temp dir
        // (the only filesystem touch in this suite) and load them with GoldenFixtureLoader.
        var plan = GpScaffolder.Plan("geometry.recenter", GpProcessKind.Geometry, []);
        var fixtureDir = Path.Combine(Path.GetTempPath(), "gp-scaffold-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureDir);
        try
        {
            foreach (var file in plan.Files.Where(f => f.RelativePath.StartsWith("samples/gp/", StringComparison.Ordinal)))
            {
                var name = Path.GetFileName(file.RelativePath);
                File.WriteAllText(Path.Combine(fixtureDir, name), file.Contents);
            }

            var manifestPath = Path.Combine(fixtureDir, GoldenFixtureLoader.ManifestFileName);
            var fixture = GoldenFixtureLoader.Load(manifestPath);

            fixture.Id.Should().Be("geometry-recenter");
            fixture.ProcessId.Should().Be("geometry.recenter");
            fixture.Mode.Should().Be(GoldenComparisonMode.Geometry);
            fixture.Inputs.Should().ContainKey("value").WhoseValue.Should().Be("hello");
            File.Exists(fixture.GoldenPath).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(fixtureDir, recursive: true);
        }
    }
}
