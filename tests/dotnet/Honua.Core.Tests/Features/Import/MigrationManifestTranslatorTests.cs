// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Services;

namespace Honua.Core.Tests.Features.Import;

public sealed class MigrationManifestTranslatorTests
{
    [Fact]
    public void Translate_WithUnsupportedResource_DoesNotEmitTargetAndRecordsUnsupported()
    {
        var inventory = CreateInventory(
            resources:
            [
                CreateResource("workspace:roads", "Roads Layer", "compatible", "Schema can be mapped.", capabilities: ["Query", "Extract"]),
                CreateResource(
                    "workspace:coverage",
                    "Raster Coverage",
                    "incompatible",
                    "Coverage layers are not translated by the feature manifest slice.",
                    code: "GEOSERVER_UNSUPPORTED_COVERAGE",
                    manualSteps: ["Publish through a raster migration path."])
            ],
            styles:
            [
                CreateStyle(
                    "workspace:roads-style",
                    "partial",
                    "External graphic requires manual review.",
                    code: "GEOSERVER_EXTERNAL_GRAPHIC")
            ]);

        var manifest = MigrationManifestTranslator.Translate(inventory, new MigrationManifestTranslationOptions
        {
            TargetServiceName = "Pilot Migration"
        });

        manifest.TargetResources.Should().ContainSingle(resource => resource.SourceResourceId == "workspace:roads")
            .Which.Should().Match<MigrationManifestTargetResource>(resource =>
                resource.Action == "publish" &&
                resource.TargetServiceName == "pilot-migration" &&
                resource.TargetResourceName == "roads-layer");
        manifest.TargetResources.Should().NotContain(resource => resource.SourceResourceId == "workspace:coverage");
        manifest.UnsupportedItems.Should().ContainSingle(item => item.SourceId == "workspace:coverage")
            .Which.Should().Match<MigrationManifestReviewItem>(item =>
                item.Code == "GEOSERVER_UNSUPPORTED_COVERAGE" &&
                item.Severity == "unsupported" &&
                item.ManualSteps.SequenceEqual(new[] { "Publish through a raster migration path." }));
        manifest.StyleActions.Should().ContainSingle(action => action.SourceStyleId == "workspace:roads-style")
            .Which.Action.Should().Be("manual-review");
        manifest.ManualReviewItems.Should().ContainSingle(item => item.SourceId == "workspace:roads-style")
            .Which.Code.Should().Be("GEOSERVER_EXTERNAL_GRAPHIC");
        manifest.Summary.Should().BeEquivalentTo(new MigrationManifestSummary
        {
            SourceResourceCount = 2,
            TargetResourceCount = 1,
            StyleActionCount = 1,
            ManualReviewCount = 1,
            UnsupportedCount = 1
        });
    }

    [Fact]
    public void Translate_WithPartialResource_EmitsManualReviewTargetAndReviewItem()
    {
        var inventory = CreateInventory(
            resources:
            [
                CreateResource(
                    "service:parcels",
                    "Parcels",
                    "partial",
                    "Coded-value domains require review.",
                    manualSteps: ["Review field domains before publishing."])
            ]);

        var manifest = MigrationManifestTranslator.Translate(inventory);

        manifest.TargetResources.Should().ContainSingle()
            .Which.Should().Match<MigrationManifestTargetResource>(resource =>
                resource.SourceResourceId == "service:parcels" &&
                resource.Action == "manual-review" &&
                resource.TargetServiceName == "migration-source" &&
                resource.TargetResourceName == "parcels");
        manifest.ManualReviewItems.Should().ContainSingle()
            .Which.Should().Match<MigrationManifestReviewItem>(item =>
                item.SourceId == "service:parcels" &&
                item.Code == "GEOSERVER_REST_LAYER_PARTIAL" &&
                item.Severity == "manual-review");
        manifest.UnsupportedItems.Should().BeEmpty();
    }

    private static MigrationSourceInventoryArtifact CreateInventory(
        MigrationInventoryResource[] resources,
        MigrationInventoryStyle[]? styles = null,
        MigrationExternalDependency[]? dependencies = null)
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
        var dependencyItems = dependencies ?? [];

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
                ExternalDependencyCount = dependencyItems.Length,
                CompatibleCount = resources.Count(resource => resource.Compatibility.Level == "compatible") +
                    styleItems.Count(style => style.Compatibility.Level == "compatible") +
                    dependencyItems.Count(dependency => dependency.Compatibility.Level == "compatible") +
                    containers.Length,
                PartiallyCompatibleCount = resources.Count(resource => resource.Compatibility.Level == "partial") +
                    styleItems.Count(style => style.Compatibility.Level == "partial") +
                    dependencyItems.Count(dependency => dependency.Compatibility.Level == "partial"),
                IncompatibleCount = resources.Count(resource => resource.Compatibility.Level == "incompatible") +
                    styleItems.Count(style => style.Compatibility.Level == "incompatible") +
                    dependencyItems.Count(dependency => dependency.Compatibility.Level == "incompatible")
            },
            OverallCompatibility = Compatible("Fixture compatibility is computed per item."),
            Containers = containers,
            Resources = resources,
            Styles = styleItems,
            ExternalDependencies = dependencyItems
        };
    }

    private static MigrationInventoryResource CreateResource(
        string id,
        string name,
        string level,
        string reason,
        string? code = null,
        string[]? capabilities = null,
        string[]? manualSteps = null)
        => new()
        {
            Id = id,
            ContainerId = "workspace",
            Kind = "layer",
            Name = name,
            GeometryType = "Point",
            Capabilities = capabilities ?? ["Query"],
            Fields =
            [
                new MigrationInventoryField
                {
                    Name = "name",
                    FieldType = "string",
                    Nullable = true
                }
            ],
            Compatibility = Assessment(level, reason, code, manualSteps)
        };

    private static MigrationInventoryStyle CreateStyle(
        string id,
        string level,
        string reason,
        string? code = null,
        string[]? manualSteps = null)
        => new()
        {
            Id = id,
            ContainerId = "workspace",
            Kind = "style",
            Name = id,
            Format = "sld",
            ResourceIds = ["workspace:roads"],
            Compatibility = Assessment(level, reason, code, manualSteps)
        };

    private static MigrationCompatibilityAssessment Compatible(string reason)
        => Assessment("compatible", reason);

    private static MigrationCompatibilityAssessment Assessment(
        string level,
        string reason,
        string? code = null,
        string[]? manualSteps = null)
        => new()
        {
            Level = level,
            Code = code,
            Reason = reason,
            ManualSteps = manualSteps ?? []
        };
}
