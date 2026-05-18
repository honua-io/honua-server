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
                resource.TargetResourceId == "target:resource:pilot-migration:roads-layer" &&
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
        manifest.IdentityRemaps.Should().Contain(remap => remap.SourceId == "workspace:roads" &&
            remap.TargetId == "target:resource:pilot-migration:roads-layer" &&
            remap.Action == "publish");
        manifest.IdentityRemaps.Should().Contain(remap => remap.SourceId == "workspace:roads-style" &&
            remap.TargetId == "target:style:pilot-migration:workspace-roads-style" &&
            remap.Action == "manual-review");
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
                resource.TargetResourceId == "target:resource:migration-source:parcels" &&
                resource.TargetServiceName == "migration-source" &&
                resource.TargetResourceName == "parcels");
        manifest.IdentityRemaps.Should().ContainSingle()
            .Which.Should().Match<MigrationManifestIdentityRemap>(remap =>
                remap.SourceId == "service:parcels" &&
                remap.TargetId == "target:resource:migration-source:parcels" &&
                remap.Action == "manual-review");
        manifest.ManualReviewItems.Should().ContainSingle()
            .Which.Should().Match<MigrationManifestReviewItem>(item =>
                item.SourceId == "service:parcels" &&
                item.Code == "GEOSERVER_REST_LAYER_PARTIAL" &&
                item.Severity == "manual-review");
        manifest.UnsupportedItems.Should().BeEmpty();
    }

    [Fact]
    public void Translate_WithOgcWfsFeatureType_EmitsFeatureImportPlan()
    {
        var inventory = CreateInventory(
            sourceKind: "ogc-wfs",
            serviceType: "WFS",
            containerKind: "ogc-service",
            resources:
            [
                CreateResource(
                    "feature-type:roads",
                    "topp:roads",
                    "compatible",
                    "WFS feature type metadata can be represented.",
                    code: ImportCompatibilityCodes.OgcWfsFeatureSource,
                    kind: "feature-type",
                    capabilities: ["wfs:GetCapabilities", "wfs:DescribeFeatureType", "wfs:GetFeature"])
            ],
            dependencies:
            [
                CreateDependency(
                    "endpoint:wfs:get-capabilities",
                    "ogc-endpoint",
                    "WFS GetCapabilities",
                    "compatible",
                    "Capabilities endpoint captured.",
                    ImportCompatibilityCodes.OgcWfsFeatureSource,
                    metadata: new Dictionary<string, string>
                    {
                        ["service"] = "WFS"
                    })
            ]);

        var manifest = MigrationManifestTranslator.Translate(inventory, new MigrationManifestTranslationOptions
        {
            TargetServiceName = "Reference WFS"
        });

        manifest.TargetResources.Should().ContainSingle()
            .Which.Should().Match<MigrationManifestTargetResource>(resource =>
                resource.SourceResourceId == "feature-type:roads" &&
                resource.Action == "publish" &&
                resource.MigrationMode == "feature-import" &&
                resource.SourceProtocol == "WFS" &&
                resource.TargetServiceName == "reference-wfs" &&
                resource.TargetResourceName == "topp-roads" &&
                resource.ExternalDependencyIds.SequenceEqual(new[] { "endpoint:wfs:get-capabilities" }));
        manifest.ServicePlans.Should().BeEmpty();
        manifest.Summary.ServicePlanCount.Should().Be(0);
    }

    [Fact]
    public void Translate_WithOgcWmsRenderOnlyInventory_EmitsManualReviewServicePlan()
    {
        var inventory = CreateInventory(
            sourceKind: "ogc-wms",
            serviceType: "WMS",
            containerKind: "ogc-service",
            resources:
            [
                CreateResource(
                    "wms-layer:roads",
                    "roads",
                    "incompatible",
                    "WMS exposes rendered map images and cannot supply automated feature data-copy by itself.",
                    code: ImportCompatibilityCodes.OgcWmsRenderOnlySource,
                    kind: "render-layer",
                    capabilities: ["wms:GetCapabilities", "wms:GetMap", "wms:GetFeatureInfo"],
                    manualSteps: ["Pair this WMS layer with a WFS, coverage, database, or file source before planning data import."])
            ],
            styles:
            [
                CreateStyle(
                    "style:roads:default",
                    "partial",
                    "WMS style metadata was captured for manual render-service migration planning.",
                    code: ImportCompatibilityCodes.OgcWmsRenderOnlySource,
                    manualSteps: ["Review WMS style semantics and recreate equivalent Honua styles where required."])
            ],
            dependencies:
            [
                CreateDependency(
                    "endpoint:wms:get-capabilities",
                    "ogc-endpoint",
                    "WMS GetCapabilities",
                    "partial",
                    "WMS capabilities endpoint was captured for manual service migration planning.",
                    ImportCompatibilityCodes.OgcWmsRenderOnlySource)
            ]);

        var manifest = MigrationManifestTranslator.Translate(inventory);

        manifest.TargetResources.Should().BeEmpty();
        manifest.ServicePlans.Should().ContainSingle()
            .Which.Should().Match<MigrationManifestServicePlan>(plan =>
                plan.SourceContainerId == "workspace" &&
                plan.SourceKind == "ogc-service" &&
                plan.Action == "manual-review" &&
                plan.ServiceType == "WMS" &&
                plan.ResourceIds.SequenceEqual(new[] { "wms-layer:roads" }) &&
                plan.StyleIds.SequenceEqual(new[] { "style:roads:default" }) &&
                plan.ExternalDependencyIds.SequenceEqual(new[] { "endpoint:wms:get-capabilities" }));
        manifest.UnsupportedItems.Should().ContainSingle(item => item.SourceId == "wms-layer:roads")
            .Which.Severity.Should().Be("unsupported");
        manifest.ManualReviewItems.Should().Contain(item => item.SourceId == "style:roads:default");
        manifest.ManualReviewItems.Should().Contain(item => item.SourceId == "endpoint:wms:get-capabilities");
        manifest.Summary.Should().BeEquivalentTo(new MigrationManifestSummary
        {
            SourceResourceCount = 1,
            TargetResourceCount = 0,
            StyleActionCount = 1,
            ServicePlanCount = 1,
            ManualReviewCount = 2,
            UnsupportedCount = 1
        });
    }

    [Fact]
    public void Translate_WithOgcWmtsInventory_IncludesTileMatrixDependenciesInServicePlan()
    {
        var inventory = CreateInventory(
            sourceKind: "ogc-wmts",
            serviceType: "WMTS",
            containerKind: "ogc-service",
            resources:
            [
                CreateResource(
                    "wmts-layer:roads",
                    "roads",
                    "incompatible",
                    "WMTS exposes pre-rendered tiles and cannot supply automated feature data-copy by itself.",
                    code: ImportCompatibilityCodes.OgcWmtsTileOnlySource,
                    kind: "tile-layer",
                    capabilities: ["wmts:GetCapabilities", "wmts:GetTile"],
                    manualSteps: ["Pair this WMTS layer with a WFS, coverage, database, or file source before planning data import."])
            ],
            dependencies:
            [
                CreateDependency(
                    "endpoint:wmts:get-capabilities",
                    "ogc-endpoint",
                    "WMTS GetCapabilities",
                    "partial",
                    "WMTS capabilities endpoint was captured for manual service migration planning.",
                    ImportCompatibilityCodes.OgcWmtsTileOnlySource),
                CreateDependency(
                    "tile-matrix-set:webmercator",
                    "tile-matrix-set",
                    "WebMercatorQuad",
                    "partial",
                    "WMTS tile matrix set metadata was captured for manual service migration planning.",
                    ImportCompatibilityCodes.OgcWmtsTileOnlySource)
            ]);

        var manifest = MigrationManifestTranslator.Translate(inventory);

        manifest.ServicePlans.Should().ContainSingle()
            .Which.ExternalDependencyIds.Should().Equal(
                "endpoint:wmts:get-capabilities",
                "tile-matrix-set:webmercator");
        manifest.TargetResources.Should().BeEmpty();
        manifest.UnsupportedItems.Should().ContainSingle(item => item.SourceId == "wmts-layer:roads");
        manifest.Summary.ServicePlanCount.Should().Be(1);
    }

    private static MigrationSourceInventoryArtifact CreateInventory(
        MigrationInventoryResource[] resources,
        string sourceKind = "geoserver-rest",
        string? serviceType = null,
        string containerKind = "workspace",
        MigrationInventoryStyle[]? styles = null,
        MigrationExternalDependency[]? dependencies = null)
    {
        var containers = new[]
        {
            new MigrationInventoryContainer
            {
                Id = "workspace",
                Kind = containerKind,
                Name = "workspace",
                Compatibility = Compatible("Workspace can be represented.")
            }
        };

        var styleItems = styles ?? [];
        var dependencyItems = dependencies ?? [];

        return new MigrationSourceInventoryArtifact
        {
            SourceKind = sourceKind,
            Source = new MigrationSourceIdentity
            {
                DisplayName = "Migration Source",
                BaseUrl = "https://geoserver.example.test/geoserver",
                Product = "GeoServer",
                Version = "2.26.0",
                ServiceType = serviceType
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
        string kind = "layer",
        string[]? capabilities = null,
        string[]? manualSteps = null,
        string[]? externalDependencyIds = null)
        => new()
        {
            Id = id,
            ContainerId = "workspace",
            Kind = kind,
            Name = name,
            GeometryType = "Point",
            Capabilities = capabilities ?? ["Query"],
            ExternalDependencyIds = externalDependencyIds ?? [],
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

    private static MigrationExternalDependency CreateDependency(
        string id,
        string kind,
        string name,
        string level,
        string reason,
        string? code = null,
        string[]? manualSteps = null,
        Dictionary<string, string>? metadata = null)
        => new()
        {
            Id = id,
            ContainerId = "workspace",
            Kind = kind,
            Name = name,
            Metadata = metadata ?? [],
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
