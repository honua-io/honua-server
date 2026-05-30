// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Tests.Features.Import;

/// <summary>
/// Slice 3 of OGC migration planning: covers the deterministic
/// <see cref="OgcWmsMigrationPlanner"/> and <see cref="OgcWmtsMigrationPlanner"/>
/// behavior — automated layer metadata, assisted SLD/SE style references with
/// diagnostics, automated trivial tile-matrix sets, manual-review non-trivial
/// tile-matrix sets, explicit unsupported render-only data classification, and
/// idempotent re-plan.
/// </summary>
public sealed class OgcWmsWmtsMigrationPlannerTests
{
    private const string WmsContainerId = "service:wms";
    private const string WmtsContainerId = "service:wmts";

    [Fact]
    public void WmsPlanner_ClassifiesLayerMetadataAsAutomated_AndRenderDataAsUnsupported()
    {
        var inventory = CreateWmsInventory();

        var result = OgcWmsMigrationPlanner.Plan(inventory, WmsContainerId);

        var metadataEntry = result.Entries.Should().ContainSingle(entry =>
                entry.Category == "layer-metadata" && entry.SourceId == "wms-layer:roads")
            .Subject;
        metadataEntry.AutomationStatus.Should().Be(MigrationFidelityAutomationStatuses.Automated);
        metadataEntry.Code.Should().Be(ImportCompatibilityCodes.OgcWmsMetadataAutomated);
        metadataEntry.ManualSteps.Should().BeEmpty();

        var renderDataEntry = result.Entries.Should().ContainSingle(entry =>
                entry.Category == "render-data" && entry.SourceId == "wms-layer:roads")
            .Subject;
        renderDataEntry.AutomationStatus.Should().Be(MigrationFidelityAutomationStatuses.Unsupported);
        renderDataEntry.Code.Should().Be(ImportCompatibilityCodes.OgcWmsRenderDataUnsupported);
        renderDataEntry.ManualSteps.Should().ContainSingle();
    }

    [Fact]
    public void WmsPlanner_EmitsAssistedStyleAndDiagnostic_AndDoesNotAutoImport()
    {
        var inventory = CreateWmsInventory();

        var result = OgcWmsMigrationPlanner.Plan(inventory, WmsContainerId);

        var styleEntry = result.Entries.Should().ContainSingle(entry =>
                entry.Category == "style" && entry.SourceId == "style:roads:default")
            .Subject;
        styleEntry.AutomationStatus.Should().Be(MigrationFidelityAutomationStatuses.Assisted);
        styleEntry.Code.Should().Be(ImportCompatibilityCodes.OgcRenderStyleAssisted);
        styleEntry.Metadata.Should().ContainKey("format").WhoseValue.Should().Be("sld");
        styleEntry.ManualSteps.Should().NotBeEmpty();

        result.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.SourceId == "style:roads:default" &&
            diagnostic.Code == ImportCompatibilityCodes.OgcRenderStyleAssisted &&
            diagnostic.Severity == "info");
    }

    [Fact]
    public void WmsPlanner_LayerMetadata_CarriesPreferredCompanionWfsSourceHint()
    {
        var inventory = CreateWmsInventory();

        var result = OgcWmsMigrationPlanner.Plan(inventory, WmsContainerId);

        var metadataEntry = result.Entries.Should().ContainSingle(entry =>
                entry.Category == "layer-metadata" && entry.SourceId == "wms-layer:roads")
            .Subject;
        metadataEntry.Metadata.Should().ContainKey("companionSourceKind").WhoseValue.Should().Be("ogc-wfs");
        metadataEntry.Metadata.Should().ContainKey("companionTypeNameHint").WhoseValue.Should().Be("roads");
        metadataEntry.Metadata.Should().ContainKey("companionCapabilitiesUrl")
            .WhoseValue.Should().Be("https://ogc-wms.example.test/service?service=WFS&request=GetCapabilities");

        result.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.SourceId == "wms-layer:roads" &&
            diagnostic.Code == ImportCompatibilityCodes.OgcWmsCompanionSourceHint &&
            diagnostic.Severity == "info");
    }

    [Fact]
    public void DeriveCompanionWfsCapabilitiesUrl_OnInvalidBaseUrl_ReturnsEmpty()
    {
        OgcWmsMigrationPlanner.DeriveCompanionWfsCapabilitiesUrl(string.Empty).Should().BeEmpty();
        OgcWmsMigrationPlanner.DeriveCompanionWfsCapabilitiesUrl("not a url").Should().BeEmpty();
    }

    [Fact]
    public void DeriveCompanionWfsCapabilitiesUrl_PreservesPathAndReplacesQuery()
    {
        var derived = OgcWmsMigrationPlanner.DeriveCompanionWfsCapabilitiesUrl(
            "https://gs.example.test/geoserver/wms?service=WMS&request=GetCapabilities&version=1.3.0");

        derived.Should().Be("https://gs.example.test/geoserver/wms?service=WFS&request=GetCapabilities");
    }

    [Fact]
    public void WmsPlanner_ClassifiesRenderEndpointAsManualReview()
    {
        var inventory = CreateWmsInventory();

        var result = OgcWmsMigrationPlanner.Plan(inventory, WmsContainerId);

        result.Entries.Should().ContainSingle(entry =>
                entry.Category == "render-endpoint" &&
                entry.SourceId == "endpoint:wms:get-map")
            .Which.AutomationStatus.Should().Be(MigrationFidelityAutomationStatuses.ManualReview);
    }

    [Fact]
    public void WmsPlanner_IsIdempotent_ReplanProducesEqualResult()
    {
        var inventory = CreateWmsInventory();

        var first = OgcWmsMigrationPlanner.Plan(inventory, WmsContainerId);
        var second = OgcWmsMigrationPlanner.Plan(inventory, WmsContainerId);

        second.Entries.Should().BeEquivalentTo(first.Entries, options => options.WithStrictOrdering());
        second.Diagnostics.Should().BeEquivalentTo(first.Diagnostics, options => options.WithStrictOrdering());
    }

    [Fact]
    public void WmsPlanner_OnNonWmsInventory_ReturnsEmptyResult()
    {
        var inventory = CreateWmtsInventory(trivialTileMatrix: true);

        var result = OgcWmsMigrationPlanner.Plan(inventory, WmtsContainerId);

        result.Entries.Should().BeEmpty();
        result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void WmtsPlanner_TrivialTileMatrixSet_IsClassifiedAutomated()
    {
        var inventory = CreateWmtsInventory(trivialTileMatrix: true);

        var result = OgcWmtsMigrationPlanner.Plan(inventory, WmtsContainerId);

        result.Entries.Should().ContainSingle(entry =>
                entry.Category == "tile-set" &&
                entry.SourceId == "tile-matrix-set:webmercatorquad")
            .Which.Should().Match<MigrationManifestPlanEntry>(entry =>
                entry.AutomationStatus == MigrationFidelityAutomationStatuses.Automated &&
                entry.Code == ImportCompatibilityCodes.OgcWmtsTileMatrixAutomated &&
                entry.ManualSteps.Length == 0);
    }

    [Fact]
    public void WmtsPlanner_NonTrivialTileMatrixSet_IsClassifiedManualReview()
    {
        var inventory = CreateWmtsInventory(trivialTileMatrix: false);

        var result = OgcWmtsMigrationPlanner.Plan(inventory, WmtsContainerId);

        var entry = result.Entries.Should().ContainSingle(item =>
                item.Category == "tile-set" &&
                item.SourceId == "tile-matrix-set:custom-grid")
            .Subject;
        entry.AutomationStatus.Should().Be(MigrationFidelityAutomationStatuses.ManualReview);
        entry.Code.Should().Be(ImportCompatibilityCodes.OgcWmtsTileMatrixManualReview);
        entry.ManualSteps.Should().NotBeEmpty();
    }

    [Fact]
    public void WmtsPlanner_ClassifiesTileDataAsUnsupported_AndMetadataAsAutomated()
    {
        var inventory = CreateWmtsInventory(trivialTileMatrix: true);

        var result = OgcWmtsMigrationPlanner.Plan(inventory, WmtsContainerId);

        result.Entries.Should().ContainSingle(entry =>
                entry.Category == "layer-metadata" && entry.SourceId == "wmts-layer:basemap")
            .Which.AutomationStatus.Should().Be(MigrationFidelityAutomationStatuses.Automated);
        result.Entries.Should().ContainSingle(entry =>
                entry.Category == "tile-data" && entry.SourceId == "wmts-layer:basemap")
            .Which.AutomationStatus.Should().Be(MigrationFidelityAutomationStatuses.Unsupported);
    }

    [Fact]
    public void WmtsPlanner_EmitsAssistedStyleDiagnostic()
    {
        var inventory = CreateWmtsInventory(trivialTileMatrix: true);

        var result = OgcWmtsMigrationPlanner.Plan(inventory, WmtsContainerId);

        result.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.SourceId == "style:basemap:default" &&
            diagnostic.Code == ImportCompatibilityCodes.OgcRenderStyleAssisted);
    }

    [Fact]
    public void WmtsPlanner_IsIdempotent_ReplanProducesEqualResult()
    {
        var inventory = CreateWmtsInventory(trivialTileMatrix: false);

        var first = OgcWmtsMigrationPlanner.Plan(inventory, WmtsContainerId);
        var second = OgcWmtsMigrationPlanner.Plan(inventory, WmtsContainerId);

        second.Entries.Should().BeEquivalentTo(first.Entries, options => options.WithStrictOrdering());
        second.Diagnostics.Should().BeEquivalentTo(first.Diagnostics, options => options.WithStrictOrdering());
    }

    [Fact]
    public void MigrationManifestTranslator_OnWmsInventory_AttachesPlanEntriesAndDiagnostics()
    {
        var inventory = CreateWmsInventory();

        var manifest = MigrationManifestTranslator.Translate(inventory);

        var plan = manifest.ServicePlans.Should().ContainSingle().Subject;
        plan.PlanEntries.Should().NotBeEmpty();
        plan.PlanEntries.Should().Contain(entry =>
            entry.Category == "layer-metadata" &&
            entry.AutomationStatus == MigrationFidelityAutomationStatuses.Automated);
        plan.PlanEntries.Should().Contain(entry =>
            entry.Category == "render-data" &&
            entry.AutomationStatus == MigrationFidelityAutomationStatuses.Unsupported);
        plan.PlanEntries.Should().Contain(entry =>
            entry.Category == "style" &&
            entry.AutomationStatus == MigrationFidelityAutomationStatuses.Assisted);
        plan.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == ImportCompatibilityCodes.OgcRenderStyleAssisted);
    }

    [Fact]
    public void MigrationManifestTranslator_OnWmtsInventory_AttachesPlanEntriesIncludingTileSet()
    {
        var inventory = CreateWmtsInventory(trivialTileMatrix: true);

        var manifest = MigrationManifestTranslator.Translate(inventory);

        var plan = manifest.ServicePlans.Should().ContainSingle().Subject;
        plan.PlanEntries.Should().Contain(entry =>
            entry.Category == "tile-set" &&
            entry.AutomationStatus == MigrationFidelityAutomationStatuses.Automated &&
            entry.Code == ImportCompatibilityCodes.OgcWmtsTileMatrixAutomated);
        plan.PlanEntries.Should().Contain(entry =>
            entry.Category == "tile-data" &&
            entry.AutomationStatus == MigrationFidelityAutomationStatuses.Unsupported);
    }

    private static MigrationSourceInventoryArtifact CreateWmsInventory()
    {
        var resources = new[]
        {
            new MigrationInventoryResource
            {
                Id = "wms-layer:roads",
                ContainerId = WmsContainerId,
                Kind = "render-layer",
                Name = "roads",
                Title = "Roads",
                Description = "Streets and roads",
                Capabilities = ["wms:GetCapabilities", "wms:GetMap", "wms:GetFeatureInfo"],
                SpatialReferences =
                [
                    new MigrationSpatialReferenceInfo { Role = "declared", SourceValue = "EPSG:4326" },
                    new MigrationSpatialReferenceInfo { Role = "supported", SourceValue = "EPSG:3857" }
                ],
                Compatibility = new MigrationCompatibilityAssessment
                {
                    Level = "incompatible",
                    Code = ImportCompatibilityCodes.OgcWmsRenderOnlySource,
                    Reason = "WMS exposes rendered map images.",
                    ManualSteps = ["Pair with WFS or coverage source."]
                }
            }
        };

        var styles = new[]
        {
            new MigrationInventoryStyle
            {
                Id = "style:roads:default",
                ContainerId = WmsContainerId,
                Kind = "wms-style",
                Name = "default",
                Format = "sld",
                ResourceIds = ["wms-layer:roads"],
                Compatibility = Partial("WMS style captured.", ImportCompatibilityCodes.OgcWmsRenderOnlySource)
            }
        };

        var dependencies = new[]
        {
            new MigrationExternalDependency
            {
                Id = "endpoint:wms:get-capabilities",
                ContainerId = WmsContainerId,
                Kind = "ogc-endpoint",
                Name = "WMS GetCapabilities",
                DependencyType = "capabilities",
                Address = "https://wms.example.test/wms?service=WMS&request=GetCapabilities",
                Metadata = new Dictionary<string, string>
                {
                    ["service"] = "WMS",
                    ["version"] = "1.3.0"
                },
                Compatibility = Partial("Captured for planning.", ImportCompatibilityCodes.OgcWmsRenderOnlySource)
            },
            new MigrationExternalDependency
            {
                Id = "endpoint:wms:get-map",
                ContainerId = WmsContainerId,
                Kind = "ogc-endpoint",
                Name = "WMS GetMap",
                DependencyType = "render",
                Address = "https://wms.example.test/wms",
                Metadata = new Dictionary<string, string>
                {
                    ["service"] = "WMS",
                    ["version"] = "1.3.0",
                    ["operation"] = "GetMap"
                },
                Compatibility = Partial("Captured for planning.", ImportCompatibilityCodes.OgcWmsRenderOnlySource)
            }
        };

        return BuildInventory(
            sourceKind: "ogc-wms",
            serviceType: "WMS",
            displayName: "Reference WMS",
            containerId: WmsContainerId,
            containerName: "WMS",
            resources: resources,
            styles: styles,
            dependencies: dependencies);
    }

    private static MigrationSourceInventoryArtifact CreateWmtsInventory(bool trivialTileMatrix)
    {
        var tileMatrixName = trivialTileMatrix ? "WebMercatorQuad" : "CustomGrid";
        var tileMatrixId = trivialTileMatrix ? "tile-matrix-set:webmercatorquad" : "tile-matrix-set:custom-grid";

        var resources = new[]
        {
            new MigrationInventoryResource
            {
                Id = "wmts-layer:basemap",
                ContainerId = WmtsContainerId,
                Kind = "tile-layer",
                Name = "basemap",
                Title = "Basemap",
                Capabilities = ["wmts:GetCapabilities", "wmts:GetTile"],
                ExternalDependencyIds = [tileMatrixId],
                Compatibility = new MigrationCompatibilityAssessment
                {
                    Level = "incompatible",
                    Code = ImportCompatibilityCodes.OgcWmtsTileOnlySource,
                    Reason = "WMTS exposes pre-rendered tiles.",
                    ManualSteps = ["Pair with WFS or coverage source."]
                }
            }
        };

        var styles = new[]
        {
            new MigrationInventoryStyle
            {
                Id = "style:basemap:default",
                ContainerId = WmtsContainerId,
                Kind = "wmts-style",
                Name = "default",
                Format = "WMTS",
                ResourceIds = ["wmts-layer:basemap"],
                Compatibility = Partial("WMTS style captured.", ImportCompatibilityCodes.OgcWmtsTileOnlySource)
            }
        };

        var dependencies = new[]
        {
            new MigrationExternalDependency
            {
                Id = tileMatrixId,
                ContainerId = WmtsContainerId,
                Kind = "tile-matrix-set",
                Name = tileMatrixName,
                DependencyType = "WMTS TileMatrixSet",
                Compatibility = Partial("Tile matrix set captured.", ImportCompatibilityCodes.OgcWmtsTileOnlySource)
            },
            new MigrationExternalDependency
            {
                Id = "endpoint:wmts:get-tile",
                ContainerId = WmtsContainerId,
                Kind = "ogc-endpoint",
                Name = "WMTS GetTile",
                DependencyType = "tile",
                Address = "https://wmts.example.test/wmts",
                Metadata = new Dictionary<string, string>
                {
                    ["service"] = "WMTS",
                    ["version"] = "1.0.0",
                    ["operation"] = "GetTile"
                },
                Compatibility = Partial("Captured for planning.", ImportCompatibilityCodes.OgcWmtsTileOnlySource)
            }
        };

        return BuildInventory(
            sourceKind: "ogc-wmts",
            serviceType: "WMTS",
            displayName: "Reference WMTS",
            containerId: WmtsContainerId,
            containerName: "WMTS",
            resources: resources,
            styles: styles,
            dependencies: dependencies);
    }

    private static MigrationSourceInventoryArtifact BuildInventory(
        string sourceKind,
        string serviceType,
        string displayName,
        string containerId,
        string containerName,
        MigrationInventoryResource[] resources,
        MigrationInventoryStyle[] styles,
        MigrationExternalDependency[] dependencies)
    {
        var containers = new[]
        {
            new MigrationInventoryContainer
            {
                Id = containerId,
                Kind = "ogc-service",
                Name = containerName,
                Title = displayName,
                IsDefault = true,
                Compatibility = Partial("Container captured for planning.", ImportCompatibilityCodes.ManualReview)
            }
        };

        return new MigrationSourceInventoryArtifact
        {
            SourceKind = sourceKind,
            Source = new MigrationSourceIdentity
            {
                DisplayName = displayName,
                BaseUrl = $"https://{sourceKind}.example.test/service",
                Product = $"OGC {serviceType}",
                Version = "1.3.0",
                ServiceType = serviceType
            },
            AuthPosture = new MigrationInventoryAuthPosture
            {
                Mode = "anonymous",
                CredentialsSupplied = false,
                AccessConfirmed = true
            },
            ScanCompleteness = new MigrationInventoryCompleteness { Status = "complete" },
            Summary = new MigrationInventorySummary
            {
                ContainerCount = containers.Length,
                ResourceCount = resources.Length,
                StyleCount = styles.Length,
                ExternalDependencyCount = dependencies.Length
            },
            OverallCompatibility = Partial("Render-only source captured.", ImportCompatibilityCodes.ManualReview),
            Containers = containers,
            Resources = resources,
            Styles = styles,
            ExternalDependencies = dependencies
        };
    }

    private static MigrationCompatibilityAssessment Partial(string reason, string code)
        => new()
        {
            Level = "partial",
            Code = code,
            Reason = reason
        };
}
