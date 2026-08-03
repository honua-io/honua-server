// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.RasterSemantics;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Worker.Gdal.Tests;

/// <summary>Executable golden evidence intended to run inside the pinned GDAL worker image.</summary>
public sealed class GdalRasterSemanticOracleTests
{
    private const string ScratchSuite = "honua-gdal-semantic-oracle";

    [GdalCliFact("gdaldem")]
    public async Task SlopeFixture_MatchesPinnedGdalGridNoDataAndHornInteriorContract()
    {
        var scratch = GdalCli.NewScratch(ScratchSuite);
        try
        {
            GdalCli.Available("gdal_translate").Should().BeTrue("the pinned worker image includes gdal_translate");
            GdalCli.Available("gdalinfo").Should().BeTrue("the pinned worker image includes gdalinfo");
            GdalCli.Available("gdalsrsinfo").Should().BeTrue("the pinned worker image includes gdalsrsinfo");
            GdalCli.Available("gdallocationinfo").Should().BeTrue("the pinned worker image includes gdallocationinfo");
            var runtimeVersion = await GdalCli.VersionAsync(scratch);
            runtimeVersion.Should().StartWith("GDAL 3.12.4");

            var fixture = RasterSemanticFixtureCatalog.Load()
                .Single(candidate => candidate.Id == "surface.slope-plane-degrees.v1");
            var executor = new GdalSurfaceJobExecutor(
                new ProcessGdalCommandRunner(
                    Microsoft.Extensions.Options.Options.Create(new GdalHardeningOptions()),
                    NullLogger<ProcessGdalCommandRunner>.Instance),
                GdalJobFactory.Options(scratch),
                NullLogger<GdalSurfaceJobExecutor>.Instance);
            var source = await GdalCli.GenerateSemanticPlaneDemAsync(scratch).ConfigureAwait(false);
            var job = GdalJobFactory.Job(
                GdalSurfaceJobExecutor.SlopeProcessId,
                ("source", Convert.ToBase64String(source)),
                ("units", "degrees"));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, CancellationToken.None);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            var payload = GdalCli.DecodeDataUri(context.Artifacts.Should().ContainSingle().Which);
            var actual = await GdalCli.InspectSmallRasterAsync(payload, scratch).ConfigureAwait(false);
            var comparison = RasterSemanticOracle.Compare(fixture, new RasterSemanticObservation
            {
                ProcessId = fixture.ProcessId,
                SemanticVersion = fixture.SemanticVersion,
                Engine = "gdalNative",
                ImplementationVersion = "honua.gdal-native.surface.slope@1.0.0",
                RuntimeVersion = runtimeVersion,
                Outcome = RasterSemanticOutcome.Success,
                Snapshot = actual,
            });
            comparison.IsMatch.Should().BeTrue(FormatDifferences(comparison));
        }
        finally
        {
            GdalCli.CleanupScratch(scratch);
        }
    }

    private static string FormatDifferences(RasterSemanticComparison comparison) => string.Join(
        Environment.NewLine,
        comparison.Differences.Select(difference => $"{difference.Path}: {difference.Message}"));
}
