// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Tests.Features.Import;

/// <summary>
/// Tests for <see cref="MigrationPerformanceEvidenceBuilder"/> (issue #1033 slice 4).
/// </summary>
public sealed class MigrationPerformanceEvidenceBuilderTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private static readonly DateTimeOffset GeneratedAt =
        DateTimeOffset.Parse("2026-05-19T12:00:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public void Build_ProducesArtifactWithPassStatusAndStableFingerprint()
    {
        var baseline = MigrationFixtureBaselineCatalog.GeoServerSmallBaseline();
        var run = BuildRun(PassingTotals());
        var evaluation = MigrationRunMetricsBaselineEvaluator.Evaluate(run, baseline);

        var artifact = MigrationPerformanceEvidenceBuilder.Build(run, evaluation, baseline.FixtureProfile, GeneratedAt);

        artifact.ArtifactKind.Should().Be("honua.migration.performance-evidence");
        artifact.ArtifactVersion.Should().Be("1.0");
        artifact.IssueReference.Should().Be("honua-server#1033");
        artifact.SourceFamily.Should().Be(MigrationCostPerformanceSourceFamilies.GeoServerRest);
        artifact.FixtureSize.Should().Be(MigrationCostPerformanceFixtureSizes.Small);
        artifact.BaselineProfile.Should().Be("geoserver-small-v1");
        artifact.Status.Should().Be(MigrationMetricBaselineStatuses.Pass);
        artifact.GeneratedAt.Should().Be(GeneratedAt.ToUniversalTime());
        artifact.RunMetrics.Should().BeSameAs(run);
        artifact.BaselineEvaluation.Should().BeSameAs(evaluation);
        artifact.FixtureProfile.Should().BeSameAs(baseline.FixtureProfile);
        artifact.Fingerprint.Should().StartWith("sha256:").And.HaveLength(7 + 64);
        artifact.Summary.Should().Contain("within").And.Contain("geoserver-small-v1");
    }

    [Fact]
    public void Build_IsDeterministic_ForIdenticalInputs()
    {
        var baseline = MigrationFixtureBaselineCatalog.GeoServerSmallBaseline();
        var run = BuildRun(PassingTotals());
        var evaluation = MigrationRunMetricsBaselineEvaluator.Evaluate(run, baseline);

        var first = MigrationPerformanceEvidenceBuilder.Build(run, evaluation, baseline.FixtureProfile, GeneratedAt);
        var second = MigrationPerformanceEvidenceBuilder.Build(run, evaluation, baseline.FixtureProfile, GeneratedAt);

        first.Fingerprint.Should().Be(second.Fingerprint);

        var firstJson = JsonSerializer.Serialize(first, JsonOptions);
        var secondJson = JsonSerializer.Serialize(second, JsonOptions);
        firstJson.Should().Be(secondJson);
    }

    [Fact]
    public void Build_FingerprintChanges_WhenAnyMetricChanges()
    {
        var baseline = MigrationFixtureBaselineCatalog.GeoServerSmallBaseline();
        var run = BuildRun(PassingTotals());
        var evaluation = MigrationRunMetricsBaselineEvaluator.Evaluate(run, baseline);
        var baselineArtifact = MigrationPerformanceEvidenceBuilder.Build(run, evaluation, baseline.FixtureProfile, GeneratedAt);

        var perturbedTotals = PassingTotals() with { DurationMilliseconds = 31_000 };
        var perturbedRun = BuildRun(perturbedTotals);
        var perturbedEval = MigrationRunMetricsBaselineEvaluator.Evaluate(perturbedRun, baseline);
        var perturbed = MigrationPerformanceEvidenceBuilder.Build(perturbedRun, perturbedEval, baseline.FixtureProfile, GeneratedAt);

        perturbed.Fingerprint.Should().NotBe(baselineArtifact.Fingerprint);
    }

    [Fact]
    public void Build_PropagatesWarnAndFailStatuses()
    {
        var baseline = MigrationFixtureBaselineCatalog.GeoServerSmallBaseline();

        var warnRun = BuildRun(PassingTotals() with { DurationMilliseconds = 60_000 });
        var warnEval = MigrationRunMetricsBaselineEvaluator.Evaluate(warnRun, baseline);
        var warnArtifact = MigrationPerformanceEvidenceBuilder.Build(warnRun, warnEval, baseline.FixtureProfile, GeneratedAt);
        warnArtifact.Status.Should().Be(MigrationMetricBaselineStatuses.Warn);
        warnArtifact.Summary.Should().Contain("warnings");

        var failRun = BuildRun(PassingTotals() with { DurationMilliseconds = 180_000 });
        var failEval = MigrationRunMetricsBaselineEvaluator.Evaluate(failRun, baseline);
        var failArtifact = MigrationPerformanceEvidenceBuilder.Build(failRun, failEval, baseline.FixtureProfile, GeneratedAt);
        failArtifact.Status.Should().Be(MigrationMetricBaselineStatuses.Fail);
        failArtifact.Summary.Should().Contain("failed");
    }

    [Fact]
    public void Build_ThrowsWhenRunAndBaselineFamiliesDisagree()
    {
        var baseline = MigrationFixtureBaselineCatalog.GeoServerSmallBaseline();
        var run = BuildRun(PassingTotals(), sourceFamily: MigrationCostPerformanceSourceFamilies.ArcGisGeoServicesRest);
        var evaluation = MigrationRunMetricsBaselineEvaluator.Evaluate(BuildRun(PassingTotals()), baseline);

        var act = () => MigrationPerformanceEvidenceBuilder.Build(
            run, evaluation, baseline.FixtureProfile, GeneratedAt);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Build_RedactionPosture_IsDenyByDefaultAndCarriesOmittedFields()
    {
        var baseline = MigrationFixtureBaselineCatalog.GeoServerSmallBaseline();
        var run = BuildRun(PassingTotals());
        var evaluation = MigrationRunMetricsBaselineEvaluator.Evaluate(run, baseline);

        var artifact = MigrationPerformanceEvidenceBuilder.Build(run, evaluation, baseline.FixtureProfile, GeneratedAt);

        artifact.Redaction.SourceUrlsIncluded.Should().BeFalse();
        artifact.Redaction.CredentialValuesIncluded.Should().BeFalse();
        artifact.Redaction.SourceDataIncluded.Should().BeFalse();
        artifact.Redaction.OperatorIdentitiesIncluded.Should().BeFalse();
        artifact.Redaction.OmittedFields.Should().Contain("source.baseUrl");
        artifact.Redaction.OmittedFields.Should().Contain("credential values");
        artifact.Redaction.OmittedFields.Should().Contain("operator identifying values");
        artifact.Redaction.OmittedFields.Should().Contain("source data samples");
        artifact.Redaction.Summary.Should().Contain("Deny-by-default");
    }

    [Fact]
    public void Build_SerializedArtifact_ExcludesSourceUrlsAndCredentialMarkers()
    {
        var baseline = MigrationFixtureBaselineCatalog.GeoServerSmallBaseline();
        var run = BuildRun(PassingTotals());
        var evaluation = MigrationRunMetricsBaselineEvaluator.Evaluate(run, baseline);

        var artifact = MigrationPerformanceEvidenceBuilder.Build(run, evaluation, baseline.FixtureProfile, GeneratedAt);
        var json = JsonSerializer.Serialize(artifact, JsonOptions);

        json.Should().NotContain("https://");
        json.Should().NotContain("http://");
        json.Should().NotContain("password", "credential markers must never appear in published evidence");
        json.Should().NotContain("api_key");
        json.Should().NotContain("authorization");
    }

    [Fact]
    public void ArtifactSchema_TopLevelProperties_AreStable()
    {
        // Pins the public top-level shape of MigrationPerformanceEvidenceArtifact so
        // the website-linkable contract cannot change accidentally. If you intentionally
        // add or rename a field, update this list AND the docs page at
        // docs/evidence/migration-performance-evidence.md.
        var properties = typeof(MigrationPerformanceEvidenceArtifact)
            .GetProperties()
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        properties.Should().BeEquivalentTo(
            [
                "ArtifactKind",
                "ArtifactVersion",
                "BaselineEvaluation",
                "BaselineProfile",
                "Fingerprint",
                "FixtureProfile",
                "FixtureSize",
                "GeneratedAt",
                "IssueReference",
                "MeasurementScope",
                "Redaction",
                "RunId",
                "RunMetrics",
                "SourceFamily",
                "Status",
                "Summary"
            ],
            options => options.WithStrictOrdering());
    }

    [Fact]
    public void RedactionPosture_TopLevelProperties_AreStable()
    {
        var properties = typeof(MigrationPerformanceEvidenceRedactionPosture)
            .GetProperties()
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        properties.Should().BeEquivalentTo(
            [
                "CredentialValuesIncluded",
                "OmittedFields",
                "OperatorIdentitiesIncluded",
                "SourceDataIncluded",
                "SourceUrlsIncluded",
                "Summary"
            ],
            options => options.WithStrictOrdering());
    }

    [Fact]
    public void Build_RoundTripsThroughJson_WithStableShape()
    {
        var baseline = MigrationFixtureBaselineCatalog.GeoServerSmallBaseline();
        var run = BuildRun(PassingTotals());
        var evaluation = MigrationRunMetricsBaselineEvaluator.Evaluate(run, baseline);
        var artifact = MigrationPerformanceEvidenceBuilder.Build(run, evaluation, baseline.FixtureProfile, GeneratedAt);

        var json = JsonSerializer.Serialize(artifact, JsonOptions);
        var restored = JsonSerializer.Deserialize<MigrationPerformanceEvidenceArtifact>(json, JsonOptions);

        restored.Should().NotBeNull();
        restored!.Fingerprint.Should().Be(artifact.Fingerprint);
        restored.Status.Should().Be(artifact.Status);
        restored.BaselineProfile.Should().Be(artifact.BaselineProfile);
        restored.SourceFamily.Should().Be(artifact.SourceFamily);
        restored.FixtureSize.Should().Be(artifact.FixtureSize);
        restored.RunMetrics.Totals.DurationMilliseconds.Should().Be(artifact.RunMetrics.Totals.DurationMilliseconds);
        restored.BaselineEvaluation.Signals.Should().HaveSameCount(artifact.BaselineEvaluation.Signals);
        restored.Redaction.OmittedFields.Should().BeEquivalentTo(artifact.Redaction.OmittedFields);
    }

    private static MigrationRunMetricsValues PassingTotals() => new()
    {
        DurationMilliseconds = 30_000,
        SourceRequestCount = 200,
        BytesRead = 32L * 1024 * 1024,
        BytesWritten = 32L * 1024 * 1024,
        RetryCount = 0,
        ResumeCount = 0,
        ManualReviewRatio = 0.0,
        FeatureThroughputPerSecond = 200,
        FeatureCount = 6_000,
        ResourceCount = 100
    };

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
                DisplayName = "fixture-host",
                Product = "GeoServer",
                Version = "2.24.0"
            },
            MeasurementScope = "release-gated geoserver-small fixture",
            RunId = "release-evidence-2026-05-19",
            StartedAt = DateTimeOffset.Parse("2026-05-19T10:00:00Z", CultureInfo.InvariantCulture),
            CompletedAt = DateTimeOffset.Parse("2026-05-19T10:00:30Z", CultureInfo.InvariantCulture),
            Totals = totals,
            Privacy = new MigrationRunMetricsPrivacySummary
            {
                SourceUrlsIncluded = false,
                CredentialValuesIncluded = false,
                SourceDataIncluded = false,
                OmittedFields = ["source.baseUrl", "credential values"]
            }
        };
}
