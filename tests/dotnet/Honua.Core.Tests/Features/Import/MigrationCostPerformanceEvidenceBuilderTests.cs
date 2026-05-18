// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Services;

namespace Honua.Core.Tests.Features.Import;

public sealed class MigrationCostPerformanceEvidenceBuilderTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    [Fact]
    public void Build_WithUnorderedPhaseMeasurements_EmitsDeterministicPhasesAndTotals()
    {
        var inventory = CreateInventory();
        var input = new MigrationCostPerformanceEvidenceInput
        {
            MeasurementScope = "nightly medium fixture",
            RunId = "Release Evidence 2026-05-18",
            FixtureProfile = MediumGeoServerFixture(),
            PhaseMeasurements =
            [
                Measurement(MigrationCostPerformancePhases.Import, duration: 200, sourceRequests: 6, bytesRead: 600, bytesWritten: 900, featureCount: 50, artifactBytes: 1_200),
                Measurement(MigrationCostPerformancePhases.Scan, duration: 100, sourceRequests: 4, bytesRead: 400, bytesWritten: 100, resourceCount: 3, cpu: 700, memory: 2_048),
                Measurement(MigrationCostPerformancePhases.Apply, duration: 30, sourceRequests: 1, bytesRead: 50, bytesWritten: 75, resumeCount: 1),
                Measurement(MigrationCostPerformancePhases.Manifest, duration: 10, sourceRequests: 1, bytesRead: 10, bytesWritten: 20, manualReviewCount: 1, candidateItemCount: 10)
            ],
            RecoveryMeasurements = PassingRecovery()
        };

        var artifact = MigrationCostPerformanceEvidenceBuilder.Build(inventory, input, PassingThresholds());

        artifact.OverallClassification.Should().Be(MigrationCostPerformanceClassifications.Pass);
        artifact.Phases.Select(static phase => phase.Phase).Should().Equal(
            MigrationCostPerformancePhases.Scan,
            MigrationCostPerformancePhases.Manifest,
            MigrationCostPerformancePhases.Apply,
            MigrationCostPerformancePhases.Import);
        artifact.Totals.DurationMilliseconds.Should().Be(340);
        artifact.Totals.SourceRequestCount.Should().Be(12);
        artifact.Totals.BytesRead.Should().Be(1_060);
        artifact.Totals.BytesWritten.Should().Be(1_095);
        artifact.Totals.ResumeCount.Should().Be(1);
        artifact.Totals.CpuMilliseconds.Should().Be(700);
        artifact.Totals.PeakMemoryBytes.Should().Be(2_048);
        artifact.Totals.ArtifactBytes.Should().Be(1_200);
        artifact.Totals.ResourceThroughputPerSecond.Should().BeApproximately(8.8235, 0.0001);
        artifact.Totals.FeatureThroughputPerSecond.Should().BeApproximately(147.0588, 0.0001);
        artifact.Totals.ManualReviewCount.Should().Be(1);
        artifact.Totals.CandidateItemCount.Should().Be(10);
        artifact.Totals.ManualReviewRatio.Should().Be(0.1);
        artifact.FixtureProfile.Should().Match<MigrationCostPerformanceFixtureProfile>(fixture =>
            fixture.SourceFamily == MigrationCostPerformanceSourceFamilies.GeoServerRest &&
            fixture.Size == MigrationCostPerformanceFixtureSizes.Medium &&
            fixture.ExpectedResourceCount == 3 &&
            fixture.ExpectedFeatureCount == 50);
        artifact.Recovery.Should().OnlyContain(recovery =>
            recovery.Classification == MigrationCostPerformanceClassifications.Pass);
        artifact.RunId.Should().Be("release-evidence-2026-05-18");
        artifact.Privacy.Should().Match<MigrationCostPerformancePrivacySummary>(privacy =>
            !privacy.SourceUrlsIncluded &&
            !privacy.CredentialValuesIncluded &&
            !privacy.SourceDataIncluded);
    }

    [Fact]
    public void Build_WithThresholdBreaches_ClassifiesWarnAndFailSignals()
    {
        var input = new MigrationCostPerformanceEvidenceInput
        {
            MeasurementScope = "strict fixture",
            PhaseMeasurements =
            [
                Measurement(MigrationCostPerformancePhases.Scan, retryCount: 1),
                Measurement(MigrationCostPerformancePhases.Manifest, duration: 10),
                Measurement(MigrationCostPerformancePhases.Apply, duration: 20),
                Measurement(MigrationCostPerformancePhases.Import, bytesRead: 600)
            ]
        };
        var thresholds = new MigrationCostPerformanceThresholds
        {
            ProfileName = "strict",
            RetryWarnCount = 1,
            RetryFailCount = 3,
            BytesReadWarn = 100,
            BytesReadFail = 500,
            FeatureThroughputWarnPerSecond = 1,
            FeatureThroughputFailPerSecond = 0.1
        };

        var artifact = MigrationCostPerformanceEvidenceBuilder.Build(CreateInventory(), input, thresholds);

        artifact.OverallClassification.Should().Be(MigrationCostPerformanceClassifications.Fail);
        artifact.Phases.Should().ContainSingle(phase => phase.Phase == MigrationCostPerformancePhases.Scan)
            .Which.Should().Match<MigrationCostPerformancePhaseEvidence>(phase =>
                phase.Classification == MigrationCostPerformanceClassifications.Warn &&
                phase.Signals.Single(signal => signal.Metric == "retryCount").Classification == MigrationCostPerformanceClassifications.Warn);
        artifact.Phases.Should().ContainSingle(phase => phase.Phase == MigrationCostPerformancePhases.Import)
            .Which.Should().Match<MigrationCostPerformancePhaseEvidence>(phase =>
                phase.Classification == MigrationCostPerformanceClassifications.Fail &&
                phase.Signals.Single(signal => signal.Metric == "bytesRead").Classification == MigrationCostPerformanceClassifications.Fail);
    }

    [Fact]
    public void Build_WithLowDerivedThroughput_ClassifiesLowerBoundSignal()
    {
        var input = new MigrationCostPerformanceEvidenceInput
        {
            MeasurementScope = "large fixture",
            FixtureProfile = new MigrationCostPerformanceFixtureProfile
            {
                SourceFamily = MigrationCostPerformanceSourceFamilies.ArcGisGeoServicesRest,
                Size = MigrationCostPerformanceFixtureSizes.Large,
                ExpectedResourceCount = 1,
                ExpectedFeatureCount = 1
            },
            PhaseMeasurements =
            [
                Measurement(MigrationCostPerformancePhases.Scan, duration: 1, sourceRequests: 1),
                Measurement(MigrationCostPerformancePhases.Manifest, duration: 1),
                Measurement(MigrationCostPerformancePhases.Apply, duration: 1),
                Measurement(MigrationCostPerformancePhases.Import, duration: 10_000, featureCount: 1)
            ],
            RecoveryMeasurements = PassingRecovery()
        };
        var thresholds = PassingThresholds() with
        {
            FeatureThroughputWarnPerSecond = 1,
            FeatureThroughputFailPerSecond = 0.2
        };

        var artifact = MigrationCostPerformanceEvidenceBuilder.Build(CreateInventory(), input, thresholds);

        artifact.OverallClassification.Should().Be(MigrationCostPerformanceClassifications.Fail);
        artifact.Phases.Should().ContainSingle(phase => phase.Phase == MigrationCostPerformancePhases.Import)
            .Which.Signals.Should().ContainSingle(signal => signal.Metric == "featureThroughputPerSecond")
            .Which.Should().Match<MigrationCostPerformanceSignal>(signal =>
                signal.Classification == MigrationCostPerformanceClassifications.Fail &&
                signal.Observed == 0.1);
    }

    [Fact]
    public void Build_WithMissingRecoveryScenario_ReturnsWarnForIncompleteRecoveryEvidence()
    {
        var input = new MigrationCostPerformanceEvidenceInput
        {
            MeasurementScope = "small fixture",
            FixtureProfile = MediumGeoServerFixture(),
            PhaseMeasurements = RequiredPhaseMeasurements(),
            RecoveryMeasurements =
            [
                new MigrationCostPerformanceRecoveryMeasurement
                {
                    Scenario = MigrationCostPerformanceRecoveryScenarios.SourceFailure,
                    Recovered = true,
                    ResumeObserved = true,
                    AttemptCount = 2
                }
            ]
        };

        var artifact = MigrationCostPerformanceEvidenceBuilder.Build(CreateInventory(), input, PassingThresholds());

        artifact.OverallClassification.Should().Be(MigrationCostPerformanceClassifications.Warn);
        artifact.Recovery.Should().Contain(recovery =>
            recovery.Scenario == MigrationCostPerformanceRecoveryScenarios.JobCancellation &&
            recovery.Classification == MigrationCostPerformanceClassifications.Warn);
    }

    [Fact]
    public void Build_WithFailedRepeatedApplyAttempt_ReturnsFailRecoveryEvidence()
    {
        var input = new MigrationCostPerformanceEvidenceInput
        {
            MeasurementScope = "small fixture",
            FixtureProfile = MediumGeoServerFixture(),
            PhaseMeasurements = RequiredPhaseMeasurements(),
            RecoveryMeasurements =
            [
                new MigrationCostPerformanceRecoveryMeasurement
                {
                    Scenario = MigrationCostPerformanceRecoveryScenarios.SourceFailure,
                    Recovered = true,
                    ResumeObserved = true,
                    AttemptCount = 2
                },
                new MigrationCostPerformanceRecoveryMeasurement
                {
                    Scenario = MigrationCostPerformanceRecoveryScenarios.JobCancellation,
                    Recovered = true,
                    ResumeObserved = true,
                    AttemptCount = 2
                },
                new MigrationCostPerformanceRecoveryMeasurement
                {
                    Scenario = MigrationCostPerformanceRecoveryScenarios.TransientNetworkError,
                    Recovered = true,
                    ResumeObserved = true,
                    AttemptCount = 2
                },
                new MigrationCostPerformanceRecoveryMeasurement
                {
                    Scenario = MigrationCostPerformanceRecoveryScenarios.RepeatedApplyAttempt,
                    Recovered = true,
                    IdempotentReplay = false,
                    AttemptCount = 2
                }
            ]
        };

        var artifact = MigrationCostPerformanceEvidenceBuilder.Build(CreateInventory(), input, PassingThresholds());

        artifact.OverallClassification.Should().Be(MigrationCostPerformanceClassifications.Fail);
        artifact.Recovery.Should().ContainSingle(recovery =>
                recovery.Scenario == MigrationCostPerformanceRecoveryScenarios.RepeatedApplyAttempt)
            .Which.Classification.Should().Be(MigrationCostPerformanceClassifications.Fail);
    }

    [Fact]
    public void Build_WithMissingRequiredPhases_ReturnsWarnForIncompleteEvidence()
    {
        var input = new MigrationCostPerformanceEvidenceInput
        {
            MeasurementScope = "scan-only fixture",
            PhaseMeasurements =
            [
                Measurement(MigrationCostPerformancePhases.Scan, duration: 5, sourceRequests: 1)
            ]
        };

        var artifact = MigrationCostPerformanceEvidenceBuilder.Build(CreateInventory(), input, PassingThresholds());

        artifact.OverallClassification.Should().Be(MigrationCostPerformanceClassifications.Warn);
        artifact.Phases.Where(static phase => phase.Phase != MigrationCostPerformancePhases.Scan)
            .Should().OnlyContain(phase =>
                phase.Classification == MigrationCostPerformanceClassifications.Warn &&
                phase.Signals.Single().Metric == "phaseMeasured");
    }

    [Fact]
    public void Build_WithCredentialBearingSource_DoesNotEmitPrivateUrlsOrCredentialValues()
    {
        var inventory = CreateInventory(
            displayName: "https://user:password@private.example.test/geoserver?token=abc",
            baseUrl: "https://user:password@private.example.test/geoserver?token=abc");
        var input = new MigrationCostPerformanceEvidenceInput
        {
            MeasurementScope = "pilot https://private.example.test/geoserver?token=abc",
            RunId = "run?token=abc",
            PhaseMeasurements =
            [
                Measurement("https://private.example.test/scan?token=abc", duration: 1),
                Measurement(MigrationCostPerformancePhases.Manifest, duration: 1),
                Measurement(MigrationCostPerformancePhases.Apply, duration: 1),
                Measurement(MigrationCostPerformancePhases.Import, duration: 1)
            ]
        };

        var artifact = MigrationCostPerformanceEvidenceBuilder.Build(inventory, input, PassingThresholds());
        var json = JsonSerializer.Serialize(artifact, JsonOptions);

        artifact.Source.DisplayName.Should().Be("[redacted-url]");
        artifact.MeasurementScope.Should().Be("[redacted-url]");
        artifact.RunId.Should().Be("run");
        json.Should().NotContain("private.example.test");
        json.Should().NotContain("user:password");
        json.Should().NotContain("token=abc");
        json.Should().NotContain("?token");
        json.Should().NotContain("https://");
    }

    private static MigrationCostPerformancePhaseMeasurement Measurement(
        string phase,
        long? duration = null,
        long? sourceRequests = null,
        long? bytesRead = null,
        long? bytesWritten = null,
        int? retryCount = null,
        int? resumeCount = null,
        long? cpu = null,
        long? memory = null,
        long? resourceCount = null,
        long? featureCount = null,
        long? coverageCount = null,
        long? artifactBytes = null,
        int? manualReviewCount = null,
        int? candidateItemCount = null)
        => new()
        {
            Phase = phase,
            Metrics = new MigrationCostPerformanceMetrics
            {
                DurationMilliseconds = duration,
                SourceRequestCount = sourceRequests,
                BytesRead = bytesRead,
                BytesWritten = bytesWritten,
                RetryCount = retryCount,
                ResumeCount = resumeCount,
                CpuMilliseconds = cpu,
                PeakMemoryBytes = memory,
                ResourceCount = resourceCount,
                FeatureCount = featureCount,
                CoverageCount = coverageCount,
                ArtifactBytes = artifactBytes,
                ManualReviewCount = manualReviewCount,
                CandidateItemCount = candidateItemCount
            }
        };

    private static MigrationCostPerformanceFixtureProfile MediumGeoServerFixture()
        => new()
        {
            SourceFamily = MigrationCostPerformanceSourceFamilies.GeoServerRest,
            Size = MigrationCostPerformanceFixtureSizes.Medium,
            ExpectedResourceCount = 3,
            ExpectedFeatureCount = 50,
            Description = "GeoServer REST medium fixture"
        };

    private static MigrationCostPerformancePhaseMeasurement[] RequiredPhaseMeasurements()
        =>
        [
            Measurement(MigrationCostPerformancePhases.Scan, duration: 10, sourceRequests: 1, resourceCount: 1),
            Measurement(MigrationCostPerformancePhases.Manifest, duration: 10),
            Measurement(MigrationCostPerformancePhases.Apply, duration: 10),
            Measurement(MigrationCostPerformancePhases.Import, duration: 10, featureCount: 1)
        ];

    private static MigrationCostPerformanceRecoveryMeasurement[] PassingRecovery()
        =>
        [
            new MigrationCostPerformanceRecoveryMeasurement
            {
                Scenario = MigrationCostPerformanceRecoveryScenarios.SourceFailure,
                Recovered = true,
                ResumeObserved = true,
                AttemptCount = 2
            },
            new MigrationCostPerformanceRecoveryMeasurement
            {
                Scenario = MigrationCostPerformanceRecoveryScenarios.JobCancellation,
                Recovered = true,
                ResumeObserved = true,
                AttemptCount = 2
            },
            new MigrationCostPerformanceRecoveryMeasurement
            {
                Scenario = MigrationCostPerformanceRecoveryScenarios.TransientNetworkError,
                Recovered = true,
                ResumeObserved = true,
                AttemptCount = 2
            },
            new MigrationCostPerformanceRecoveryMeasurement
            {
                Scenario = MigrationCostPerformanceRecoveryScenarios.RepeatedApplyAttempt,
                Recovered = true,
                IdempotentReplay = true,
                AttemptCount = 2
            }
        ];

    private static MigrationCostPerformanceThresholds PassingThresholds()
        => new()
        {
            ProfileName = "fixture",
            DurationWarnMilliseconds = 10_000,
            DurationFailMilliseconds = 20_000,
            SourceRequestWarnCount = 100,
            SourceRequestFailCount = 200,
            BytesReadWarn = 100_000,
            BytesReadFail = 200_000,
            BytesWrittenWarn = 100_000,
            BytesWrittenFail = 200_000,
            RetryWarnCount = 10,
            RetryFailCount = 20,
            ResumeWarnCount = 10,
            ResumeFailCount = 20,
            CpuWarnMilliseconds = 10_000,
            CpuFailMilliseconds = 20_000,
            PeakMemoryWarnBytes = 10_000,
            PeakMemoryFailBytes = 20_000,
            ArtifactBytesWarn = 10_000,
            ArtifactBytesFail = 20_000,
            ResourceThroughputWarnPerSecond = 0.1,
            ResourceThroughputFailPerSecond = 0.01,
            FeatureThroughputWarnPerSecond = 0.1,
            FeatureThroughputFailPerSecond = 0.01,
            CoverageThroughputWarnPerSecond = 0.1,
            CoverageThroughputFailPerSecond = 0.01,
            ManualReviewRatioWarn = 0.5,
            ManualReviewRatioFail = 0.75
        };

    private static MigrationSourceInventoryArtifact CreateInventory(
        string displayName = "Fixture GeoServer",
        string baseUrl = "https://geoserver.example.test/geoserver")
        => new()
        {
            SourceKind = "geoserver-rest",
            Source = new MigrationSourceIdentity
            {
                DisplayName = displayName,
                BaseUrl = baseUrl,
                Product = "GeoServer",
                Version = "2.26.0",
                ServiceType = "rest"
            },
            AuthPosture = new MigrationInventoryAuthPosture
            {
                Mode = "anonymous",
                AccessConfirmed = true
            },
            ScanCompleteness = new MigrationInventoryCompleteness
            {
                Status = "complete"
            },
            Summary = new MigrationInventorySummary
            {
                ResourceCount = 3,
                CompatibleCount = 3
            },
            OverallCompatibility = new MigrationCompatibilityAssessment
            {
                Level = "compatible",
                Reason = "Fixture inventory is compatible."
            }
        };
}
