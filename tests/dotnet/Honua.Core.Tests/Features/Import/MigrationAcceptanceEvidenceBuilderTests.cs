// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Services;

namespace Honua.Core.Tests.Features.Import;

public sealed class MigrationAcceptanceEvidenceBuilderTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    [Fact]
    public void Build_WithRequiredSourceFamiliesCovered_ReturnsPassSuite()
    {
        var suite = MigrationAcceptanceEvidenceBuilder.Build(
            "nightly-20260518",
            [
                AutomatedInput("arcgis", "arcgis-geoservices-rest"),
                AutomatedInput("geoserver", "geoserver-rest")
            ],
            new MigrationAcceptanceEvidenceOptions
            {
                RequiredSourceKinds = ["geoserver-rest", "arcgis-geoservices-rest"]
            });

        suite.Summary.OverallState.Should().Be(MigrationEvidenceStates.Pass);
        suite.Summary.SourceCount.Should().Be(2);
        suite.Summary.AutomatedSourceCount.Should().Be(2);
        suite.Summary.CoveredSourceKinds.Should().Equal("arcgis-geoservices-rest", "geoserver-rest");
        suite.BlockingGaps.Should().BeEmpty();
        suite.Entries.Select(entry => entry.Id).Should().Equal("arcgis", "geoserver");
        suite.Entries.Should().OnlyContain(entry => entry.AutomationLevel == MigrationAutomationLevels.Automated);
        suite.Entries.Should().OnlyContain(entry => entry.Stages.All(stage =>
            stage.State == MigrationEvidenceStates.Pass ||
            stage.State == MigrationEvidenceStates.NotApplicable));
    }

    [Fact]
    public void Build_WithMissingRequiredSourceFamily_ReturnsBlockingGap()
    {
        var suite = MigrationAcceptanceEvidenceBuilder.Build(
            "nightly-20260518",
            [AutomatedInput("arcgis", "arcgis-geoservices-rest")],
            new MigrationAcceptanceEvidenceOptions
            {
                RequiredSourceKinds = ["arcgis-geoservices-rest", "geoserver-rest", "ogc-api-features"]
            });

        suite.Summary.OverallState.Should().Be(MigrationEvidenceStates.Fail);
        suite.Summary.RequiredSourceKinds.Should().Equal("arcgis-geoservices-rest", "geoserver-rest", "ogc-api-features");
        suite.BlockingGaps.Select(gap => gap.Id).Should().Equal(
            "missing-source-kind:geoserver-rest",
            "missing-source-kind:ogc-api-features");
        suite.BlockingGaps.Should().OnlyContain(gap => gap.State == MigrationEvidenceStates.Fail);
    }

    [Fact]
    public void Build_WithManualReviewEntry_ClassifiesAssistedAndBlocksAutomatedClaim()
    {
        var inventory = CreateInventory(
            "geoserver-rest",
            resources:
            [
                CreateResource(
                    "workspace:roads",
                    "Roads",
                    "partial",
                    "Field domains require review.",
                    ["Confirm domain mapping."]),
                CreateResource("workspace:parcels", "Parcels", "compatible", "Schema can be mapped.")
            ]);

        var suite = MigrationAcceptanceEvidenceBuilder.Build(
            "nightly-20260518",
            [
                new MigrationAcceptanceEvidenceInput
                {
                    Id = "geoserver",
                    Inventory = inventory,
                    ReadinessAttestation = CreatePassingAttestation()
                }
            ]);

        suite.Summary.OverallState.Should().Be(MigrationEvidenceStates.Unknown);
        suite.Summary.ManualReviewSourceCount.Should().Be(1);
        suite.Entries.Should().ContainSingle()
            .Which.Should().Match<MigrationAcceptanceEvidenceEntry>(entry =>
                entry.AutomationLevel == MigrationAutomationLevels.Assisted &&
                entry.ManualReviewCount == 1 &&
                entry.UnsupportedCount == 0);
        suite.BlockingGaps.Should().Contain(gap => gap.Id == "automation-level:geoserver")
            .Which.State.Should().Be(MigrationEvidenceStates.Unknown);
    }

    [Fact]
    public void Build_WithoutApplyOrPublishEvidence_KeepsSuiteUnknown()
    {
        var suite = MigrationAcceptanceEvidenceBuilder.Build(
            "nightly-20260518",
            [
                AutomatedInput(
                    "arcgis",
                    "arcgis-geoservices-rest",
                    includeApplyAndPublishEvidence: false)
            ]);

        suite.Summary.OverallState.Should().Be(MigrationEvidenceStates.Unknown);
        suite.Entries.Should().ContainSingle()
            .Which.Stages.Select(stage => (stage.Id, stage.State)).Should().Equal(
                (MigrationAcceptanceStageIds.Scan, MigrationEvidenceStates.Pass),
                (MigrationAcceptanceStageIds.Manifest, MigrationEvidenceStates.Pass),
                (MigrationAcceptanceStageIds.ApplyOrDryRun, MigrationEvidenceStates.Unknown),
                (MigrationAcceptanceStageIds.Publish, MigrationEvidenceStates.Unknown),
                (MigrationAcceptanceStageIds.Parity, MigrationEvidenceStates.Pass),
                (MigrationAcceptanceStageIds.Readiness, MigrationEvidenceStates.Pass));
        suite.BlockingGaps.Select(gap => gap.Id).Should().Contain(
            "stage:arcgis:apply-dry-run",
            "stage:arcgis:publish");
    }

    [Fact]
    public void Build_SanitizesEvidenceReferencesBeforeSerialization()
    {
        var suite = MigrationAcceptanceEvidenceBuilder.Build(
            "nightly-20260518",
            [
                AutomatedInput(
                    "arcgis",
                    "arcgis-geoservices-rest",
                    [
                        "https://user:password@example.test/evidence/inventory.json?token=secret#fragment",
                        " s3://migration-evidence/run/parity.json?X-Amz-Signature=secret "
                    ])
            ]);

        suite.Entries.Should().ContainSingle()
            .Which.EvidenceReferences.Should().Equal(
                "https://example.test/evidence/inventory.json",
                "s3://migration-evidence/run/parity.json");

        var json = JsonSerializer.Serialize(suite, JsonOptions);

        json.Should().Contain("\"artifactKind\":\"honua.migration.acceptance-evidence-suite\"");
        json.Should().Contain("\"evidenceReferences\"");
        json.Should().NotContain("password");
        json.Should().NotContain("token");
        json.Should().NotContain("X-Amz-Signature");
        json.Should().NotContain("#fragment");
    }

    [Fact]
    public void Build_WithMismatchedManifestSourceKind_Throws()
    {
        var input = AutomatedInput("arcgis", "arcgis-geoservices-rest");
        var otherManifest = MigrationManifestTranslator.Translate(CreateInventory(
            "geoserver-rest",
            [CreateResource("workspace:roads", "Roads", "compatible", "Schema can be mapped.")]));

        Action act = () => MigrationAcceptanceEvidenceBuilder.Build(
            "nightly-20260518",
            [input with { Manifest = otherManifest }]);

        act.Should().Throw<ArgumentException>()
            .WithMessage("Migration manifest source kind must match inventory source kind.");
    }

    [Fact]
    public void Build_WithMismatchedParitySourceIdentity_Throws()
    {
        var input = AutomatedInput("arcgis", "arcgis-geoservices-rest");
        var parity = MigrationParityEvidenceGenerator.Generate(
            input.Inventory,
            MigrationManifestTranslator.Translate(input.Inventory),
            CreatePassingAttestation()) with
        {
            Source = input.Inventory.Source with { BaseUrl = "https://example.test/other-source" }
        };

        Action act = () => MigrationAcceptanceEvidenceBuilder.Build(
            "nightly-20260518",
            [input with { ParityEvidence = parity }]);

        act.Should().Throw<ArgumentException>()
            .WithMessage("Migration parity evidence source identity must match inventory source identity.");
    }

    [Fact]
    public void Build_WithSuppliedManifestAndStaleParityManifestFlag_ReportsManifestAvailable()
    {
        var input = AutomatedInput("arcgis", "arcgis-geoservices-rest");
        var manifest = MigrationManifestTranslator.Translate(input.Inventory);
        var parity = MigrationParityEvidenceGenerator.Generate(
            input.Inventory,
            attestation: CreatePassingAttestation());

        parity.ManifestAvailable.Should().BeFalse();

        var suite = MigrationAcceptanceEvidenceBuilder.Build(
            "nightly-20260518",
            [input with { Manifest = manifest, ParityEvidence = parity }]);

        suite.Entries.Should().ContainSingle()
            .Which.ManifestAvailable.Should().BeTrue();
    }

    [Fact]
    public void Build_WithPassingCostEvidence_AttachesSuiteMetricsAndRedactsReferences()
    {
        var suite = MigrationAcceptanceEvidenceBuilder.Build(
            "nightly-20260518",
            [
                AutomatedInput(
                    "arcgis",
                    "arcgis-geoservices-rest",
                    performanceCost: CompletePerformanceCostEvidence())
            ],
            new MigrationAcceptanceEvidenceOptions
            {
                RequireCostEvidence = true,
                CostEvidenceThresholds = StrictCostContract()
            });

        suite.Summary.OverallState.Should().Be(MigrationEvidenceStates.Pass);
        suite.CostEvidence.Should().Match<MigrationAcceptanceCostEvidenceSummary>(cost =>
            cost.State == MigrationCostEvidenceStates.Pass &&
            cost.MeasuredSourceCount == 1 &&
            cost.MissingSourceCount == 0 &&
            cost.DurationMilliseconds == 2_000 &&
            cost.ScanDurationMilliseconds == 500 &&
            cost.ApplyDurationMilliseconds == 1_500 &&
            cost.FeatureThroughputPerSecond == 50 &&
            cost.SourceRequestCount == 3 &&
            cost.RetryCount == 1 &&
            cost.BytesRead == 10_240 &&
            cost.BytesWritten == 20_480 &&
            cost.ArtifactSizeBytes == 4_096 &&
            cost.ManualReviewRatio == 0);
        suite.BlockingGaps.Should().BeEmpty();

        var entryCost = suite.Entries.Should().ContainSingle().Which.CostEvidence;
        entryCost.Should().NotBeNull();

        entryCost!.Operations.Select(operation => (operation.Stage, operation.DurationMilliseconds)).Should().Equal(
            ("apply-dry-run", 1_500L),
            ("scan", 500L));
        entryCost.EvidenceReferences.Should().Equal(
            "https://example.test/evidence/cost.json",
            "s3://migration-evidence/issue-1033/cost.json");

        var json = JsonSerializer.Serialize(suite, JsonOptions);

        json.Should().Contain("\"costEvidence\"");
        json.Should().Contain("\"featureThroughputPerSecond\":50");
        json.Should().Contain("\"sourceRequestCount\":3");
        json.Should().Contain("\"artifactSizeBytes\":4096");
        json.Should().NotContain("password");
        json.Should().NotContain("token");
        json.Should().NotContain("X-Amz-Signature");
        json.Should().NotContain("#fragment");
    }

    [Fact]
    public void Build_WithRequiredCostEvidenceMissing_FailsCostEvidenceContract()
    {
        var suite = MigrationAcceptanceEvidenceBuilder.Build(
            "nightly-20260518",
            [AutomatedInput("arcgis", "arcgis-geoservices-rest")],
            new MigrationAcceptanceEvidenceOptions
            {
                RequireCostEvidence = true,
                CostEvidenceThresholds = StrictCostContract()
            });

        suite.Summary.OverallState.Should().Be(MigrationEvidenceStates.Fail);
        suite.CostEvidence.Should().Match<MigrationAcceptanceCostEvidenceSummary>(cost =>
            cost.State == MigrationCostEvidenceStates.Fail &&
            cost.MeasuredSourceCount == 0 &&
            cost.MissingSourceCount == 1);
        suite.BlockingGaps.Should().ContainSingle(gap => gap.Id == "cost-evidence:arcgis")
            .Which.State.Should().Be(MigrationEvidenceStates.Fail);
    }

    [Fact]
    public void Build_WithCostEvidenceWarning_BlocksReleaseCostClaimWithoutFailingFunctionalEvidence()
    {
        var suite = MigrationAcceptanceEvidenceBuilder.Build(
            "nightly-20260518",
            [
                AutomatedInput(
                    "arcgis",
                    "arcgis-geoservices-rest",
                    performanceCost: CompletePerformanceCostEvidence(manualReviewCount: 1, resourceCount: 4))
            ],
            new MigrationAcceptanceEvidenceOptions
            {
                RequireCostEvidence = true,
                CostEvidenceThresholds = StrictCostContract() with
                {
                    MaxManualReviewRatioWarn = 0.20,
                    MaxManualReviewRatioFail = 0.50
                }
            });

        suite.Summary.OverallState.Should().Be(MigrationEvidenceStates.Unknown);
        suite.CostEvidence.State.Should().Be(MigrationCostEvidenceStates.Warn);
        var entryCost = suite.Entries.Should().ContainSingle().Which.CostEvidence;
        entryCost.Should().NotBeNull();
        entryCost!.Findings.Should().ContainSingle(finding =>
            finding.Id == "threshold:manual-review-ratio" &&
            finding.State == MigrationCostEvidenceStates.Warn);
        suite.BlockingGaps.Should().ContainSingle(gap => gap.Id == "cost-evidence:arcgis")
            .Which.State.Should().Be(MigrationEvidenceStates.Unknown);
    }

    [Fact]
    public void Build_WithMissingRequiredCostMetrics_FailsCostEvidenceContract()
    {
        var suite = MigrationAcceptanceEvidenceBuilder.Build(
            "nightly-20260518",
            [
                AutomatedInput(
                    "arcgis",
                    "arcgis-geoservices-rest",
                    performanceCost: IncompletePerformanceCostEvidence())
            ],
            new MigrationAcceptanceEvidenceOptions
            {
                RequireCostEvidence = true,
                CostEvidenceThresholds = StrictCostContract()
            });

        suite.Summary.OverallState.Should().Be(MigrationEvidenceStates.Fail);
        suite.CostEvidence.State.Should().Be(MigrationCostEvidenceStates.Fail);
        var entryCost = suite.Entries.Should().ContainSingle().Which.CostEvidence;
        entryCost.Should().NotBeNull();
        entryCost!.Findings.Select(finding => finding.Id).Should().Contain(
            "missing-metric:apply-duration-milliseconds",
            "missing-metric:bytes",
            "missing-metric:artifact-size-bytes",
            "missing-metric:stage-duration:apply-dry-run");
        suite.BlockingGaps.Should().ContainSingle(gap => gap.Id == "cost-evidence:arcgis")
            .Which.State.Should().Be(MigrationEvidenceStates.Fail);
    }

    private static MigrationAcceptanceEvidenceInput AutomatedInput(
        string id,
        string sourceKind,
        string[]? evidenceReferences = null,
        bool includeApplyAndPublishEvidence = true,
        MigrationPerformanceCostEvidence? performanceCost = null)
        => new()
        {
            Id = id,
            Inventory = CreateInventory(
                sourceKind,
                resources:
                [
                    CreateResource($"{id}:roads", "Roads", "compatible", "Schema can be mapped.")
                ]),
            ReadinessAttestation = CreatePassingAttestation(),
            StageEvidence = includeApplyAndPublishEvidence ? PassingApplyAndPublishEvidence(id) : [],
            EvidenceReferences = evidenceReferences ?? [],
            PerformanceCost = performanceCost
        };

    private static MigrationAcceptanceCostEvidenceThresholds StrictCostContract()
        => new()
        {
            RequireMetricContract = true,
            RequiredDurationStages = [MigrationAcceptanceStageIds.Scan, MigrationAcceptanceStageIds.ApplyOrDryRun],
            MinFeatureThroughputPerSecondWarn = 20,
            MinFeatureThroughputPerSecondFail = 10,
            MaxSourceRequestCountWarn = 10,
            MaxSourceRequestCountFail = 20,
            MaxRetryCountWarn = 2,
            MaxRetryCountFail = 5,
            MaxArtifactSizeBytesWarn = 16_384,
            MaxArtifactSizeBytesFail = 65_536,
            MaxManualReviewRatioWarn = 0.10,
            MaxManualReviewRatioFail = 0.25
        };

    private static MigrationPerformanceCostEvidence CompletePerformanceCostEvidence(
        int manualReviewCount = 0,
        long resourceCount = 1)
        => new()
        {
            State = MigrationEvidenceStates.Pass,
            Summary = "Fixture scan and apply measurements passed configured thresholds.",
            MeasurementScope = "small deterministic fixture",
            Totals = new MigrationPerformanceCostTotals
            {
                DurationMilliseconds = 2_000,
                ResourceCount = resourceCount,
                FeatureCount = 100,
                BytesRead = 10_240,
                BytesWritten = 20_480,
                RetryCount = 1,
                SourceRequestCount = 3,
                ManualReviewCount = manualReviewCount,
                ArtifactSizeBytes = 4_096
            },
            EvidenceReferences =
            [
                "https://user:password@example.test/evidence/cost.json?token=secret#fragment",
                "s3://migration-evidence/issue-1033/cost.json?X-Amz-Signature=secret"
            ],
            Operations =
            [
                new MigrationPerformanceCostOperation
                {
                    Id = "scan",
                    Stage = MigrationAcceptanceStageIds.Scan,
                    State = MigrationEvidenceStates.Pass,
                    DurationMilliseconds = 500,
                    ResourceCount = resourceCount,
                    FeatureCount = 100,
                    BytesRead = 10_240,
                    RetryCount = 1,
                    SourceRequestCount = 2,
                    ArtifactSizeBytes = 2_048,
                    EvidenceReferences = ["https://example.test/evidence/scan.json?token=secret"]
                },
                new MigrationPerformanceCostOperation
                {
                    Id = "apply",
                    Stage = MigrationAcceptanceStageIds.ApplyOrDryRun,
                    State = MigrationEvidenceStates.Pass,
                    DurationMilliseconds = 1_500,
                    ResourceCount = resourceCount,
                    FeatureCount = 100,
                    BytesWritten = 20_480,
                    SourceRequestCount = 1,
                    ArtifactSizeBytes = 2_048,
                    EvidenceReferences = ["https://example.test/evidence/apply.json#fragment"]
                }
            ]
        };

    private static MigrationPerformanceCostEvidence IncompletePerformanceCostEvidence()
        => new()
        {
            State = MigrationEvidenceStates.Pass,
            Summary = "Fixture scan measurements are incomplete.",
            MeasurementScope = "small deterministic fixture",
            Totals = new MigrationPerformanceCostTotals
            {
                DurationMilliseconds = 500,
                ResourceCount = 1,
                FeatureCount = 100,
                RetryCount = 0,
                SourceRequestCount = 1,
                ManualReviewCount = 0
            },
            Operations =
            [
                new MigrationPerformanceCostOperation
                {
                    Id = "scan",
                    Stage = MigrationAcceptanceStageIds.Scan,
                    State = MigrationEvidenceStates.Pass,
                    DurationMilliseconds = 500,
                    ResourceCount = 1,
                    FeatureCount = 100,
                    SourceRequestCount = 1
                }
            ]
        };

    private static Honua.Core.Features.Import.Services.MigrationAcceptanceStageEvidenceInput[] PassingApplyAndPublishEvidence(string id)
        =>
        [
            new Honua.Core.Features.Import.Services.MigrationAcceptanceStageEvidenceInput
            {
                Id = MigrationAcceptanceStageIds.ApplyOrDryRun,
                State = MigrationEvidenceStates.Pass,
                Summary = "Deterministic dry-run evidence passed.",
                ArtifactKinds = ["honua.migration.apply-dry-run-evidence"],
                EvidenceReferences = [$"artifacts/{id}/apply-dry-run.json"]
            },
            new Honua.Core.Features.Import.Services.MigrationAcceptanceStageEvidenceInput
            {
                Id = MigrationAcceptanceStageIds.Publish,
                State = MigrationEvidenceStates.Pass,
                Summary = "Target publish evidence passed.",
                ArtifactKinds = ["honua.migration.publish-evidence"],
                EvidenceReferences = [$"artifacts/{id}/publish.json"]
            }
        ];

    private static MigrationReadinessAttestation CreatePassingAttestation()
        => new()
        {
            Items =
            [
                Attest("inventory-confirmed"),
                Attest("manifest-reviewed"),
                Attest("parity-report-reviewed"),
                Attest("rollback-plan-documented"),
                Attest("traffic-switch-planned")
            ]
        };

    private static MigrationReadinessAttestationItem Attest(string id)
        => new()
        {
            Id = id,
            State = MigrationEvidenceStates.Pass,
            Evidence = [$"{id} evidence accepted."],
            Owner = "platform"
        };

    private static MigrationSourceInventoryArtifact CreateInventory(
        string sourceKind,
        MigrationInventoryResource[] resources)
    {
        var container = new MigrationInventoryContainer
        {
            Id = "workspace",
            Kind = "workspace",
            Name = "workspace",
            Compatibility = Compatible("Workspace can be represented.")
        };

        return new MigrationSourceInventoryArtifact
        {
            SourceKind = sourceKind,
            Source = new MigrationSourceIdentity
            {
                DisplayName = $"{sourceKind} fixture",
                BaseUrl = $"https://example.test/{sourceKind}",
                Product = sourceKind,
                Version = "1.0"
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
                ContainerCount = 1,
                ResourceCount = resources.Length,
                CompatibleCount = resources.Count(resource => resource.Compatibility.Level == "compatible") + 1,
                PartiallyCompatibleCount = resources.Count(resource => resource.Compatibility.Level == "partial"),
                IncompatibleCount = resources.Count(resource => resource.Compatibility.Level == "incompatible")
            },
            OverallCompatibility = Compatible("Fixture compatibility is computed per item."),
            Containers = [container],
            Resources = resources
        };
    }

    private static MigrationInventoryResource CreateResource(
        string id,
        string name,
        string level,
        string reason,
        string[]? manualSteps = null)
        => new()
        {
            Id = id,
            ContainerId = "workspace",
            Kind = "layer",
            Name = name,
            GeometryType = "Point",
            Capabilities = ["Query", "Extract"],
            Fields =
            [
                new MigrationInventoryField
                {
                    Name = "name",
                    FieldType = "string",
                    Nullable = true
                }
            ],
            Compatibility = Assessment(level, reason, manualSteps)
        };

    private static MigrationCompatibilityAssessment Compatible(string reason)
        => Assessment("compatible", reason);

    private static MigrationCompatibilityAssessment Assessment(string level, string reason, string[]? manualSteps = null)
        => new()
        {
            Level = level,
            Reason = reason,
            ManualSteps = manualSteps ?? []
        };
}
