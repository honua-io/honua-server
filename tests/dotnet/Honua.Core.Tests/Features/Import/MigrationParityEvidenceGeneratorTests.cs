// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Services;

namespace Honua.Core.Tests.Features.Import;

public sealed class MigrationParityEvidenceGeneratorTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    [Fact]
    public void Generate_WithoutReadinessAttestation_LeavesChecklistUnknown()
    {
        var inventory = CreateInventory(
            resources:
            [
                CreateResource("workspace:roads", "Roads", "compatible", "Schema can be mapped."),
                CreateResource(
                    "workspace:parcels",
                    "Parcels",
                    "partial",
                    "Field domains require manual review.",
                    manualSteps: ["Confirm domain mapping."])
            ],
            styles:
            [
                CreateStyle("workspace:roads-style", "partial", "External graphic requires manual review.")
            ]);
        var manifest = MigrationManifestTranslator.Translate(inventory);

        var evidence = MigrationParityEvidenceGenerator.Generate(inventory, manifest);

        evidence.ManifestAvailable.Should().BeTrue();
        evidence.OverallState.Should().Be(MigrationEvidenceStates.Unknown);
        evidence.Sections.Should().ContainSingle(section => section.Id == "style")
            .Which.State.Should().Be(MigrationEvidenceStates.Unknown);
        evidence.Sections.Should().ContainSingle(section => section.Id == "data")
            .Which.Items.Should().ContainSingle(item => item.Id == "data:workspace:parcels")
            .Which.Should().Match<MigrationParityEvidenceItem>(item =>
                item.State == MigrationEvidenceStates.Unknown &&
                item.Remediation.SequenceEqual(new[] { "Confirm domain mapping." }));
        evidence.CutoverReadiness.Items.Should().OnlyContain(item =>
            item.Id == "known-gaps-accepted" || item.State == MigrationEvidenceStates.Unknown);
    }

    [Fact]
    public void Generate_WithPassingAttestationAndNoGaps_ReturnsPass()
    {
        var inventory = CreateInventory(
            resources:
            [
                CreateResource("workspace:roads", "Roads", "compatible", "Schema can be mapped.")
            ],
            styles:
            [
                CreateStyle("workspace:roads-style", "compatible", "Style can be imported.")
            ]);
        var manifest = MigrationManifestTranslator.Translate(inventory);

        var evidence = MigrationParityEvidenceGenerator.Generate(inventory, manifest, CreatePassingAttestation());

        evidence.OverallState.Should().Be(MigrationEvidenceStates.Pass);
        evidence.Sections.Should().OnlyContain(section => section.State == MigrationEvidenceStates.Pass);
        evidence.CutoverReadiness.State.Should().Be(MigrationEvidenceStates.Pass);
        evidence.CutoverReadiness.Items.Should().OnlyContain(item =>
            item.State == MigrationEvidenceStates.Pass || item.State == MigrationEvidenceStates.NotApplicable);
        evidence.CutoverReadiness.Items.Should().ContainSingle(item => item.Id == "known-gaps-accepted")
            .Which.State.Should().Be(MigrationEvidenceStates.NotApplicable);
    }

    [Fact]
    public void Generate_WithIncompatibleResource_ReturnsFailOverallState()
    {
        var inventory = CreateInventory(
            resources:
            [
                CreateResource(
                    "workspace:legacy",
                    "Legacy",
                    "incompatible",
                    "Source resource uses an unsupported renderer pipeline.")
            ]);

        var evidence = MigrationParityEvidenceGenerator.Generate(inventory);

        evidence.OverallState.Should().Be(MigrationEvidenceStates.Fail);
        evidence.Sections.Should().ContainSingle(section => section.Id == "capability")
            .Which.Should().Match<MigrationParityEvidenceSection>(section =>
                section.State == MigrationEvidenceStates.Fail &&
                section.Items.Single().State == MigrationEvidenceStates.Fail);
        evidence.Sections.Should().ContainSingle(section => section.Id == "data")
            .Which.Should().Match<MigrationParityEvidenceSection>(section =>
                section.State == MigrationEvidenceStates.Fail &&
                section.Items.Single().State == MigrationEvidenceStates.Fail);
        evidence.CutoverReadiness.Items.Should().ContainSingle(item => item.Id == "known-gaps-accepted")
            .Which.State.Should().Be(MigrationEvidenceStates.Unknown);
    }

    [Fact]
    public void Generate_WithoutManifest_DoesNotInferDataSuccess()
    {
        var inventory = CreateInventory(
            resources:
            [
                CreateResource("workspace:roads", "Roads", "compatible", "Schema can be mapped.")
            ]);

        var evidence = MigrationParityEvidenceGenerator.Generate(inventory);

        evidence.ManifestAvailable.Should().BeFalse();
        evidence.OverallState.Should().Be(MigrationEvidenceStates.Unknown);
        evidence.Sections.Should().ContainSingle(section => section.Id == "data")
            .Which.Should().Match<MigrationParityEvidenceSection>(section =>
                section.State == MigrationEvidenceStates.Unknown &&
                section.Items.Single().State == MigrationEvidenceStates.Unknown);
        evidence.CutoverReadiness.Items.Should().ContainSingle(item => item.Id == "manifest-reviewed")
            .Which.Should().Match<MigrationCutoverReadinessItem>(item =>
                item.State == MigrationEvidenceStates.Unknown &&
                item.Remediation.SequenceEqual(new[] { "Generate and review the migration manifest." }));
    }

    [Fact]
    public void Generate_WithPerformanceCostEvidence_AttachesStableSecretSafeMetrics()
    {
        var inventory = CreateInventory(
            resources:
            [
                CreateResource("workspace:roads", "Roads", "compatible", "Schema can be mapped.")
            ]);
        var manifest = MigrationManifestTranslator.Translate(inventory);
        var performanceCost = new MigrationPerformanceCostEvidence
        {
            State = "collected",
            Summary = "Fixture scan and manifest translation measurements captured.",
            MeasurementScope = "fixture import dry run",
            Totals = new MigrationPerformanceCostTotals
            {
                DurationMilliseconds = 1_250,
                ResourceCount = 1,
                FeatureCount = 42,
                BytesRead = 4_096,
                BytesWritten = 2_048,
                RetryCount = 1,
                ManualReviewCount = 0
            },
            EvidenceReferences =
            [
                " s3://migration-evidence/issue-1033/metrics.json?X-Amz-Signature=secret ",
                "https://user:password@example.test/evidence/run.json?token=secret#fragment"
            ],
            Operations =
            [
                new MigrationPerformanceCostOperation
                {
                    Id = "manifest",
                    Stage = "manifest",
                    State = MigrationEvidenceStates.Pass,
                    DurationMilliseconds = 50,
                    ResourceCount = 1,
                    EvidenceReferences =
                    [
                        "https://example.test/evidence/manifest.json?token=secret"
                    ]
                },
                new MigrationPerformanceCostOperation
                {
                    Id = "scan",
                    Stage = "scan",
                    State = MigrationEvidenceStates.Pass,
                    DurationMilliseconds = 1_200,
                    ResourceCount = 1,
                    FeatureCount = 42,
                    BytesRead = 4_096,
                    BytesWritten = 2_048,
                    RetryCount = 1,
                    ManualReviewCount = 0,
                    EvidenceReferences =
                    [
                        "https://example.test/evidence/scan.json#secret-fragment"
                    ]
                }
            ]
        };

        var evidence = MigrationParityEvidenceGenerator.Generate(
            inventory,
            manifest,
            CreatePassingAttestation(),
            performanceCost);

        evidence.OverallState.Should().Be(MigrationEvidenceStates.Pass);
        evidence.PerformanceCost.Should().NotBeNull();
        evidence.PerformanceCost!.State.Should().Be(MigrationEvidenceStates.Unknown);
        evidence.PerformanceCost.EvidenceReferences.Should().Equal(
            "https://example.test/evidence/run.json",
            "s3://migration-evidence/issue-1033/metrics.json");
        evidence.PerformanceCost.Operations.Select(operation => operation.Id)
            .Should().Equal("manifest", "scan");
        evidence.PerformanceCost.Operations.SelectMany(operation => operation.EvidenceReferences)
            .Should().OnlyContain(reference =>
                !reference.Contains('?') &&
                !reference.Contains('#') &&
                !reference.Contains("secret", StringComparison.OrdinalIgnoreCase) &&
                !reference.Contains("password", StringComparison.OrdinalIgnoreCase));

        var json = JsonSerializer.Serialize(evidence, JsonOptions);

        json.Should().Contain("\"performanceCost\"");
        json.Should().Contain("\"durationMilliseconds\":1250");
        json.Should().Contain("\"bytesRead\":4096");
        json.Should().Contain("\"retryCount\":1");
        json.Should().Contain("\"manualReviewCount\":0");
        json.Should().NotContain("token");
        json.Should().NotContain("password");
        json.Should().NotContain("X-Amz-Signature");
        json.Should().NotContain("#fragment");
    }

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
        MigrationInventoryResource[] resources,
        MigrationInventoryStyle[]? styles = null)
    {
        var containers = new[]
        {
            new MigrationInventoryContainer
            {
                Id = "workspace",
                Kind = "workspace",
                Name = "workspace",
                Compatibility = Compatible("Workspace can be represented.")
            }
        };
        var styleItems = styles ?? [];

        return new MigrationSourceInventoryArtifact
        {
            SourceKind = "geoserver-rest",
            Source = new MigrationSourceIdentity
            {
                DisplayName = "Migration Source",
                BaseUrl = "https://geoserver.example.test/geoserver",
                Product = "GeoServer",
                Version = "2.26.0"
            },
            AuthPosture = new MigrationInventoryAuthPosture
            {
                Mode = "basic",
                CredentialsSupplied = true,
                AccessConfirmed = true
            },
            ScanCompleteness = new MigrationInventoryCompleteness
            {
                Status = "complete"
            },
            Summary = new MigrationInventorySummary
            {
                ContainerCount = containers.Length,
                ResourceCount = resources.Length,
                StyleCount = styleItems.Length,
                CompatibleCount = resources.Count(resource => resource.Compatibility.Level == "compatible") +
                    styleItems.Count(style => style.Compatibility.Level == "compatible") +
                    containers.Length,
                PartiallyCompatibleCount = resources.Count(resource => resource.Compatibility.Level == "partial") +
                    styleItems.Count(style => style.Compatibility.Level == "partial"),
                IncompatibleCount = resources.Count(resource => resource.Compatibility.Level == "incompatible") +
                    styleItems.Count(style => style.Compatibility.Level == "incompatible")
            },
            OverallCompatibility = Compatible("Fixture compatibility is computed per item."),
            Containers = containers,
            Resources = resources,
            Styles = styleItems
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

    private static MigrationInventoryStyle CreateStyle(string id, string level, string reason)
        => new()
        {
            Id = id,
            ContainerId = "workspace",
            Kind = "style",
            Name = id,
            Format = "sld",
            ResourceIds = ["workspace:roads"],
            Compatibility = Assessment(level, reason)
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
