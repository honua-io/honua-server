// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Worker.Gdal.Tests;

/// <summary>
/// Fake-runner coverage for <see cref="GdalSurfaceJobExecutor"/>: argument
/// projection per surface.* id, source-bytes guardrails, and the canonical
/// data-URI artifact contract. Integration coverage with the real gdaldem CLI
/// fires only when GDAL is present on the host.
/// </summary>
public sealed class GdalSurfaceExecutorTests
{
    private const string ScratchSuite = "honua-gdal-surface-test";

    /// <summary>Width and height of the planar DEM fixtures, in cells.</summary>
    private const int GridSize = 7;

    /// <summary>Square cell size of the planar DEM fixtures, in metres.</summary>
    private const double CellSizeMetres = 10d;

    /// <summary>West edge of the planar DEM fixtures, in EPSG:32610 metres.</summary>
    private const double LowerLeftX = 500_000d;

    /// <summary>South edge of the planar DEM fixtures, in EPSG:32610 metres.</summary>
    private const double LowerLeftY = 4_000_000d;

    /// <summary>EPSG code of the planar DEM fixtures (WGS 84 / UTM zone 10N, metres).</summary>
    private const int Utm10N = 32610;

    /// <summary>
    /// Tolerance for decoded slope samples. gdaldem computes in double and stores
    /// Float32, so the round trip carries roughly 1e-5 of relative error on values
    /// of this magnitude; asserting exact float equality would be wrong.
    /// </summary>
    private const double SlopeTolerance = 1e-4;

    private static string Base64(string text) => GdalCli.Base64(text);

    [UnitTest]
    public void GdalSurfaceExecutor_DeclaresNativeRuntimeProfile()
    {
        var executor = NewExecutor(FakeGdalCommandRunner.Failing(1, "n/a"), out _);

        executor.Kind.Should().Be(ExecutionJobKind.Geoprocessing);
        executor.AcceptedRuntimeProfiles.Should().NotBeNull().And.ContainSingle()
            .Which.Should().Be(RuntimeProfiles.Native);
        executor.AcceptedRuntimeProfiles.Should().NotContain(RuntimeProfiles.Managed);
    }

    [UnitTest]
    public void GdalSurfaceExecutor_AdvertisesAllSixSurfaceIds()
    {
        GdalSurfaceJobExecutor.SupportedProcessIds.Should().BeEquivalentTo(new[]
        {
            "surface.slope",
            "surface.aspect",
            "surface.hillshade",
            "surface.rugosity-tri",
            "surface.rugosity-tpi",
            "surface.roughness",
        });
    }

    [UnitTest]
    public async Task Slope_Default_RunsGdaldemSlope_AndPublishesGeoTiffArtifact()
    {
        var output = Encoding.UTF8.GetBytes("fake-slope-tif");
        var runner = FakeGdalCommandRunner.Succeeding(output);
        var executor = NewExecutor(runner, out var scratch);

        try
        {
            var job = GdalJobFactory.Job(
                GdalSurfaceJobExecutor.SlopeProcessId,
                ("source", Base64("fake-dem")));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            context.Artifacts.Should().ContainSingle();
            context.Artifacts[0].Should().StartWith("data:image/tiff");

            var invocation = runner.Invocations.Single();
            invocation.Tool.Should().Be("gdaldem");
            invocation.Arguments.Should().StartWith(new[] { "slope" });
            invocation.Arguments.Should().NotContain("-p", because: "default slope units are degrees");
            invocation.Arguments.Should().ContainInOrder("-s", "1");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Slope_Percent_PassesDashPFlag()
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("ok"));
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalSurfaceJobExecutor.SlopeProcessId,
                ("source", Base64("fake-dem")),
                ("units", "percent"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);
            result.Status.Should().Be(ExecutionJobStatus.Succeeded);
            runner.Invocations.Single().Arguments.Should().Contain("-p");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Slope_Radians_RejectedBeforeReachingTheCli()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalSurfaceJobExecutor.SlopeProcessId,
                ("source", Base64("fake-dem")),
                ("units", "radians"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);
            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("unsupported slope units");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Hillshade_PassesAzimuthAltitudeAndScaleFactor()
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("ok"));
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            // 111120 is the canonical degrees-to-meters scale that callers
            // working with geographic DEMs supply. The catalog declares
            // zFactor as a vertical-to-horizontal scale ratio, so it must
            // reach gdaldem as -s/-scale, NOT -z (vertical exaggeration).
            var job = GdalJobFactory.Job(
                GdalSurfaceJobExecutor.HillshadeProcessId,
                ("source", Base64("fake-dem")),
                ("azimuth", "270"),
                ("altitude", "30"),
                ("zFactor", "111120"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);
            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);

            var args = runner.Invocations.Single().Arguments;
            args.Should().StartWith(new[] { "hillshade" });
            args.Should().ContainInOrder("-az", "270");
            args.Should().ContainInOrder("-alt", "30");
            args.Should().ContainInOrder("-s", "111120");
            args.Should().NotContain("-z",
                because: "the catalog declares zFactor as a unit ratio, which maps to gdaldem -s rather than -z (vertical exaggeration)");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Hillshade_AzimuthOutOfRange_RejectedBeforeReachingTheCli()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalSurfaceJobExecutor.HillshadeProcessId,
                ("source", Base64("fake-dem")),
                ("azimuth", "400"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);
            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("azimuth");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Aspect_RunsGdaldemAspect_NoExtraSwitches()
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("ok"));
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalSurfaceJobExecutor.AspectProcessId,
                ("source", Base64("fake-dem")));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);
            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);

            var args = runner.Invocations.Single().Arguments;
            args.Should().StartWith(new[] { "aspect" });
            args.Should().Contain("-q");
            args.Should().NotContain("-az");
            args.Should().NotContain("-alt");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public Task RugosityTri_RunsMatchingSubcommand()
        => AssertSubcommand(GdalSurfaceJobExecutor.RugosityTriProcessId, "TRI");

    [UnitTest]
    public Task RugosityTpi_RunsMatchingSubcommand()
        => AssertSubcommand(GdalSurfaceJobExecutor.RugosityTpiProcessId, "TPI");

    [UnitTest]
    public Task Roughness_RunsMatchingSubcommand()
        => AssertSubcommand(GdalSurfaceJobExecutor.RoughnessProcessId, "roughness");

    private async Task AssertSubcommand(string processId, string expectedSubcommand)
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("ok"));
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(processId, ("source", Base64("fake-dem")));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            runner.Invocations.Single().Arguments[0].Should().Be(expectedSubcommand);
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Rugosity_WindowRadiusOtherThanOne_RejectedBeforeReachingTheCli()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalSurfaceJobExecutor.RugosityTriProcessId,
                ("source", Base64("fake-dem")),
                ("windowRadius", "2"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);
            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("windowRadius");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task MissingSource_FailsWithClearMessage()
    {
        var executor = NewExecutor(FakeGdalCommandRunner.Failing(1, "n/a"), out var scratch);
        try
        {
            var job = GdalJobFactory.Job(GdalSurfaceJobExecutor.SlopeProcessId);

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);
            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("source");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task UnknownProcessId_FailsWithRoutingError()
    {
        var executor = NewExecutor(FakeGdalCommandRunner.Failing(1, "n/a"), out var scratch);
        try
        {
            var job = GdalJobFactory.Job("not.surface", ("source", Base64("x")));
            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);
            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("not handled");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [GdalCliFact("gdaldem")]
    [Protocol(ProtocolNames.TestQuality)]
    [Operation(Operations.TestInfrastructure)]
    public async Task Slope_WithRealGdaldem_ProducesGeoTiff_AndReconcilesAgainstSource()
    {
        var scratch = NewScratch();
        var executor = NewRealGdalExecutor(scratch);

        try
        {
            var demBytes = await GdalCli.GenerateSampleDemAsync(scratch).ConfigureAwait(false);
            var job = GdalJobFactory.Job(
                GdalSurfaceJobExecutor.SlopeProcessId,
                ("source", Convert.ToBase64String(demBytes)));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);
            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            context.Artifacts.Should().ContainSingle();
            var artifact = context.Artifacts[0];
            artifact.Should().StartWith("data:image/tiff");
            // GeoTIFF magic bytes: II*\0 (little-endian) or MM\0* (big-endian).
            var payload = GdalCli.DecodeDataUri(artifact);
            payload.Should().HaveCountGreaterThan(4);
            (payload[0] == 0x49 && payload[1] == 0x49 || payload[0] == 0x4D && payload[1] == 0x4D)
                .Should().BeTrue("output must be a real GeoTIFF");

            // The signature check above is not correctness evidence on its own, so
            // decode the raster: the sample DEM is a constant-elevation 16×16
            // surface, whose slope is identically zero on every interior cell.
            var decoded = await GdalRasterOracle
                .DecodeAsync(scratch, "flat-slope", payload)
                .ConfigureAwait(false);

            decoded.Width.Should().Be(16);
            decoded.Height.Should().Be(16);
            decoded.InteriorValues().Should().AllSatisfy(value =>
                value.Should().BeApproximately(0d, SlopeTolerance,
                    "a constant-elevation DEM has zero slope everywhere"));
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    /// <summary>
    /// Whole-catalog GP execution receipt for <c>surface.slope</c> in its default
    /// (degrees) mode — #3922.
    ///
    /// Drives the production <see cref="GdalSurfaceJobExecutor"/> over the real
    /// <c>gdaldem</c> binary with a georeferenced planar DEM of known rise/run, then
    /// DECODES the emitted GeoTIFF and asserts the numeric surface, the preserved
    /// grid and CRS, and the explicit nodata edge. A GeoTIFF signature, a non-empty
    /// artifact, or a CLI-flag assertion against a fake runner is listed as
    /// insufficient evidence by certification/gp-operation-matrix.v1.json.
    /// </summary>
    [GdalCliFact("gdaldem")]
    [Protocol(ProtocolNames.TestQuality)]
    [Operation(Operations.TestInfrastructure)]
    public async Task Slope_Degrees_WithRealGdaldem_MatchesPlanarOracle_AndPreservesGrid()
    {
        var scratch = NewScratch();
        var executor = NewRealGdalExecutor(scratch);

        try
        {
            // 7×7 UTM 10N grid, 10 m cells, rising 5 m per cell to the east and flat
            // north-south: dz/dx = 0.5, dz/dy = 0, so Horn's operator — the kernel
            // gdaldem uses — reproduces the plane exactly and every interior cell must
            // read atan(0.5) = 26.565051° regardless of GDAL build.
            var demBytes = await GdalRasterOracle.WritePlanarDemAsync(
                scratch,
                "planar-dem-degrees",
                columns: GridSize,
                rows: GridSize,
                cellSize: CellSizeMetres,
                lowerLeftX: LowerLeftX,
                lowerLeftY: LowerLeftY,
                baseElevation: 100d,
                risePerColumnEast: 5d,
                risePerRowNorth: 0d,
                epsg: Utm10N).ConfigureAwait(false);

            var job = GdalJobFactory.Job(
                GdalSurfaceJobExecutor.SlopeProcessId,
                ("source", Convert.ToBase64String(demBytes)));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            context.Artifacts.Should().ContainSingle();
            context.Artifacts[0].Should().StartWith("data:image/tiff");

            var decoded = await GdalRasterOracle
                .DecodeAsync(scratch, "planar-slope-degrees", GdalCli.DecodeDataUri(context.Artifacts[0]))
                .ConfigureAwait(false);

            // Grid preservation: same shape, same georeferencing, same CRS, float band.
            decoded.Width.Should().Be(GridSize);
            decoded.Height.Should().Be(GridSize);
            decoded.DataType.Should().Be("Float32");
            decoded.OriginX.Should().BeApproximately(LowerLeftX, 1e-6);
            decoded.OriginY.Should().BeApproximately(LowerLeftY + (GridSize * CellSizeMetres), 1e-6);
            decoded.PixelWidth.Should().BeApproximately(CellSizeMetres, 1e-9);
            decoded.PixelHeight.Should().BeApproximately(-CellSizeMetres, 1e-9);
            decoded.GeoTransform[2].Should().Be(0d, "the output grid stays axis-aligned");
            decoded.GeoTransform[4].Should().Be(0d, "the output grid stays axis-aligned");
            decoded.CoordinateSystemWkt.Should().Contain(
                Utm10N.ToString(CultureInfo.InvariantCulture),
                "the source CRS (EPSG:32610) must survive the operation");

            // Numeric surface: every interior cell is the plane's slope in DEGREES.
            var expectedDegrees = double.RadiansToDegrees(Math.Atan(0.5d));
            expectedDegrees.Should().BeApproximately(26.565051d, 1e-6, "sanity-check the oracle itself");

            foreach (var (column, row) in InteriorCells())
            {
                decoded.Value(column, row).Should().BeApproximately(
                    expectedDegrees, SlopeTolerance,
                    $"cell ({column},{row}) sits on a plane rising 5 m per 10 m cell eastward");
            }

            // Fails for the plausible wrong-but-well-formed output: percent rise.
            decoded.Value(3, 3).Should().NotBeApproximately(50d, 1d,
                "default slope units are degrees, not percent rise");

            // Explicit edge/nodata expectation: gdaldem leaves the one-pixel border
            // as nodata because -compute_edges is not passed.
            decoded.NoDataValue.Should().NotBeNull();
            decoded.NoDataValue!.Value.Should().BeApproximately(GdalRasterOracle.NoDataSentinel, 1e-6);
            foreach (var value in decoded.BorderValues())
            {
                value.Should().BeApproximately(
                    GdalRasterOracle.NoDataSentinel, 1e-6,
                    "the outer ring has no full 3×3 window, so gdaldem writes nodata there");
            }
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    /// <summary>
    /// Percent-mode half of the <c>surface.slope</c> execution receipt (#3922).
    /// Uses a DIFFERENT plane from the degrees case so the two expected surfaces
    /// cannot be satisfied by one constant, and asserts the percent value rather
    /// than the degree value the same grid would produce.
    /// </summary>
    [GdalCliFact("gdaldem")]
    [Protocol(ProtocolNames.TestQuality)]
    [Operation(Operations.TestInfrastructure)]
    public async Task Slope_Percent_WithRealGdaldem_MatchesPlanarOracle()
    {
        var scratch = NewScratch();
        var executor = NewRealGdalExecutor(scratch);

        try
        {
            // 6 m east + 8 m north per 10 m cell: dz/dx = 0.6, dz/dy = 0.8, so the
            // gradient magnitude is exactly 1.0 — 100% rise, or 45° in degrees mode.
            var demBytes = await GdalRasterOracle.WritePlanarDemAsync(
                scratch,
                "planar-dem-percent",
                columns: GridSize,
                rows: GridSize,
                cellSize: CellSizeMetres,
                lowerLeftX: LowerLeftX,
                lowerLeftY: LowerLeftY,
                baseElevation: 100d,
                risePerColumnEast: 6d,
                risePerRowNorth: 8d,
                epsg: Utm10N).ConfigureAwait(false);

            var job = GdalJobFactory.Job(
                GdalSurfaceJobExecutor.SlopeProcessId,
                ("source", Convert.ToBase64String(demBytes)),
                ("units", "percent"));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            context.Artifacts.Should().ContainSingle();

            var decoded = await GdalRasterOracle
                .DecodeAsync(scratch, "planar-slope-percent", GdalCli.DecodeDataUri(context.Artifacts[0]))
                .ConfigureAwait(false);

            decoded.Width.Should().Be(GridSize);
            decoded.Height.Should().Be(GridSize);
            decoded.CoordinateSystemWkt.Should().Contain(Utm10N.ToString(CultureInfo.InvariantCulture));

            foreach (var (column, row) in InteriorCells())
            {
                decoded.Value(column, row).Should().BeApproximately(
                    100d, SlopeTolerance,
                    $"cell ({column},{row}) sits on a plane whose gradient magnitude is exactly 1.0");
            }

            // Fails for the plausible wrong-but-well-formed output: degrees.
            decoded.Value(3, 3).Should().NotBeApproximately(45d, 1d,
                "units=percent must reach gdaldem as -p, not fall back to degrees");

            foreach (var value in decoded.BorderValues())
            {
                value.Should().BeApproximately(GdalRasterOracle.NoDataSentinel, 1e-6);
            }
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    private static IEnumerable<(int Column, int Row)> InteriorCells()
    {
        for (var row = 1; row < GridSize - 1; row++)
        {
            for (var column = 1; column < GridSize - 1; column++)
            {
                yield return (column, row);
            }
        }
    }

    private static GdalSurfaceJobExecutor NewRealGdalExecutor(string scratch)
        => new(
            new ProcessGdalCommandRunner(
                Microsoft.Extensions.Options.Options.Create(new GdalHardeningOptions()),
                Microsoft.Extensions.Options.Options.Create(new AwsS3Options()),
                Microsoft.Extensions.Options.Options.Create(new AzureBlobOptions()),
                NullLogger<ProcessGdalCommandRunner>.Instance),
            GdalJobFactory.Options(scratch),
            NullLogger<GdalSurfaceJobExecutor>.Instance);

    private static GdalSurfaceJobExecutor NewExecutor(IGdalCommandRunner runner, out string scratch)
    {
        scratch = NewScratch();
        return new GdalSurfaceJobExecutor(
            runner, GdalJobFactory.Options(scratch), NullLogger<GdalSurfaceJobExecutor>.Instance);
    }

    private static string NewScratch() => GdalCli.NewScratch(ScratchSuite);

    private static void CleanupScratch(string scratch) => GdalCli.CleanupScratch(scratch);
}
