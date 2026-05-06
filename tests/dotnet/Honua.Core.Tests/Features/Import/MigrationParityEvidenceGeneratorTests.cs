// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Services;

namespace Honua.Core.Tests.Features.Import;

public sealed class MigrationParityEvidenceGeneratorTests
{
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
