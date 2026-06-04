// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;

namespace Honua.Core.Tests.Features.Import;

/// <summary>
/// Exercises the signed reconciliation scorecard builder (issue #1381): aggregation of per-layer
/// data-reconciliation outcomes, strict separation of the capability-parity dimension, and the
/// deterministic evidence-pack-style fingerprint.
/// </summary>
public sealed class MigrationReconciliationScorecardBuilderTests
{
    private static readonly DateTimeOffset GeneratedAt = new(2026, 6, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Build_AggregatesPerLayerOutcomes_AndDerivesVerdictFromDataReconciliation()
    {
        var artifact = BuildArtifact(
            MigrationReconciliationClassifications.Warn,
            passCount: 2,
            warnCount: 1,
            failCount: 0,
            skippedCount: 1,
            layerClassifications:
            [
                MigrationReconciliationClassifications.Pass,
                MigrationReconciliationClassifications.Pass,
                MigrationReconciliationClassifications.Warn,
                MigrationReconciliationClassifications.Skipped
            ],
            reasons: ["Feature-count delta 3 (1%) is outside the pass band; investigate."]);

        var scorecard = MigrationReconciliationScorecardBuilder.Build(
            "run-1", "arcgis-geoservices-rest", GeneratedAt, artifact, catalogReport: null,
            capabilityAutomationStatuses: ["automated", "automated", "unsupported"]);

        scorecard.Verdict.Should().Be(MigrationReconciliationClassifications.Warn);
        scorecard.DataReconciliation.LayerCount.Should().Be(4);
        scorecard.DataReconciliation.PassCount.Should().Be(2);
        scorecard.DataReconciliation.WarnCount.Should().Be(1);
        scorecard.DataReconciliation.SkippedCount.Should().Be(1);
        scorecard.DataReconciliation.Layers.Should().HaveCount(4);
        scorecard.DataReconciliation.Reasons.Should().ContainSingle();
    }

    [Fact]
    public void Build_KeepsCapabilityParityDistinctFromDataReconciliation()
    {
        // Data reconciles cleanly, but only half the constructs can be expressed on Honua. The two
        // dimensions must stay separate: the verdict (data) is pass while capability parity is 0.5.
        var artifact = BuildArtifact(
            MigrationReconciliationClassifications.Pass,
            passCount: 1, warnCount: 0, failCount: 0, skippedCount: 0,
            layerClassifications: [MigrationReconciliationClassifications.Pass],
            reasons: []);

        var scorecard = MigrationReconciliationScorecardBuilder.Build(
            "run-2", "arcgis-geoservices-rest", GeneratedAt, artifact, catalogReport: null,
            capabilityAutomationStatuses: ["automated", "unsupported", "manual-review", "assisted"]);

        scorecard.Verdict.Should().Be(MigrationReconciliationClassifications.Pass);
        scorecard.DataReconciliation.Classification.Should().Be(MigrationReconciliationClassifications.Pass);

        scorecard.CapabilityParity.ConstructCount.Should().Be(4);
        scorecard.CapabilityParity.AutomatedCount.Should().Be(1);
        scorecard.CapabilityParity.AssistedCount.Should().Be(1);
        scorecard.CapabilityParity.ManualReviewCount.Should().Be(1);
        scorecard.CapabilityParity.UnsupportedCount.Should().Be(1);
        // (automated + assisted) / total = 2/4. Capability parity does NOT change the verdict.
        scorecard.CapabilityParity.ParityRatio.Should().Be(0.5);
    }

    [Fact]
    public void Build_WhenCatalogReportHasFailFinding_EscalatesDataClassificationToFail()
    {
        var artifact = BuildArtifact(
            MigrationReconciliationClassifications.Pass,
            passCount: 1, warnCount: 0, failCount: 0, skippedCount: 0,
            layerClassifications: [MigrationReconciliationClassifications.Pass],
            reasons: []);

        var catalogReport = new MigrationCatalogReconciliationReport
        {
            RunId = "run-3",
            SourceKind = "arcgis-geoservices-rest",
            Summary = new MigrationCatalogReconciliationSummary
            {
                ResourceCount = 1,
                FailResourceCount = 1,
                FindingCount = 1
            },
            Resources =
            [
                new MigrationCatalogReconciliationResource
                {
                    SourceResourceId = "layer:parcels",
                    Classification = MigrationCatalogReconciliationClassifications.Fail,
                    Findings =
                    [
                        new MigrationCatalogReconciliationFinding
                        {
                            Code = MigrationCatalogReconciliationCodes.SubtypeMissing,
                            Severity = MigrationCatalogReconciliationSeverities.Fail,
                            Summary = "Published subtype set is missing."
                        }
                    ]
                }
            ]
        };

        var scorecard = MigrationReconciliationScorecardBuilder.Build(
            "run-3", "arcgis-geoservices-rest", GeneratedAt, artifact, catalogReport,
            capabilityAutomationStatuses: []);

        scorecard.Verdict.Should().Be(MigrationReconciliationClassifications.Fail);
        scorecard.DataReconciliation.Classification.Should().Be(MigrationReconciliationClassifications.Fail);
        scorecard.DataReconciliation.CatalogFindingCount.Should().Be(1);
        scorecard.DataReconciliation.Reasons.Should().Contain("Published subtype set is missing.");
    }

    [Fact]
    public void Build_SetsDeterministicFingerprint_ThatRecomputesOverUnsignedBody()
    {
        var artifact = BuildArtifact(
            MigrationReconciliationClassifications.Pass,
            passCount: 1, warnCount: 0, failCount: 0, skippedCount: 0,
            layerClassifications: [MigrationReconciliationClassifications.Pass],
            reasons: []);

        var first = MigrationReconciliationScorecardBuilder.Build(
            "run-4", "arcgis-geoservices-rest", GeneratedAt, artifact, null, ["automated"]);
        var second = MigrationReconciliationScorecardBuilder.Build(
            "run-4", "arcgis-geoservices-rest", GeneratedAt, artifact, null, ["automated"]);

        first.Fingerprint.Should().StartWith("sha256:");
        first.Fingerprint.Should().Be(second.Fingerprint);

        // Recomputing the fingerprint over the signed scorecard (which clears Fingerprint first)
        // reproduces the embedded value — proving the signature is over the unsigned body.
        MigrationReconciliationScorecardBuilder.ComputeFingerprint(first).Should().Be(first.Fingerprint);
    }

    [Fact]
    public void Build_WhenNoConstructsAssessed_ReportsFullCapabilityParity()
    {
        var artifact = BuildArtifact(
            MigrationReconciliationClassifications.Pass,
            passCount: 1, warnCount: 0, failCount: 0, skippedCount: 0,
            layerClassifications: [MigrationReconciliationClassifications.Pass],
            reasons: []);

        var scorecard = MigrationReconciliationScorecardBuilder.Build(
            "run-5", "arcgis-geoservices-rest", GeneratedAt, artifact, null, []);

        scorecard.CapabilityParity.ConstructCount.Should().Be(0);
        scorecard.CapabilityParity.ParityRatio.Should().Be(1d);
    }

    private static MigrationReconciliationArtifact BuildArtifact(
        string classification,
        int passCount,
        int warnCount,
        int failCount,
        int skippedCount,
        string[] layerClassifications,
        string[] reasons)
    {
        var layers = layerClassifications
            .Select((c, i) => new MigrationReconciliationLayerReport
            {
                SourceLayerId = $"layer-{i}",
                SourceLayerName = $"Layer {i}",
                TargetHonuaLayerId = i + 1,
                Classification = c,
                Count = new MigrationReconciliationCountProbe { Classification = c },
                Geometry = new MigrationReconciliationGeometryProbe { Classification = c },
                Content = new MigrationReconciliationContentProbe { Classification = c },
                Extent = new MigrationReconciliationExtentProbe { Classification = c }
            })
            .ToArray();

        return new MigrationReconciliationArtifact
        {
            RunId = "run",
            SourceKind = "arcgis-geoservices-rest",
            Classification = classification,
            StartedAt = GeneratedAt,
            CompletedAt = GeneratedAt,
            Summary = new MigrationReconciliationSummary
            {
                LayerCount = layerClassifications.Length,
                PassCount = passCount,
                WarnCount = warnCount,
                FailCount = failCount,
                SkippedCount = skippedCount
            },
            Layers = layers,
            Reasons = reasons,
            Options = new LayerReconciliationOptions()
        };
    }
}
