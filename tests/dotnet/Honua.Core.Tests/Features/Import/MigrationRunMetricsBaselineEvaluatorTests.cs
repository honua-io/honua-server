// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Services;

namespace Honua.Core.Tests.Features.Import;

/// <summary>
/// Tests for <see cref="MigrationRunMetricsBaselineEvaluator"/> and the seed
/// catalog supplied by <see cref="MigrationFixtureBaselineCatalog"/> (issue #1033 slice 2).
/// </summary>
public sealed class MigrationRunMetricsBaselineEvaluatorTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    [Fact]
    public void Catalog_SeedsGeoServerSmall_Only()
    {
        MigrationFixtureBaselineCatalog.SeededCount.Should().Be(1);

        MigrationFixtureBaselineCatalog
            .TryGet(MigrationCostPerformanceSourceFamilies.GeoServerRest, MigrationCostPerformanceFixtureSizes.Small, out var geoServerSmall)
            .Should().BeTrue();
        geoServerSmall.ProfileName.Should().Be("geoserver-small-v1");
        geoServerSmall.FixtureProfile.ExpectedResourceCount.Should().Be(200);
        geoServerSmall.Bands.Should().NotBeEmpty();

        MigrationFixtureBaselineCatalog
            .TryGet(MigrationCostPerformanceSourceFamilies.ArcGisGeoServicesRest, MigrationCostPerformanceFixtureSizes.Small, out _)
            .Should().BeFalse("ArcGIS seed is deferred to slice 3+");

        MigrationFixtureBaselineCatalog
            .TryGet(MigrationCostPerformanceSourceFamilies.GeoServerRest, MigrationCostPerformanceFixtureSizes.Medium, out _)
            .Should().BeFalse("GeoServer Medium seed is deferred");
    }

    [Fact]
    public void Evaluate_WithMetricsInsideAllPassBands_EmitsPassStatus()
    {
        var baseline = MigrationFixtureBaselineCatalog.GeoServerSmallBaseline();
        var run = BuildRun(new MigrationRunMetricsValues
        {
            DurationMilliseconds = 30_000,
            SourceRequestCount = 200,
            BytesRead = 32L * 1024 * 1024,
            BytesWritten = 32L * 1024 * 1024,
            RetryCount = 0,
            ResumeCount = 0,
            ManualReviewRatio = 0.0,
            FeatureThroughputPerSecond = 200
        });

        var result = MigrationRunMetricsBaselineEvaluator.Evaluate(run, baseline);

        result.Status.Should().Be(MigrationMetricBaselineStatuses.Pass);
        result.Signals.Should().OnlyContain(signal => signal.Status == MigrationMetricBaselineStatuses.Pass);
        result.SourceFamily.Should().Be(MigrationCostPerformanceSourceFamilies.GeoServerRest);
        result.Size.Should().Be(MigrationCostPerformanceFixtureSizes.Small);
        result.BaselineProfile.Should().Be("geoserver-small-v1");
        result.Summary.Should().Contain("within baseline");
    }

    [Fact]
    public void Evaluate_OnUpperBoundWarnBoundary_EmitsWarnStatus()
    {
        var baseline = MigrationFixtureBaselineCatalog.GeoServerSmallBaseline();
        // Duration warn = 60_000ms (>=). Right on the boundary should be Warn.
        var run = BuildRun(new MigrationRunMetricsValues
        {
            DurationMilliseconds = 60_000,
            FeatureThroughputPerSecond = 200
        });

        var result = MigrationRunMetricsBaselineEvaluator.Evaluate(run, baseline);

        result.Status.Should().Be(MigrationMetricBaselineStatuses.Warn);
        var duration = result.Signals.Single(s => s.Metric == "durationMilliseconds");
        duration.Status.Should().Be(MigrationMetricBaselineStatuses.Warn);
        duration.Observed.Should().Be(60_000);
    }

    [Fact]
    public void Evaluate_OnUpperBoundFailBoundary_EmitsFailStatus()
    {
        var baseline = MigrationFixtureBaselineCatalog.GeoServerSmallBaseline();
        // Duration fail = 180_000ms (>=). Right on the boundary should be Fail.
        var run = BuildRun(new MigrationRunMetricsValues
        {
            DurationMilliseconds = 180_000,
            FeatureThroughputPerSecond = 200
        });

        var result = MigrationRunMetricsBaselineEvaluator.Evaluate(run, baseline);

        result.Status.Should().Be(MigrationMetricBaselineStatuses.Fail);
        result.Signals.Single(s => s.Metric == "durationMilliseconds").Status
            .Should().Be(MigrationMetricBaselineStatuses.Fail);
    }

    [Fact]
    public void Evaluate_OnUpperBoundJustBelowWarn_EmitsPassStatus()
    {
        var baseline = MigrationFixtureBaselineCatalog.GeoServerSmallBaseline();
        // Just below 60_000 warn boundary.
        var run = BuildRun(new MigrationRunMetricsValues
        {
            DurationMilliseconds = 59_999,
            FeatureThroughputPerSecond = 200
        });

        var result = MigrationRunMetricsBaselineEvaluator.Evaluate(run, baseline);

        result.Signals.Single(s => s.Metric == "durationMilliseconds").Status
            .Should().Be(MigrationMetricBaselineStatuses.Pass);
    }

    [Fact]
    public void Evaluate_OnLowerBoundWarnBoundary_EmitsWarnStatus()
    {
        var baseline = MigrationFixtureBaselineCatalog.GeoServerSmallBaseline();
        // featureThroughput warn floor = 50 features/sec. Exactly 50 must Warn.
        var run = BuildRun(new MigrationRunMetricsValues
        {
            DurationMilliseconds = 30_000,
            FeatureThroughputPerSecond = 50
        });

        var result = MigrationRunMetricsBaselineEvaluator.Evaluate(run, baseline);

        result.Status.Should().Be(MigrationMetricBaselineStatuses.Warn);
        result.Signals.Single(s => s.Metric == "featureThroughputPerSecond").Status
            .Should().Be(MigrationMetricBaselineStatuses.Warn);
    }

    [Fact]
    public void Evaluate_OnLowerBoundFailBoundary_EmitsFailStatus()
    {
        var baseline = MigrationFixtureBaselineCatalog.GeoServerSmallBaseline();
        // featureThroughput fail floor = 10 features/sec. Exactly 10 must Fail.
        var run = BuildRun(new MigrationRunMetricsValues
        {
            DurationMilliseconds = 30_000,
            FeatureThroughputPerSecond = 10
        });

        var result = MigrationRunMetricsBaselineEvaluator.Evaluate(run, baseline);

        result.Status.Should().Be(MigrationMetricBaselineStatuses.Fail);
        result.Signals.Single(s => s.Metric == "featureThroughputPerSecond").Status
            .Should().Be(MigrationMetricBaselineStatuses.Fail);
    }

    [Fact]
    public void Evaluate_WithMissingMetric_EmitsWarnSignalNotMeasured()
    {
        var baseline = MigrationFixtureBaselineCatalog.GeoServerSmallBaseline();
        // Duration omitted; feature throughput in pass band. Missing metric should Warn.
        var run = BuildRun(new MigrationRunMetricsValues
        {
            FeatureThroughputPerSecond = 200
        });

        var result = MigrationRunMetricsBaselineEvaluator.Evaluate(run, baseline);

        var duration = result.Signals.Single(s => s.Metric == "durationMilliseconds");
        duration.Status.Should().Be(MigrationMetricBaselineStatuses.Warn);
        duration.Observed.Should().BeNull();
        duration.Summary.Should().Contain("was not measured");
        result.Status.Should().Be(MigrationMetricBaselineStatuses.Warn);
    }

    [Fact]
    public void Evaluate_WithNegativeObserved_EmitsFailSignal()
    {
        var baseline = MigrationFixtureBaselineCatalog.GeoServerSmallBaseline();
        var run = BuildRun(new MigrationRunMetricsValues
        {
            DurationMilliseconds = 30_000,
            ManualReviewRatio = -0.5,
            FeatureThroughputPerSecond = 200
        });

        var result = MigrationRunMetricsBaselineEvaluator.Evaluate(run, baseline);

        var ratio = result.Signals.Single(s => s.Metric == "manualReviewRatio");
        ratio.Status.Should().Be(MigrationMetricBaselineStatuses.Fail);
        ratio.Summary.Should().Contain("must not be negative");
        result.Status.Should().Be(MigrationMetricBaselineStatuses.Fail);
    }

    [Fact]
    public void TryEvaluate_ResolvesBaselineFromCatalog()
    {
        var run = BuildRun(new MigrationRunMetricsValues
        {
            DurationMilliseconds = 30_000,
            FeatureThroughputPerSecond = 200
        });

        var result = MigrationRunMetricsBaselineEvaluator.TryEvaluate(
            run,
            MigrationCostPerformanceFixtureSizes.Small);

        result.Should().NotBeNull();
        result!.Status.Should().Be(MigrationMetricBaselineStatuses.Pass);
    }

    [Fact]
    public void TryEvaluate_WithUnseededFamily_ReturnsNull()
    {
        var run = BuildRun(
            new MigrationRunMetricsValues { DurationMilliseconds = 30_000 },
            sourceFamily: MigrationCostPerformanceSourceFamilies.ArcGisGeoServicesRest);

        var result = MigrationRunMetricsBaselineEvaluator.TryEvaluate(
            run,
            MigrationCostPerformanceFixtureSizes.Small);

        result.Should().BeNull();
    }

    [Fact]
    public void Baseline_RoundTripsThroughJson_PreservesShape()
    {
        var baseline = MigrationFixtureBaselineCatalog.GeoServerSmallBaseline();

        var json = JsonSerializer.Serialize(baseline, JsonOptions);
        var restored = JsonSerializer.Deserialize<MigrationMetricBaseline>(json, JsonOptions);

        restored.Should().NotBeNull();
        restored!.ProfileName.Should().Be(baseline.ProfileName);
        restored.SourceFamily.Should().Be(baseline.SourceFamily);
        restored.Size.Should().Be(baseline.Size);
        restored.FixtureProfile.ExpectedResourceCount.Should().Be(baseline.FixtureProfile.ExpectedResourceCount);
        restored.Bands.Should().HaveSameCount(baseline.Bands);
        restored.Bands.Select(b => b.Metric).Should().Equal(baseline.Bands.Select(b => b.Metric));
    }

    [Fact]
    public void Artifact_RoundTripsThroughJson_PreservesShape()
    {
        var baseline = MigrationFixtureBaselineCatalog.GeoServerSmallBaseline();
        var run = BuildRun(new MigrationRunMetricsValues
        {
            DurationMilliseconds = 60_000,
            FeatureThroughputPerSecond = 30
        });
        var artifact = MigrationRunMetricsBaselineEvaluator.Evaluate(run, baseline);

        var json = JsonSerializer.Serialize(artifact, JsonOptions);
        var restored = JsonSerializer.Deserialize<MigrationRunMetricsBaselineArtifact>(json, JsonOptions);

        restored.Should().NotBeNull();
        restored!.Status.Should().Be(artifact.Status);
        restored.Signals.Should().HaveSameCount(artifact.Signals);
        restored.Signals.Select(s => s.Metric).Should().Equal(artifact.Signals.Select(s => s.Metric));
        restored.BaselineProfile.Should().Be(artifact.BaselineProfile);
        restored.FixtureProfile.Description.Should().Be(artifact.FixtureProfile.Description);
    }

    private static MigrationRunMetricsArtifact BuildRun(
        MigrationRunMetricsValues totals,
        string sourceFamily = MigrationCostPerformanceSourceFamilies.GeoServerRest,
        string sourceKind = "geoserver-rest")
        => new()
        {
            SourceKind = sourceKind,
            SourceFamily = sourceFamily,
            Source = new MigrationRunMetricsSourceSummary
            {
                DisplayName = "fixture-host"
            },
            MeasurementScope = "slice2 baseline test",
            RunId = "test-run-001",
            StartedAt = DateTimeOffset.Parse("2026-05-19T10:00:00Z", CultureInfo.InvariantCulture),
            CompletedAt = DateTimeOffset.Parse("2026-05-19T10:01:00Z", CultureInfo.InvariantCulture),
            Totals = totals,
            Privacy = new MigrationRunMetricsPrivacySummary
            {
                SourceUrlsIncluded = false,
                CredentialValuesIncluded = false,
                SourceDataIncluded = false,
                OmittedFields = []
            }
        };
}
