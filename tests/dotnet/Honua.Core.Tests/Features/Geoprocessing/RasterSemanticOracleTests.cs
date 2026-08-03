// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Raster;
using Honua.TestKit.RasterSemantics;

namespace Honua.Core.Tests.Features.Geoprocessing;

public sealed class RasterSemanticOracleTests
{
    [Fact]
    public void FixtureCatalog_CoversTheCrossEngineSemanticRiskSurface()
    {
        var fixtures = RasterSemanticFixtureCatalog.Load();
        var coverage = fixtures.SelectMany(fixture => fixture.Coverage).ToHashSet(StringComparer.Ordinal);

        Assert.All(new[]
        {
            "clip", "window-boundary", "crs", "grid", "pixel-center", "resampling",
            "mosaic-order", "overlap", "nodata", "map-algebra", "reclassification",
            "spectral-index", "statistics", "histogram", "zonal-statistics", "slope", "aspect",
            "hillshade", "roughness", "rugosity", "tri", "tpi",
            "multi-band", "pixel-type-promotion", "color-interpretation", "antimeridian",
            "invalid-crs", "empty-input", "cancellation", "edge-treatment",
            "partial-result-cleanup",
        }, dimension => Assert.Contains(dimension, coverage));
        Assert.Equal(fixtures.Count, fixtures.Select(fixture => fixture.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void CapabilityEvidence_LinksOnlyToMatchingCheckedInFixtures()
    {
        var fixtures = RasterSemanticFixtureCatalog.Load()
            .ToDictionary(fixture => fixture.Id, StringComparer.Ordinal);
        var registry = new RasterEngineCapabilityRegistry();

        Assert.All(registry.Processes, process => Assert.All(process.Engines, engine =>
        {
            Assert.All(engine.SemanticEvidenceFixtureIds, evidenceId =>
            {
                Assert.True(fixtures.TryGetValue(evidenceId, out var fixture));
                Assert.Contains(fixture!.ProcessId, engine.RequiredCapabilities);
                Assert.Equal(process.SemanticVersion, fixture.SemanticVersion);
                Assert.Contains(fixture.Variant, engine.VerifiedSemanticVariants);
            });

            if (engine.SemanticConformance is RasterSemanticConformanceStatus.Verified
                or RasterSemanticConformanceStatus.Restricted)
            {
                Assert.All(engine.VerifiedSemanticVariants, variant =>
                    Assert.Contains(
                        engine.SemanticEvidenceFixtureIds,
                        evidenceId => string.Equals(
                            fixtures[evidenceId].Variant,
                            variant,
                            StringComparison.Ordinal)));
            }

            if (engine.Engine == RasterEngine.Postgis && engine.IsAvailable)
            {
                Assert.NotEqual(RasterSemanticConformanceStatus.Unverified, engine.SemanticConformance);
                Assert.NotEmpty(engine.SemanticEvidenceFixtureIds);
            }
        }));
    }

    [Fact]
    public void Compare_ExactGoldenSnapshot_Matches()
    {
        var fixture = RasterSemanticFixtureCatalog.Load()
            .Single(candidate => candidate.Id == "clip.pixel-center-boundary.v1");

        var result = RasterSemanticOracle.Compare(fixture.Expected!, fixture.Expected!, fixture.Tolerance);

        Assert.True(result.IsMatch);
        Assert.Empty(result.Differences);
        Assert.Equal(0, result.OmittedDifferenceCount);
    }

    [Fact]
    public void Compare_VersionStampedObservation_MatchesFixture()
    {
        var fixture = RasterSemanticFixtureCatalog.Load()
            .Single(candidate => candidate.Id == "clip.pixel-center-boundary.v1");
        var observation = new RasterSemanticObservation
        {
            ProcessId = fixture.ProcessId,
            SemanticVersion = fixture.SemanticVersion,
            Engine = "gdalNative",
            ImplementationVersion = "honua.gdal-native.raster.clip@1.0.0",
            RuntimeVersion = "3.12.4",
            Outcome = RasterSemanticOutcome.Success,
            Snapshot = fixture.Expected,
        };

        var result = RasterSemanticOracle.Compare(fixture, observation);

        Assert.True(result.IsMatch);
    }

    [Theory]
    [InlineData(RasterSemanticOutcome.Error, "raster.invalid-crs")]
    [InlineData(RasterSemanticOutcome.Cancelled, null)]
    public void Compare_NonSuccessObservation_RejectsPartialResult(
        RasterSemanticOutcome outcome,
        string? errorCode)
    {
        var fixture = RasterSemanticFixtureCatalog.Load()
            .Single(candidate => candidate.Outcome == outcome &&
                (errorCode is null || candidate.ErrorCode == errorCode));
        var observation = new RasterSemanticObservation
        {
            ProcessId = fixture.ProcessId,
            SemanticVersion = fixture.SemanticVersion,
            Engine = "postgis",
            ImplementationVersion = $"honua.postgis.{fixture.ProcessId}@1.0.0",
            RuntimeVersion = "3.6",
            Outcome = outcome,
            ErrorCode = errorCode ?? fixture.ErrorCode,
            Snapshot = Snapshot([1]),
        };

        var result = RasterSemanticOracle.Compare(fixture, observation);

        Assert.False(result.IsMatch);
        Assert.Contains(result.Differences, difference => difference.Path == "snapshot");
    }

    [Fact]
    public void Compare_NoDataTopologyAndGridDrift_AreNotHiddenByCellTolerance()
    {
        var fixture = RasterSemanticFixtureCatalog.Load()
            .Single(candidate => candidate.Id == "resample.bilinear-nodata-edge.v1");
        var expected = fixture.Expected!;
        var actual = expected with
        {
            Grid = expected.Grid! with
            {
                Srid = 3857,
                Transform = [0, 0.5, 0, 1.50001, 0, -0.5],
            },
            Bands =
            [
                expected.Bands[0] with
                {
                    Cells = [1, 1.5, 2, 2, -9999, 3, 3, 3.5, 4],
                },
            ],
        };

        var result = RasterSemanticOracle.Compare(expected, actual, fixture.Tolerance);

        Assert.False(result.IsMatch);
        Assert.Contains(result.Differences, difference => difference.Path == "grid.srid");
        Assert.Contains(result.Differences, difference => difference.Path == "grid.transform[3]");
        Assert.Contains(result.Differences, difference => difference.Path == "bands[0].cells[4]");
    }

    [Fact]
    public void Compare_CellAndScalarTolerance_IsExplicitAndRelative()
    {
        var fixture = RasterSemanticFixtureCatalog.Load()
            .Single(candidate => candidate.Id == "statistics.nodata-population.v1");
        var expected = fixture.Expected!;
        var within = expected with
        {
            Scalars = expected.Scalars.ToDictionary(
                pair => pair.Key,
                pair => pair.Key.EndsWith("stddev", StringComparison.Ordinal)
                    ? pair.Value + 0.0000000005
                    : pair.Value,
                StringComparer.Ordinal),
        };
        var outside = within with
        {
            Scalars = within.Scalars.ToDictionary(
                pair => pair.Key,
                pair => pair.Key.EndsWith("stddev", StringComparison.Ordinal)
                    ? pair.Value + 0.001
                    : pair.Value,
                StringComparer.Ordinal),
        };

        Assert.True(RasterSemanticOracle.Compare(expected, within, fixture.Tolerance).IsMatch);
        Assert.False(RasterSemanticOracle.Compare(expected, outside, fixture.Tolerance).IsMatch);
    }

    [Fact]
    public void Compare_DiagnosticVolume_IsBounded()
    {
        var expected = Snapshot(Enumerable.Repeat<double?>(1, 400).ToArray());
        var actual = Snapshot(Enumerable.Repeat<double?>(2, 400).ToArray());

        var result = RasterSemanticOracle.Compare(expected, actual, new RasterSemanticTolerance());

        Assert.False(result.IsMatch);
        Assert.Equal(100, result.Differences.Count);
        Assert.Equal(300, result.OmittedDifferenceCount);
    }

    [Fact]
    public void Compare_NonFiniteEvidence_IsRejected()
    {
        var expected = Snapshot([1]);
        var actual = Snapshot([double.NaN]);

        var exception = Assert.Throws<ArgumentException>(() =>
            RasterSemanticOracle.Compare(expected, actual, new RasterSemanticTolerance()));

        Assert.Equal("actual", exception.ParamName);
    }

    private static RasterSemanticSnapshot Snapshot(double?[] cells) => new()
    {
        Grid = new RasterSemanticGrid
        {
            Width = cells.Length,
            Height = 1,
            Srid = 4326,
            Transform = [0, 1, 0, 1, 0, -1],
        },
        Bands =
        [
            new RasterSemanticBand
            {
                PixelType = "32BF",
                ColorInterpretation = "gray",
                NoData = -9999,
                Cells = cells,
            },
        ],
    };
}
