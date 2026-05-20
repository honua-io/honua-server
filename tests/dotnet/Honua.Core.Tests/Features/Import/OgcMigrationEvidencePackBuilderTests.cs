// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Services;

namespace Honua.Core.Tests.Features.Import;

/// <summary>
/// Slice 5 of issue #1016: deterministic OGC migration evidence-pack
/// generation. The pack bundles the slice 1-4 outputs (inventory, WFS data
/// import, WMS/WMTS render plans, tile-cache export) per source so reviewers
/// can audit every stage of a classic OGC migration from one artifact.
/// </summary>
public sealed class OgcMigrationEvidencePackBuilderTests
{
    [Fact]
    public void Build_ProducesDeterministicFingerprint_ForIdenticalInputs()
    {
        var inputs = BuildInputs();

        var first = OgcMigrationEvidencePackBuilder.Build(inputs, new OgcMigrationEvidencePackBuilderOptions
        {
            RunId = "nightly-20260519",
            Generator = "test/1.0",
            GeneratedAt = DateTimeOffset.Parse("2026-05-19T00:00:00Z")
        });

        var second = OgcMigrationEvidencePackBuilder.Build(inputs, new OgcMigrationEvidencePackBuilderOptions
        {
            // Different run-time metadata; fingerprint must be unaffected.
            RunId = "nightly-20260601",
            Generator = "test/2.0",
            GeneratedAt = DateTimeOffset.Parse("2026-06-01T12:34:56Z")
        });

        first.BundleFingerprint.Should().StartWith("sha256:");
        first.BundleFingerprint.Should().Be(second.BundleFingerprint,
            "fingerprint must cover the bundle only — wall-clock and generator labels are excluded so nightly re-runs stay byte-identical.");

        first.RunId.Should().Be("nightly-20260519");
        second.RunId.Should().Be("nightly-20260601");
    }

    [Fact]
    public void Build_FingerprintChanges_WhenWfsFeatureCountChanges()
    {
        var inputs = BuildInputs();
        var mutated = inputs with
        {
            WfsImport = inputs.WfsImport! with
            {
                FeaturesCopied = inputs.WfsImport!.FeaturesCopied + 1
            }
        };

        var baseline = OgcMigrationEvidencePackBuilder.Build(inputs);
        var changed = OgcMigrationEvidencePackBuilder.Build(mutated);

        baseline.BundleFingerprint.Should().NotBe(changed.BundleFingerprint,
            "any change to the bundle inputs must propagate to the fingerprint.");
    }

    [Fact]
    public void Build_FingerprintChanges_WhenRenderPlanEntryChanges()
    {
        var inputs = BuildInputs();
        var mutated = inputs with
        {
            WmsPlan = new OgcRenderMigrationPlanResult(
                inputs.WmsPlan!.Value.Entries
                    .Select(entry => entry.Id == "plan:wms:metadata:roads"
                        ? entry with { AutomationStatus = MigrationFidelityAutomationStatuses.ManualReview }
                        : entry)
                    .ToArray(),
                inputs.WmsPlan.Value.Diagnostics)
        };

        var baseline = OgcMigrationEvidencePackBuilder.Build(inputs);
        var changed = OgcMigrationEvidencePackBuilder.Build(mutated);

        baseline.BundleFingerprint.Should().NotBe(changed.BundleFingerprint);
    }

    [Fact]
    public void Build_FingerprintChanges_WhenTileCacheTilesPersistedChanges()
    {
        var inputs = BuildInputs();
        var mutated = inputs with
        {
            TileCacheExport = inputs.TileCacheExport! with
            {
                TilesPersisted = inputs.TileCacheExport!.TilesPersisted + 1
            }
        };

        var baseline = OgcMigrationEvidencePackBuilder.Build(inputs);
        var changed = OgcMigrationEvidencePackBuilder.Build(mutated);

        baseline.BundleFingerprint.Should().NotBe(changed.BundleFingerprint);
    }

    [Fact]
    public void Build_SummaryReflectsAggregateCounts_FromAllStages()
    {
        var pack = OgcMigrationEvidencePackBuilder.Build(BuildInputs());

        var summary = pack.Bundle.Summary;

        summary.InventoryContainerCount.Should().Be(2);
        summary.InventoryResourceCount.Should().Be(3);
        summary.InventoryStyleCount.Should().Be(1);

        summary.WfsImportExecuted.Should().BeTrue();
        summary.WfsFeatureTypesImported.Should().Be(2);
        summary.WfsFeatureTypesSkipped.Should().Be(1);
        summary.WfsFeaturesCopied.Should().Be(150);

        summary.WmsPlanEntryCount.Should().Be(2);
        summary.WmtsPlanEntryCount.Should().Be(2);
        summary.RenderManualReviewOrUnsupportedCount.Should().Be(2,
            "the fixture WMS plan contains one unsupported render-data entry " +
            "and the WMTS plan contains one manual-review tile-set entry.");

        summary.TileCacheExportExecuted.Should().BeTrue();
        summary.TileCacheTileSetsExported.Should().Be(1);
        summary.TileCacheTileSetsSkipped.Should().Be(1);
        summary.TileCacheTilesPersisted.Should().Be(5);
        summary.TileCacheTilesFailed.Should().Be(0);
    }

    [Fact]
    public void Build_RenderStages_OrderEntriesAndDiagnosticsDeterministically()
    {
        var pack = OgcMigrationEvidencePackBuilder.Build(BuildInputs());

        pack.Bundle.WmsPlan.ServiceKind.Should().Be("ogc-wms");
        pack.Bundle.WmtsPlan.ServiceKind.Should().Be("ogc-wmts");

        pack.Bundle.WmsPlan.Entries
            .Select(e => e.Id)
            .Should().BeInAscendingOrder(StringComparer.Ordinal);
        pack.Bundle.WmtsPlan.Entries
            .Select(e => e.Id)
            .Should().BeInAscendingOrder(StringComparer.Ordinal);
        pack.Bundle.WmsPlan.Diagnostics
            .Select(d => $"{d.SourceId}|{d.Code}")
            .Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Fact]
    public void Build_RenderStage_IsEmpty_WhenNoPlanProvided()
    {
        var inputs = BuildInputs() with { WmsPlan = null, WmtsPlan = null };

        var pack = OgcMigrationEvidencePackBuilder.Build(inputs);

        pack.Bundle.WmsPlan.EntryCount.Should().Be(0);
        pack.Bundle.WmsPlan.Entries.Should().BeEmpty();
        pack.Bundle.WmtsPlan.EntryCount.Should().Be(0);
        pack.Bundle.Summary.WmsPlanEntryCount.Should().Be(0);
        pack.Bundle.Summary.RenderManualReviewOrUnsupportedCount.Should().Be(0);
    }

    [Fact]
    public void Build_WfsAndTileCache_AreNull_WhenStagesAreSkipped()
    {
        var inputs = BuildInputs() with { WfsImport = null, TileCacheExport = null };

        var pack = OgcMigrationEvidencePackBuilder.Build(inputs);

        pack.Bundle.WfsImport.Should().BeNull();
        pack.Bundle.TileCacheExport.Should().BeNull();
        pack.Bundle.Summary.WfsImportExecuted.Should().BeFalse();
        pack.Bundle.Summary.TileCacheExportExecuted.Should().BeFalse();
        pack.Bundle.Summary.WfsFeaturesCopied.Should().Be(0);
        pack.Bundle.Summary.TileCacheTilesPersisted.Should().Be(0);
    }

    [Fact]
    public void Build_RedactsCredentials_FromEveryEmbeddedSourceUrl()
    {
        const string secretUrl = "https://admin:hunter2@ogc.example.com:8443/services/wfs?token=topsecret#frag";

        var inputs = BuildInputs();
        var withSecretUrls = inputs with
        {
            Inventory = inputs.Inventory with
            {
                Source = inputs.Inventory.Source with { BaseUrl = secretUrl }
            },
            WfsImport = inputs.WfsImport! with
            {
                SourceServiceUrl = secretUrl,
                Manifest = inputs.WfsImport!.Manifest with
                {
                    Source = inputs.WfsImport.Manifest.Source with { BaseUrl = secretUrl }
                }
            },
            TileCacheExport = inputs.TileCacheExport! with
            {
                SourceServiceUrl = secretUrl,
                Manifest = inputs.TileCacheExport!.Manifest with
                {
                    Source = inputs.TileCacheExport.Manifest.Source with { BaseUrl = secretUrl }
                }
            }
        };

        var pack = OgcMigrationEvidencePackBuilder.Build(withSecretUrls);

        // Spot-check the redacted shape on the top-level source identity.
        pack.Bundle.Source.BaseUrl.Should().Be("https://ogc.example.com:8443/services/wfs");
        pack.Bundle.Inventory.Source.BaseUrl.Should().Be("https://ogc.example.com:8443/services/wfs");
        pack.Bundle.WfsImport!.SourceServiceUrl.Should().Be("https://ogc.example.com:8443/services/wfs");
        pack.Bundle.WfsImport.Manifest.Source.BaseUrl.Should().Be("https://ogc.example.com:8443/services/wfs");
        pack.Bundle.TileCacheExport!.SourceServiceUrl.Should().Be("https://ogc.example.com:8443/services/wfs");
        pack.Bundle.TileCacheExport.Manifest.Source.BaseUrl.Should().Be("https://ogc.example.com:8443/services/wfs");

        // Serialize the entire pack and assert no credential leaked anywhere.
        var json = JsonSerializer.Serialize(
            pack,
            OgcMigrationEvidencePackJsonContext.Default.OgcMigrationEvidencePackArtifact);
        json.Should().NotContain("hunter2");
        json.Should().NotContain("topsecret");
        json.Should().NotContain("admin:hunter2");
    }

    [Fact]
    public void Artifact_Shape_HasStableTopLevelFields()
    {
        // Schema-stability guard: surface any accidental addition/rename of the
        // evidence-pack contract so reviewers update consumers (admin UI, SDK
        // orchestration in slice 6) deliberately.
        var pack = OgcMigrationEvidencePackBuilder.Build(BuildInputs());
        var json = JsonSerializer.Serialize(
            pack,
            OgcMigrationEvidencePackJsonContext.Default.OgcMigrationEvidencePackArtifact);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("artifactKind").GetString().Should().Be("honua.migration.ogc.evidence-pack");
        root.GetProperty("artifactVersion").GetString().Should().Be("1.0");
        root.GetProperty("runId").ValueKind.Should().Be(JsonValueKind.String);
        root.GetProperty("generator").ValueKind.Should().Be(JsonValueKind.String);
        root.GetProperty("generatedAt").ValueKind.Should().Be(JsonValueKind.String);
        root.GetProperty("bundleFingerprint").GetString().Should().StartWith("sha256:");

        var bundle = root.GetProperty("bundle");
        bundle.GetProperty("sourceKind").ValueKind.Should().Be(JsonValueKind.String);
        bundle.GetProperty("source").ValueKind.Should().Be(JsonValueKind.Object);
        bundle.GetProperty("summary").ValueKind.Should().Be(JsonValueKind.Object);
        bundle.GetProperty("inventory").ValueKind.Should().Be(JsonValueKind.Object);
        bundle.GetProperty("wfsImport").ValueKind.Should().Be(JsonValueKind.Object);
        bundle.GetProperty("wmsPlan").ValueKind.Should().Be(JsonValueKind.Object);
        bundle.GetProperty("wmtsPlan").ValueKind.Should().Be(JsonValueKind.Object);
        bundle.GetProperty("tileCacheExport").ValueKind.Should().Be(JsonValueKind.Object);

        var summary = bundle.GetProperty("summary");
        summary.GetProperty("inventoryContainerCount").ValueKind.Should().Be(JsonValueKind.Number);
        summary.GetProperty("inventoryResourceCount").ValueKind.Should().Be(JsonValueKind.Number);
        summary.GetProperty("inventoryStyleCount").ValueKind.Should().Be(JsonValueKind.Number);
        summary.GetProperty("wfsImportExecuted").GetBoolean().Should().BeTrue();
        summary.GetProperty("wfsFeatureTypesImported").ValueKind.Should().Be(JsonValueKind.Number);
        summary.GetProperty("wfsFeatureTypesSkipped").ValueKind.Should().Be(JsonValueKind.Number);
        summary.GetProperty("wfsFeaturesCopied").ValueKind.Should().Be(JsonValueKind.Number);
        summary.GetProperty("wmsPlanEntryCount").ValueKind.Should().Be(JsonValueKind.Number);
        summary.GetProperty("wmtsPlanEntryCount").ValueKind.Should().Be(JsonValueKind.Number);
        summary.GetProperty("renderManualReviewOrUnsupportedCount").ValueKind.Should().Be(JsonValueKind.Number);
        summary.GetProperty("tileCacheExportExecuted").GetBoolean().Should().BeTrue();
        summary.GetProperty("tileCacheTileSetsExported").ValueKind.Should().Be(JsonValueKind.Number);
        summary.GetProperty("tileCacheTileSetsSkipped").ValueKind.Should().Be(JsonValueKind.Number);
        summary.GetProperty("tileCacheTilesPersisted").ValueKind.Should().Be(JsonValueKind.Number);
        summary.GetProperty("tileCacheTilesFailed").ValueKind.Should().Be(JsonValueKind.Number);

        var wmsPlan = bundle.GetProperty("wmsPlan");
        wmsPlan.GetProperty("serviceKind").GetString().Should().Be("ogc-wms");
        wmsPlan.GetProperty("entryCount").ValueKind.Should().Be(JsonValueKind.Number);
        wmsPlan.GetProperty("automatedCount").ValueKind.Should().Be(JsonValueKind.Number);
        wmsPlan.GetProperty("assistedCount").ValueKind.Should().Be(JsonValueKind.Number);
        wmsPlan.GetProperty("manualReviewCount").ValueKind.Should().Be(JsonValueKind.Number);
        wmsPlan.GetProperty("unsupportedCount").ValueKind.Should().Be(JsonValueKind.Number);
        wmsPlan.GetProperty("entries").ValueKind.Should().Be(JsonValueKind.Array);
        wmsPlan.GetProperty("diagnostics").ValueKind.Should().Be(JsonValueKind.Array);
    }

    private static OgcMigrationEvidencePackInputs BuildInputs()
    {
        var source = new MigrationSourceIdentity
        {
            DisplayName = "OGC Sample",
            BaseUrl = "https://ogc.example.com/services/wfs",
            Product = "GeoServer",
            Version = "2.24.0",
            ServiceType = "OGC"
        };

        var inventory = new MigrationSourceInventoryArtifact
        {
            SourceKind = "ogc-wfs",
            Source = source,
            AuthPosture = new MigrationInventoryAuthPosture
            {
                Mode = "anonymous",
                CredentialsSupplied = false,
                AccessConfirmed = true
            },
            ScanCompleteness = new MigrationInventoryCompleteness { Status = "complete" },
            Summary = new MigrationInventorySummary
            {
                ContainerCount = 2,
                ResourceCount = 3,
                StyleCount = 1
            },
            OverallCompatibility = new MigrationCompatibilityAssessment
            {
                Level = "compatible",
                Reason = "All advertised feature types and layers are compatible."
            }
        };

        var manifestForWfs = new MigrationManifestArtifact
        {
            SourceKind = "ogc-wfs",
            Source = source,
            Summary = new MigrationManifestSummary
            {
                SourceResourceCount = 3,
                TargetResourceCount = 2
            }
        };

        var wfsImport = new OgcWfsImportResult
        {
            Success = true,
            WasDryRun = false,
            SourceServiceUrl = "https://ogc.example.com/services/wfs",
            SourceVersion = "2.0.0",
            FeatureTypesPlanned = 3,
            FeatureTypesImported = 2,
            FeatureTypesSkipped = 1,
            FeaturesCopied = 150,
            FeaturesFailed = 0,
            FeatureTypes =
            [
                new OgcWfsImportedFeatureType
                {
                    SourceName = "ops:parcels",
                    TargetSchema = "ops",
                    TargetTable = "parcels",
                    GeometryType = "MultiPolygon",
                    Srid = 4326,
                    FeaturesCopied = 100,
                    Classification = MigrationFidelityAutomationStatuses.Automated
                },
                new OgcWfsImportedFeatureType
                {
                    SourceName = "ops:roads",
                    TargetSchema = "ops",
                    TargetTable = "roads",
                    GeometryType = "MultiLineString",
                    Srid = 4326,
                    FeaturesCopied = 50,
                    Classification = MigrationFidelityAutomationStatuses.Automated
                },
                new OgcWfsImportedFeatureType
                {
                    SourceName = "ops:routes",
                    Classification = MigrationFidelityAutomationStatuses.ManualReview,
                    Warnings = ["Mixed geometry type — manual review required."]
                }
            ],
            Manifest = manifestForWfs,
            ParityEvidence = new MigrationParityEvidenceArtifact
            {
                SourceKind = "ogc-wfs",
                Source = source,
                OverallState = MigrationEvidenceStates.Pass,
                Summary = "Initial parity baseline.",
                CutoverReadiness = new MigrationCutoverReadinessSummary
                {
                    State = MigrationEvidenceStates.Unknown
                }
            }
        };

        var wmsPlan = new OgcRenderMigrationPlanResult(
            Entries:
            [
                new MigrationManifestPlanEntry
                {
                    Id = "plan:wms:metadata:roads",
                    SourceId = "wms-layer:roads",
                    SourceKind = "render-layer",
                    Category = "layer-metadata",
                    AutomationStatus = MigrationFidelityAutomationStatuses.Automated,
                    Code = "OGC_WMS_METADATA_AUTOMATED",
                    Name = "roads",
                    Reason = "WMS layer metadata projects deterministically."
                },
                new MigrationManifestPlanEntry
                {
                    Id = "plan:wms:render-data:roads",
                    SourceId = "wms-layer:roads",
                    SourceKind = "render-layer",
                    Category = "render-data",
                    AutomationStatus = MigrationFidelityAutomationStatuses.Unsupported,
                    Code = "OGC_WMS_RENDER_DATA_UNSUPPORTED",
                    Name = "roads",
                    Reason = "WMS exposes rendered images only."
                }
            ],
            Diagnostics:
            [
                new MigrationManifestPlanDiagnostic
                {
                    SourceId = "wms-layer:roads",
                    Code = "OGC_WMS_SLD_REFERENCE",
                    Severity = "info",
                    Message = "Layer references an external SLD; manual review recommended."
                }
            ]);

        var wmtsPlan = new OgcRenderMigrationPlanResult(
            Entries:
            [
                new MigrationManifestPlanEntry
                {
                    Id = "plan:wmts:tile-set:basemap",
                    SourceId = "tile-layer:basemap",
                    SourceKind = "tile-layer",
                    Category = "tile-set",
                    AutomationStatus = MigrationFidelityAutomationStatuses.Automated,
                    Code = "OGC_WMTS_TILE_SET_AUTOMATED",
                    Name = "basemap",
                    Reason = "Trivial tile-matrix-set automates cleanly."
                },
                new MigrationManifestPlanEntry
                {
                    Id = "plan:wmts:tile-set:imagery-deep",
                    SourceId = "tile-layer:imagery",
                    SourceKind = "tile-layer",
                    Category = "tile-set",
                    AutomationStatus = MigrationFidelityAutomationStatuses.ManualReview,
                    Code = "OGC_WMTS_TILE_SET_MANUAL_REVIEW",
                    Name = "imagery",
                    Reason = "Tile-matrix-set exceeds automated zoom safety cap."
                }
            ],
            Diagnostics: []);

        var manifestForTiles = new MigrationManifestArtifact
        {
            SourceKind = "ogc-wmts",
            Source = source,
            Summary = new MigrationManifestSummary
            {
                SourceResourceCount = 2,
                TargetResourceCount = 1
            }
        };

        var tileCacheExport = new OgcTileCacheExportResult
        {
            Success = true,
            WasDryRun = false,
            SourceServiceUrl = "https://ogc.example.com/services/wmts",
            TileSetsPlanned = 2,
            TileSetsExported = 1,
            TileSetsSkipped = 1,
            TilesPersisted = 5,
            TilesAlreadyPresent = 0,
            TilesFailed = 0,
            TileSets =
            [
                new OgcTileCacheExportedTileSet
                {
                    LayerIdentifier = "basemap",
                    TileMatrixSetIdentifier = "WebMercatorQuad",
                    TargetTileCacheId = "honua.tiles.basemap",
                    MinZoom = 0,
                    MaxZoom = 2,
                    TilesPersisted = 5,
                    Classification = MigrationFidelityAutomationStatuses.Automated,
                    Code = "OGC_WMTS_TILE_SET_EXPORTED"
                },
                new OgcTileCacheExportedTileSet
                {
                    LayerIdentifier = "imagery",
                    TileMatrixSetIdentifier = "WebMercatorQuad",
                    MinZoom = 0,
                    MaxZoom = 0,
                    Classification = MigrationFidelityAutomationStatuses.ManualReview,
                    Code = "OGC_WMTS_TILE_SET_MANUAL_REVIEW"
                }
            ],
            Manifest = manifestForTiles
        };

        return new OgcMigrationEvidencePackInputs
        {
            Inventory = inventory,
            WfsImport = wfsImport,
            WmsPlan = wmsPlan,
            WmtsPlan = wmtsPlan,
            TileCacheExport = tileCacheExport
        };
    }
}
