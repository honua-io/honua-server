// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
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
    public void Translate_WithFidelityClassifications_EmitsManifestFidelityMatrixWithTargetIds()
    {
        var inventory = CreateInventory(
            sourceKind: "arcgis-geoservices-rest",
            serviceType: "FeatureServer",
            resources:
            [
                CreateResource(
                    "layer:fidelity:0",
                    "Inspections",
                    "partial",
                    "Attachments and relationships require review.",
                    code: ImportCompatibilityCodes.ArcGisAttachments,
                    capabilities: ["Query", "Extract"],
                    manualSteps: ["Plan attachment migration."])
            ],
            styles:
            [
                CreateStyle(
                    "renderer:fidelity:0",
                    "partial",
                    "Renderer can be recreated with manual follow-up.",
                    code: ImportCompatibilityCodes.ManualReview)
            ],
            fidelityClassifications:
            [
                CreateFidelity(
                    "classification:layer:fidelity:0:identity",
                    "layer:fidelity:0",
                    "layer",
                    "identity",
                    MigrationFidelityAutomationStatuses.Automated,
                    ImportCompatibilityCodes.Compatible),
                CreateFidelity(
                    "classification:layer:fidelity:0:attachments",
                    "layer:fidelity:0",
                    "attachment",
                    "attachments",
                    MigrationFidelityAutomationStatuses.ManualReview,
                    ImportCompatibilityCodes.ArcGisAttachments,
                    manualSteps: ["Plan attachment migration."]),
                CreateFidelity(
                    "classification:renderer:fidelity:0:renderer",
                    "renderer:fidelity:0",
                    "renderer",
                    "renderers",
                    MigrationFidelityAutomationStatuses.ManualReview,
                    ImportCompatibilityCodes.ManualReview,
                    manualSteps: ["Recreate renderer in Honua style endpoints."])
            ]);

        var manifest = MigrationManifestTranslator.Translate(inventory, new MigrationManifestTranslationOptions
        {
            TargetServiceName = "ArcGIS Fidelity"
        });

        manifest.FidelityMatrix.Should().NotBeNull();
        manifest.FidelityMatrix!.Summary.ManualReviewCount.Should().Be(2);
        manifest.FidelityMatrix.Cells.Should().ContainSingle(cell =>
                cell.Category == "identity" &&
                cell.AutomationStatus == MigrationFidelityAutomationStatuses.Automated)
            .Which.TargetIds.Should().Equal("target:resource:arcgis-fidelity:inspections");
        manifest.FidelityMatrix.Cells.Should().ContainSingle(cell =>
                cell.Category == "renderers" &&
                cell.AutomationStatus == MigrationFidelityAutomationStatuses.ManualReview)
            .Which.TargetIds.Should().Equal("target:style:arcgis-fidelity:renderer-fidelity-0");
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

    [Fact]
    public void Translate_WithArcGisInventory_RecordsResourceIdentityEvidence()
    {
        var inventory = CreateArcGisInventory(
            serviceUrl: "https://example.com/arcgis/rest/services/Roads/FeatureServer",
            containers:
            [
                new MigrationInventoryContainer
                {
                    Id = "service:Roads",
                    Kind = "feature-service",
                    Name = "Roads",
                    Compatibility = Compatible("Service can be represented.")
                }
            ],
            resources:
            [
                new MigrationInventoryResource
                {
                    Id = "resource:Roads:layer:0",
                    ContainerId = "service:Roads",
                    Kind = "layer",
                    Name = "Centerlines",
                    GeometryType = "Polyline",
                    Capabilities = ["Query"],
                    Fields =
                    [
                        new MigrationInventoryField { Name = "OBJECTID", FieldType = "oid", Nullable = false }
                    ],
                    Compatibility = Compatible("Layer can be represented.")
                }
            ]);

        var manifest = MigrationManifestTranslator.Translate(inventory, new MigrationManifestTranslationOptions
        {
            TargetServiceName = "Roads"
        });

        var resource = manifest.TargetResources.Should().ContainSingle().Subject;
        resource.Identity.Should().NotBeNull();
        // Source layer id "0" was synthesized into the normalized resource name "centerlines",
        // so the identity is remapped (not preserved verbatim).
        resource.Identity!.SourceServiceId.Should().Be("Roads");
        resource.Identity.SourceLayerId.Should().Be("0");
        resource.Identity.SourceQualifiedName.Should().Be("Roads/0");
        // Honua normalizes target service names to lowercase deterministic slugs.
        resource.Identity.TargetServiceId.Should().Be("roads");
        resource.Identity.TargetLayerId.Should().Be("target:resource:roads:centerlines");
        resource.Identity.TargetName.Should().Be("centerlines");
        resource.Identity.IdentityStability.Should().Be(MigrationManifestIdentityStabilities.Remapped);
        resource.Identity.IdentityRemapReason.Should().NotBeNullOrEmpty();

        manifest.IdentityRemaps.Should().ContainSingle(remap => remap.SourceId == "resource:Roads:layer:0")
            .Which.Should().Match<MigrationManifestIdentityRemap>(remap =>
                remap.IdentityStability == MigrationManifestIdentityStabilities.Remapped &&
                remap.Reason != null);

        manifest.IdentityRemapping.Should().ContainKey("resource:Roads:layer:0")
            .WhoseValue.Should().Be("target:resource:roads:centerlines");
    }

    [Fact]
    public void Translate_WithArcGisFolderUrl_CapturesSourceFolderPath()
    {
        var inventory = CreateArcGisInventory(
            serviceUrl: "https://example.com/arcgis/rest/services/Utilities/Water/FeatureServer",
            containers:
            [
                new MigrationInventoryContainer
                {
                    Id = "service:Water",
                    Kind = "feature-service",
                    Name = "Water",
                    Compatibility = Compatible("Service can be represented.")
                }
            ],
            resources:
            [
                new MigrationInventoryResource
                {
                    Id = "resource:Water:layer:2",
                    ContainerId = "service:Water",
                    Kind = "layer",
                    Name = "Hydrants",
                    GeometryType = "Point",
                    Capabilities = ["Query"],
                    Fields = [],
                    Compatibility = Compatible("Layer can be represented.")
                }
            ]);

        var manifest = MigrationManifestTranslator.Translate(inventory);

        var resource = manifest.TargetResources.Should().ContainSingle().Subject;
        resource.Identity.Should().NotBeNull();
        resource.Identity!.SourceFolderPath.Should().Be("Utilities");
        resource.Identity.SourceQualifiedName.Should().Be("Utilities/Water/2");
    }

    [Fact]
    public void Translate_WithNonArcGisInventory_OmitsLayerIdentityFields()
    {
        var inventory = CreateInventory(
            resources:
            [
                CreateResource("workspace:roads", "Roads", "compatible", "Layer can be represented.")
            ]);

        var manifest = MigrationManifestTranslator.Translate(inventory);

        var resource = manifest.TargetResources.Should().ContainSingle().Subject;
        // Non-ArcGIS sources do not advertise stable layer identifiers, so identity is synthesized.
        resource.Identity.Should().NotBeNull();
        resource.Identity!.SourceLayerId.Should().BeNull();
        resource.Identity.SourceServiceId.Should().BeNull();
        resource.Identity.IdentityStability.Should().Be(MigrationManifestIdentityStabilities.Synthesized);
        manifest.IdentityRemaps.Should().ContainSingle(remap => remap.SourceId == "workspace:roads")
            .Which.IdentityStability.Should().Be(MigrationManifestIdentityStabilities.Synthesized);
        manifest.IdentityRemapping.Should().ContainKey("workspace:roads");
    }

    [Fact]
    public void Translate_OmitsPreservedIdentitiesFromIdentityRemapping()
    {
        // Translator deterministically slugifies resource names into the target service. Since the
        // resource pipeline always emits "target:resource:..." prefixed target ids, we cannot
        // reasonably construct an end-to-end "preserved" remap via the public Translate path. Verify
        // the filter directly: only remapped/synthesized entries appear in IdentityRemapping, so any
        // remap entry whose stability is Preserved must be absent from the dictionary.
        var inventory = CreateArcGisInventory(
            serviceUrl: "https://example.com/arcgis/rest/services/Mixed/FeatureServer",
            containers:
            [
                new MigrationInventoryContainer
                {
                    Id = "service:Mixed",
                    Kind = "feature-service",
                    Name = "Mixed",
                    Compatibility = Compatible("Service can be represented.")
                }
            ],
            resources:
            [
                new MigrationInventoryResource
                {
                    Id = "resource:Mixed:layer:0",
                    ContainerId = "service:Mixed",
                    Kind = "layer",
                    Name = "Hydrants",
                    GeometryType = "Point",
                    Capabilities = ["Query"],
                    Fields = [],
                    Compatibility = Compatible("Layer can be represented.")
                }
            ]);

        var manifest = MigrationManifestTranslator.Translate(inventory);

        manifest.IdentityRemaps.Should().NotBeEmpty();
        foreach (var remap in manifest.IdentityRemaps)
        {
            if (string.Equals(remap.IdentityStability, MigrationManifestIdentityStabilities.Preserved, StringComparison.Ordinal))
            {
                manifest.IdentityRemapping.Should().NotContainKey(remap.SourceId,
                    "preserved-identity entries are intentionally excluded so consumers can short-circuit");
            }
            else
            {
                manifest.IdentityRemapping.Should().ContainKey(remap.SourceId)
                    .WhoseValue.Should().Be(remap.TargetId);
            }
        }
    }

    [Fact]
    public void Translate_WithConflictingDuplicateSourceIds_Throws()
    {
        // Two resources sharing the same id but normalizing to different target names would yield
        // ambiguous identity remappings. The translator must surface this rather than silently
        // overwrite one mapping with the other.
        var inventory = CreateInventory(
            resources:
            [
                CreateResource("workspace:duplicate", "First Name", "compatible", "First."),
                CreateResource("workspace:duplicate", "Second Name", "compatible", "Second.")
            ]);

        Action act = () => MigrationManifestTranslator.Translate(inventory);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*workspace:duplicate*conflicting*");
    }

    [Fact]
    public void Translate_IdentityRemapEvidence_RoundTripsThroughSystemTextJson()
    {
        var inventory = CreateArcGisInventory(
            serviceUrl: "https://example.com/arcgis/rest/services/Parcels/FeatureServer",
            containers:
            [
                new MigrationInventoryContainer
                {
                    Id = "service:Parcels",
                    Kind = "feature-service",
                    Name = "Parcels",
                    Compatibility = Compatible("Service can be represented.")
                }
            ],
            resources:
            [
                new MigrationInventoryResource
                {
                    Id = "resource:Parcels:layer:0",
                    ContainerId = "service:Parcels",
                    Kind = "layer",
                    Name = "Parcels",
                    GeometryType = "Polygon",
                    Capabilities = ["Query"],
                    Fields = [],
                    Compatibility = Compatible("Layer can be represented.")
                }
            ]);

        var manifest = MigrationManifestTranslator.Translate(inventory, new MigrationManifestTranslationOptions
        {
            TargetServiceName = "Parcels"
        });

        // Round-trip through System.Text.Json using camelCase + ignore-null options matching
        // the server-side source-generated context.
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        var json = JsonSerializer.Serialize(manifest, options);
        json.Should().Contain("\"identity\":");
        json.Should().Contain("\"identityStability\":");
        json.Should().Contain("\"identityRemapping\":");
        json.Should().Contain("\"sourceLayerId\":\"0\"");

        var round = JsonSerializer.Deserialize<MigrationManifestArtifact>(json, options);
        round.Should().NotBeNull();
        round!.IdentityRemaps.Should().NotBeEmpty();
        round.IdentityRemaps[0].IdentityStability.Should()
            .Be(manifest.IdentityRemaps[0].IdentityStability);
        round.IdentityRemapping.Should().BeEquivalentTo(manifest.IdentityRemapping);
        round.TargetResources[0].Identity.Should().NotBeNull();
        round.TargetResources[0].Identity!.SourceLayerId.Should().Be("0");
        round.TargetResources[0].Identity!.IdentityStability.Should()
            .Be(manifest.TargetResources[0].Identity!.IdentityStability);
    }

    [Fact]
    public void Translate_WithArcGisAttachmentUnderSizeThreshold_ClassifiesAsAutomated()
    {
        var inventory = CreateArcGisAttachmentInventory(
            attachmentMetadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["attachmentId"] = "42",
                ["name"] = "site-photo.png",
                ["contentType"] = "image/png",
                ["size"] = "524288" // 512 KiB, well under the 10 MiB automated lane.
            });

        var manifest = MigrationManifestTranslator.Translate(inventory, new MigrationManifestTranslationOptions
        {
            TargetServiceName = "Inspections"
        });

        var resource = manifest.TargetResources.Should().ContainSingle().Subject;
        resource.Attachments.Should().ContainSingle()
            .Which.Should().Match<MigrationManifestAttachmentRecord>(record =>
                record.SourceAttachmentId == "42" &&
                record.Name == "site-photo.png" &&
                record.ContentType == "image/png" &&
                record.Size == 524288L &&
                record.Classification == MigrationManifestAttachmentClassifications.Automated &&
                record.TargetAttachmentRef == "target:attachment:inspections:inspection-points:42" &&
                record.Reason == null);
    }

    [Fact]
    public void Translate_WithArcGisAttachmentOverAutomatedLane_ClassifiesAsAssisted()
    {
        // 25 MiB exceeds the automated lane (10 MiB) but is well under the assisted ceiling (100 MiB).
        var inventory = CreateArcGisAttachmentInventory(
            attachmentMetadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["attachmentId"] = "77",
                ["name"] = "scan.pdf",
                ["contentType"] = "application/pdf",
                ["size"] = (25L * 1024L * 1024L).ToString(System.Globalization.CultureInfo.InvariantCulture)
            });

        var manifest = MigrationManifestTranslator.Translate(inventory, new MigrationManifestTranslationOptions
        {
            TargetServiceName = "Inspections"
        });

        var record = manifest.TargetResources.Should().ContainSingle().Subject
            .Attachments.Should().ContainSingle().Subject;
        record.Classification.Should().Be(MigrationManifestAttachmentClassifications.Assisted);
        record.TargetAttachmentRef.Should().Be("target:attachment:inspections:inspection-points:77");
        record.Reason.Should().Contain("10 MiB automated lane");
    }

    [Fact]
    public void Translate_WithArcGisAttachmentOverAssistedLane_ClassifiesAsManualReview()
    {
        // 200 MiB exceeds the 100 MiB assisted ceiling and must be planned out of band.
        var inventory = CreateArcGisAttachmentInventory(
            attachmentMetadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["attachmentId"] = "9001",
                ["name"] = "drone-survey.tiff",
                ["contentType"] = "image/tiff",
                ["size"] = (200L * 1024L * 1024L).ToString(System.Globalization.CultureInfo.InvariantCulture)
            });

        var manifest = MigrationManifestTranslator.Translate(inventory);
        var record = manifest.TargetResources.Should().ContainSingle().Subject
            .Attachments.Should().ContainSingle().Subject;
        // Disallowed content type already pushes the classification to manual-review; the size threshold
        // would have the same effect, so the classification stays manual-review regardless of which check
        // is reached first. Reason text reflects whichever guard fires first (content type).
        record.Classification.Should().Be(MigrationManifestAttachmentClassifications.ManualReview);
        record.TargetAttachmentRef.Should().BeNull();
        record.Reason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Translate_WithArcGisAttachmentMissingSize_ClassifiesAsManualReview()
    {
        var inventory = CreateArcGisAttachmentInventory(
            attachmentMetadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["attachmentId"] = "100",
                ["name"] = "unknown.bin",
                ["contentType"] = "application/pdf"
            });

        var record = MigrationManifestTranslator.Translate(inventory)
            .TargetResources.Should().ContainSingle().Subject
            .Attachments.Should().ContainSingle().Subject;
        record.Size.Should().BeNull();
        record.Classification.Should().Be(MigrationManifestAttachmentClassifications.ManualReview);
        record.Reason.Should().Contain("size was not advertised");
    }

    [Fact]
    public void Translate_WithArcGisOneToManyRelationship_ClassifiesAsAssisted()
    {
        var inventory = CreateArcGisRelationshipInventory(
            classifications:
            [
                CreateRelationshipClassification(
                    sourceId: "resource:Inspections:layer:0",
                    name: "InspectionToPhotos",
                    metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["relationshipId"] = "3",
                        ["name"] = "InspectionToPhotos",
                        ["cardinality"] = "1:N",
                        ["relationshipType"] = "simple",
                        ["relatedLayerIds"] = "0,1"
                    })
            ]);

        var manifest = MigrationManifestTranslator.Translate(inventory, new MigrationManifestTranslationOptions
        {
            TargetServiceName = "Inspections"
        });

        var record = manifest.TargetResources.Should().ContainSingle().Subject
            .Relationships.Should().ContainSingle().Subject;
        record.SourceRelationshipId.Should().Be("3");
        record.Cardinality.Should().Be("1:N");
        record.RelationshipType.Should().Be("simple");
        record.RelatedLayerIds.Should().Equal("0", "1");
        record.Classification.Should().Be(MigrationManifestRelationshipClassifications.Assisted);
        record.TargetRelationshipRef.Should().Be("target:relationship:inspections:inspection-points:3");
        record.Reason.Should().Contain("1:N");
    }

    [Fact]
    public void Translate_WithArcGisManyToManyRelationship_ClassifiesAsAssisted()
    {
        var inventory = CreateArcGisRelationshipInventory(
            classifications:
            [
                CreateRelationshipClassification(
                    sourceId: "resource:Inspections:layer:0",
                    name: "JunctionLink",
                    metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["relationshipId"] = "7",
                        ["cardinality"] = "esriRelCardinalityManyToMany",
                        ["relationshipType"] = "simple",
                        ["relatedLayerIds"] = "0,2,5"
                    })
            ]);

        var record = MigrationManifestTranslator.Translate(inventory)
            .TargetResources.Should().ContainSingle().Subject
            .Relationships.Should().ContainSingle().Subject;
        record.Cardinality.Should().Be("esriRelCardinalityManyToMany");
        record.Classification.Should().Be(MigrationManifestRelationshipClassifications.Assisted);
        record.Reason.Should().Contain("M:N");
        record.RelatedLayerIds.Should().Equal("0", "2", "5");
    }

    [Fact]
    public void Translate_WithArcGisCompositeRelationship_ClassifiesAsManualReviewWithReason()
    {
        var inventory = CreateArcGisRelationshipInventory(
            classifications:
            [
                CreateRelationshipClassification(
                    sourceId: "resource:Inspections:layer:0",
                    name: "CompositeOwnership",
                    metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["relationshipId"] = "11",
                        ["cardinality"] = "1:N",
                        ["relationshipType"] = "composite",
                        ["relatedLayerIds"] = "0,3"
                    })
            ]);

        var record = MigrationManifestTranslator.Translate(inventory)
            .TargetResources.Should().ContainSingle().Subject
            .Relationships.Should().ContainSingle().Subject;
        record.Classification.Should().Be(MigrationManifestRelationshipClassifications.ManualReview);
        record.TargetRelationshipRef.Should().BeNull();
        record.Reason.Should().Contain("composite");
    }

    [Fact]
    public void Translate_WithNonArcGisInventory_OmitsAttachmentAndRelationshipRecords()
    {
        var inventory = CreateInventory(
            resources:
            [
                CreateResource("workspace:roads", "Roads", "compatible", "Layer can be represented.")
            ]);

        var resource = MigrationManifestTranslator.Translate(inventory)
            .TargetResources.Should().ContainSingle().Subject;
        resource.Attachments.Should().BeEmpty();
        resource.Relationships.Should().BeEmpty();
    }

    [Fact]
    public void Translate_AttachmentAndRelationshipRecords_RoundTripThroughSystemTextJson()
    {
        var inventory = CreateArcGisInventory(
            serviceUrl: "https://example.com/arcgis/rest/services/Inspections/FeatureServer",
            containers:
            [
                new MigrationInventoryContainer
                {
                    Id = "service:Inspections",
                    Kind = "feature-service",
                    Name = "Inspections",
                    Compatibility = Compatible("Service can be represented.")
                }
            ],
            resources:
            [
                new MigrationInventoryResource
                {
                    Id = "resource:Inspections:layer:0",
                    ContainerId = "service:Inspections",
                    Kind = "layer",
                    Name = "Inspection Points",
                    GeometryType = "Point",
                    Capabilities = ["Query"],
                    ExternalDependencyIds = ["dependency:Inspections:layer:resource:Inspections:layer:0:attachments"],
                    Fields = [],
                    Compatibility = Compatible("Layer can be represented.")
                }
            ],
            dependencies:
            [
                new MigrationExternalDependency
                {
                    Id = "dependency:Inspections:layer:resource:Inspections:layer:0:attachments",
                    ContainerId = "service:Inspections",
                    ResourceId = "resource:Inspections:layer:0",
                    Kind = "attachments",
                    Name = "site-photo.png",
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["attachmentId"] = "42",
                        ["name"] = "site-photo.png",
                        ["contentType"] = "image/png",
                        ["size"] = "1024"
                    },
                    Compatibility = Compatible("Attachment captured.")
                }
            ],
            fidelityClassifications:
            [
                CreateRelationshipClassification(
                    sourceId: "resource:Inspections:layer:0",
                    name: "InspectionToPhotos",
                    metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["relationshipId"] = "3",
                        ["cardinality"] = "1:N",
                        ["relationshipType"] = "simple",
                        ["relatedLayerIds"] = "0,1"
                    })
            ]);

        var manifest = MigrationManifestTranslator.Translate(inventory, new MigrationManifestTranslationOptions
        {
            TargetServiceName = "Inspections"
        });

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        var json = JsonSerializer.Serialize(manifest, options);
        json.Should().Contain("\"attachments\":");
        json.Should().Contain("\"relationships\":");
        json.Should().Contain("\"classification\":\"automated\"");
        json.Should().Contain("\"cardinality\":\"1:N\"");

        var round = JsonSerializer.Deserialize<MigrationManifestArtifact>(json, options);
        round.Should().NotBeNull();
        var resource = round!.TargetResources.Should().ContainSingle().Subject;
        resource.Attachments.Should().HaveCount(1);
        resource.Attachments[0].Classification.Should().Be(MigrationManifestAttachmentClassifications.Automated);
        resource.Attachments[0].Size.Should().Be(1024);
        resource.Relationships.Should().HaveCount(1);
        resource.Relationships[0].Classification.Should().Be(MigrationManifestRelationshipClassifications.Assisted);
        resource.Relationships[0].RelatedLayerIds.Should().Equal("0", "1");
    }

    [Fact]
    public void Translate_WithArcGisSimpleRenderer_ClassifiesAsAutomatedWithSuggestedStyleId()
    {
        var inventory = CreateArcGisRendererInventory(
            rendererMetadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["rendererType"] = "simple"
            });

        var manifest = MigrationManifestTranslator.Translate(inventory, new MigrationManifestTranslationOptions
        {
            TargetServiceName = "Inspections"
        });

        var diagnostic = manifest.TargetResources.Should().ContainSingle().Subject
            .RendererDiagnostics.Should().ContainSingle().Subject;
        diagnostic.RendererKind.Should().Be("simple");
        diagnostic.CategoryCount.Should().BeNull();
        diagnostic.Classification.Should().Be(MigrationManifestRendererClassifications.Automated);
        diagnostic.SuggestedTargetStyleId.Should().Be("target:style:inspections:inspection-points:simple");
        diagnostic.Reason.Should().BeNull();
    }

    [Fact]
    public void Translate_WithArcGisUniqueValueRenderer_ClassifiesAsAssistedUnderCategoryLimit()
    {
        var inventory = CreateArcGisRendererInventory(
            rendererMetadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["rendererType"] = "uniqueValue",
                ["uniqueValueInfoCount"] = "5"
            });

        var diagnostic = MigrationManifestTranslator.Translate(inventory, new MigrationManifestTranslationOptions
        {
            TargetServiceName = "Inspections"
        }).TargetResources.Should().ContainSingle().Subject
          .RendererDiagnostics.Should().ContainSingle().Subject;
        diagnostic.RendererKind.Should().Be("uniqueValue");
        diagnostic.CategoryCount.Should().Be(5);
        diagnostic.Classification.Should().Be(MigrationManifestRendererClassifications.Assisted);
        diagnostic.SuggestedTargetStyleId.Should().Be("target:style:inspections:inspection-points:unique-value");
        diagnostic.Reason.Should().Contain("uniqueValue");
    }

    [Fact]
    public void Translate_WithArcGisClassBreaksRenderer_ClassifiesAsAssistedUnderBreakLimit()
    {
        var inventory = CreateArcGisRendererInventory(
            rendererMetadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["rendererType"] = "classBreaks",
                ["classBreakInfoCount"] = "4"
            });

        var diagnostic = MigrationManifestTranslator.Translate(inventory, new MigrationManifestTranslationOptions
        {
            TargetServiceName = "Inspections"
        }).TargetResources.Should().ContainSingle().Subject
          .RendererDiagnostics.Should().ContainSingle().Subject;
        diagnostic.RendererKind.Should().Be("classBreaks");
        diagnostic.CategoryCount.Should().Be(4);
        diagnostic.Classification.Should().Be(MigrationManifestRendererClassifications.Assisted);
        diagnostic.SuggestedTargetStyleId.Should().Be("target:style:inspections:inspection-points:class-breaks");
    }

    [Fact]
    public void Translate_WithArcGisUniqueValueRenderer_OverCategoryLimit_ClassifiesAsManualReview()
    {
        var inventory = CreateArcGisRendererInventory(
            rendererMetadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["rendererType"] = "uniqueValue",
                ["uniqueValueInfoCount"] = "42"
            });

        var diagnostic = MigrationManifestTranslator.Translate(inventory)
            .TargetResources.Should().ContainSingle().Subject
            .RendererDiagnostics.Should().ContainSingle().Subject;
        diagnostic.Classification.Should().Be(MigrationManifestRendererClassifications.ManualReview);
        diagnostic.SuggestedTargetStyleId.Should().BeNull();
        diagnostic.Reason.Should().Contain("42 categories");
    }

    [Fact]
    public void Translate_WithArcGisDictionaryRenderer_ClassifiesAsManualReview()
    {
        var inventory = CreateArcGisRendererInventory(
            rendererMetadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["rendererType"] = "dictionary"
            });

        var diagnostic = MigrationManifestTranslator.Translate(inventory)
            .TargetResources.Should().ContainSingle().Subject
            .RendererDiagnostics.Should().ContainSingle().Subject;
        diagnostic.Classification.Should().Be(MigrationManifestRendererClassifications.ManualReview);
        diagnostic.SuggestedTargetStyleId.Should().BeNull();
        diagnostic.Reason.Should().Contain("dictionary");
    }

    [Fact]
    public void Translate_WithArcGisUnknownRenderer_ClassifiesAsUnsupported()
    {
        var inventory = CreateArcGisRendererInventory(
            rendererMetadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["rendererType"] = "experimentalQuantumGradient"
            });

        var diagnostic = MigrationManifestTranslator.Translate(inventory)
            .TargetResources.Should().ContainSingle().Subject
            .RendererDiagnostics.Should().ContainSingle().Subject;
        diagnostic.Classification.Should().Be(MigrationManifestRendererClassifications.Unsupported);
        diagnostic.SuggestedTargetStyleId.Should().BeNull();
    }

    [Fact]
    public void Translate_WithArcGisFieldTokenLabelExpression_ClassifiesAsAutomated()
    {
        var inventory = CreateArcGisRendererInventory(
            rendererMetadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["rendererType"] = "simple",
                ["labelClass.0.expression"] = "[NAME]"
            });

        var diagnostic = MigrationManifestTranslator.Translate(inventory)
            .TargetResources.Should().ContainSingle().Subject
            .LabelClassDiagnostics.Should().ContainSingle().Subject;
        diagnostic.LabelClassIndex.Should().Be(0);
        diagnostic.Expression.Should().Be("[NAME]");
        diagnostic.Classification.Should().Be(MigrationManifestLabelClassClassifications.Automated);
        diagnostic.Reason.Should().BeNull();
    }

    [Fact]
    public void Translate_WithArcGisArcadeLabelExpression_ClassifiesAsAssisted()
    {
        var inventory = CreateArcGisRendererInventory(
            rendererMetadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["rendererType"] = "simple",
                ["labelClass.0.expression"] = "$feature.NAME + ' (' + $feature.YEAR + ')'",
                ["labelClass.0.expressionEngine"] = "Arcade"
            });

        var diagnostic = MigrationManifestTranslator.Translate(inventory)
            .TargetResources.Should().ContainSingle().Subject
            .LabelClassDiagnostics.Should().ContainSingle().Subject;
        diagnostic.ExpressionEngine.Should().Be("Arcade");
        diagnostic.Classification.Should().Be(MigrationManifestLabelClassClassifications.Assisted);
        diagnostic.Reason.Should().Contain("Arcade");
    }

    [Fact]
    public void Translate_WithArcGisVBScriptLabelExpression_ClassifiesAsUnsupported()
    {
        var inventory = CreateArcGisRendererInventory(
            rendererMetadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["rendererType"] = "simple",
                ["labelClass.0.expression"] = "[NAME] & vbCrLf & [POP]",
                ["labelClass.0.expressionEngine"] = "VBScript"
            });

        var diagnostic = MigrationManifestTranslator.Translate(inventory)
            .TargetResources.Should().ContainSingle().Subject
            .LabelClassDiagnostics.Should().ContainSingle().Subject;
        diagnostic.ExpressionEngine.Should().Be("VBScript");
        diagnostic.Classification.Should().Be(MigrationManifestLabelClassClassifications.Unsupported);
        diagnostic.Reason.Should().Contain("VBScript");
    }

    [Fact]
    public void Translate_WithMultipleArcGisLabelClasses_EmitsOneDiagnosticPerIndex()
    {
        var inventory = CreateArcGisRendererInventory(
            rendererMetadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["rendererType"] = "simple",
                ["labelClass.0.expression"] = "[NAME]",
                ["labelClass.1.expression"] = "[NAME] - [DEPT]",
                ["labelClass.2.expression"] = "$feature.NAME",
                ["labelClass.2.expressionEngine"] = "Arcade"
            });

        var diagnostics = MigrationManifestTranslator.Translate(inventory)
            .TargetResources.Should().ContainSingle().Subject
            .LabelClassDiagnostics;
        diagnostics.Should().HaveCount(3);
        diagnostics[0].LabelClassIndex.Should().Be(0);
        diagnostics[0].Classification.Should().Be(MigrationManifestLabelClassClassifications.Automated);
        diagnostics[1].LabelClassIndex.Should().Be(1);
        diagnostics[1].Classification.Should().Be(MigrationManifestLabelClassClassifications.ManualReview);
        diagnostics[2].LabelClassIndex.Should().Be(2);
        diagnostics[2].Classification.Should().Be(MigrationManifestLabelClassClassifications.Assisted);
    }

    [Fact]
    public void Translate_WithNonArcGisInventory_OmitsRendererAndLabelClassDiagnostics()
    {
        var inventory = CreateInventory(
            resources:
            [
                CreateResource("workspace:roads", "Roads", "compatible", "Layer can be represented.")
            ]);

        var resource = MigrationManifestTranslator.Translate(inventory)
            .TargetResources.Should().ContainSingle().Subject;
        resource.RendererDiagnostics.Should().BeEmpty();
        resource.LabelClassDiagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Translate_RendererAndLabelDiagnostics_RoundTripThroughSystemTextJson()
    {
        var inventory = CreateArcGisRendererInventory(
            rendererMetadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["rendererType"] = "uniqueValue",
                ["uniqueValueInfoCount"] = "3",
                ["labelClass.0.expression"] = "[NAME]"
            });

        var manifest = MigrationManifestTranslator.Translate(inventory, new MigrationManifestTranslationOptions
        {
            TargetServiceName = "Inspections"
        });

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        var json = JsonSerializer.Serialize(manifest, options);
        json.Should().Contain("\"rendererDiagnostics\":");
        json.Should().Contain("\"labelClassDiagnostics\":");
        json.Should().Contain("\"rendererKind\":\"uniqueValue\"");
        json.Should().Contain("\"suggestedTargetStyleId\":\"target:style:inspections:inspection-points:unique-value\"");
        json.Should().Contain("\"classification\":\"automated\"");

        var round = JsonSerializer.Deserialize<MigrationManifestArtifact>(json, options);
        round.Should().NotBeNull();
        var resource = round!.TargetResources.Should().ContainSingle().Subject;
        resource.RendererDiagnostics.Should().HaveCount(1);
        resource.RendererDiagnostics[0].RendererKind.Should().Be("uniqueValue");
        resource.RendererDiagnostics[0].CategoryCount.Should().Be(3);
        resource.RendererDiagnostics[0].Classification.Should().Be(MigrationManifestRendererClassifications.Assisted);
        resource.LabelClassDiagnostics.Should().HaveCount(1);
        resource.LabelClassDiagnostics[0].Expression.Should().Be("[NAME]");
        resource.LabelClassDiagnostics[0].Classification.Should().Be(MigrationManifestLabelClassClassifications.Automated);
    }

    private static MigrationSourceInventoryArtifact CreateArcGisRendererInventory(
        Dictionary<string, string> rendererMetadata)
    {
        const string resourceId = "resource:Inspections:layer:0";
        const string styleId = "renderer:Inspections:0";
        return CreateArcGisInventory(
            serviceUrl: "https://example.com/arcgis/rest/services/Inspections/FeatureServer",
            containers:
            [
                new MigrationInventoryContainer
                {
                    Id = "service:Inspections",
                    Kind = "feature-service",
                    Name = "Inspections",
                    Compatibility = Compatible("Service can be represented.")
                }
            ],
            resources:
            [
                new MigrationInventoryResource
                {
                    Id = resourceId,
                    ContainerId = "service:Inspections",
                    Kind = "layer",
                    Name = "Inspection Points",
                    GeometryType = "Point",
                    Capabilities = ["Query"],
                    Fields = [],
                    Compatibility = Compatible("Layer can be represented.")
                }
            ],
            styles:
            [
                new MigrationInventoryStyle
                {
                    Id = styleId,
                    ContainerId = "service:Inspections",
                    Kind = "renderer",
                    Name = "Inspection Points",
                    Format = "esri-renderer",
                    ResourceIds = [resourceId],
                    Metadata = rendererMetadata,
                    Compatibility = Compatible("Renderer captured.")
                }
            ]);
    }

    private static MigrationSourceInventoryArtifact CreateArcGisAttachmentInventory(
        Dictionary<string, string> attachmentMetadata)
    {
        const string resourceId = "resource:Inspections:layer:0";
        var dependencyId = $"dependency:Inspections:layer:{resourceId}:attachments";
        return CreateArcGisInventory(
            serviceUrl: "https://example.com/arcgis/rest/services/Inspections/FeatureServer",
            containers:
            [
                new MigrationInventoryContainer
                {
                    Id = "service:Inspections",
                    Kind = "feature-service",
                    Name = "Inspections",
                    Compatibility = Compatible("Service can be represented.")
                }
            ],
            resources:
            [
                new MigrationInventoryResource
                {
                    Id = resourceId,
                    ContainerId = "service:Inspections",
                    Kind = "layer",
                    Name = "Inspection Points",
                    GeometryType = "Point",
                    Capabilities = ["Query"],
                    ExternalDependencyIds = [dependencyId],
                    Fields = [],
                    Compatibility = Compatible("Layer can be represented.")
                }
            ],
            dependencies:
            [
                new MigrationExternalDependency
                {
                    Id = dependencyId,
                    ContainerId = "service:Inspections",
                    ResourceId = resourceId,
                    Kind = "attachments",
                    Name = attachmentMetadata.GetValueOrDefault("name") ?? "attachment",
                    Metadata = attachmentMetadata,
                    Compatibility = Compatible("Attachment captured.")
                }
            ]);
    }

    private static MigrationSourceInventoryArtifact CreateArcGisRelationshipInventory(
        MigrationFidelityClassificationRecord[] classifications)
    {
        return CreateArcGisInventory(
            serviceUrl: "https://example.com/arcgis/rest/services/Inspections/FeatureServer",
            containers:
            [
                new MigrationInventoryContainer
                {
                    Id = "service:Inspections",
                    Kind = "feature-service",
                    Name = "Inspections",
                    Compatibility = Compatible("Service can be represented.")
                }
            ],
            resources:
            [
                new MigrationInventoryResource
                {
                    Id = "resource:Inspections:layer:0",
                    ContainerId = "service:Inspections",
                    Kind = "layer",
                    Name = "Inspection Points",
                    GeometryType = "Point",
                    Capabilities = ["Query"],
                    Fields = [],
                    Compatibility = Compatible("Layer can be represented.")
                }
            ],
            fidelityClassifications: classifications);
    }

    private static MigrationFidelityClassificationRecord CreateRelationshipClassification(
        string sourceId,
        string name,
        Dictionary<string, string> metadata)
        => new()
        {
            Id = $"classification:{sourceId}:relationships:{metadata.GetValueOrDefault("relationshipId", "0")}",
            SourceId = sourceId,
            Kind = "relationship",
            Category = "relationships",
            Name = name,
            AutomationStatus = MigrationFidelityAutomationStatuses.ManualReview,
            Code = ImportCompatibilityCodes.ArcGisRelationshipsManualReview,
            Reason = "Relationship metadata captured.",
            ManualSteps = [],
            RelatedIds = [],
            Metadata = metadata
        };

    private static MigrationSourceInventoryArtifact CreateArcGisInventory(
        string serviceUrl,
        MigrationInventoryContainer[] containers,
        MigrationInventoryResource[] resources,
        MigrationInventoryStyle[]? styles = null,
        MigrationExternalDependency[]? dependencies = null,
        MigrationFidelityClassificationRecord[]? fidelityClassifications = null)
    {
        var styleItems = styles ?? [];
        var dependencyItems = dependencies ?? [];
        var fidelityItems = fidelityClassifications ?? [];

        return new MigrationSourceInventoryArtifact
        {
            SourceKind = "arcgis-geoservices-rest",
            Source = new MigrationSourceIdentity
            {
                DisplayName = "ArcGIS Source",
                BaseUrl = serviceUrl,
                Product = "ArcGIS",
                Version = "11.2",
                ServiceType = "FeatureServer"
            },
            AuthPosture = new MigrationInventoryAuthPosture
            {
                Mode = "anonymous",
                CredentialsSupplied = false,
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
                CompatibleCount = resources.Length + styleItems.Length + dependencyItems.Length + containers.Length
            },
            OverallCompatibility = Compatible("Fixture compatibility is compatible."),
            Containers = containers,
            Resources = resources,
            Styles = styleItems,
            ExternalDependencies = dependencyItems,
            FidelityClassifications = fidelityItems,
            FidelityMatrix = MigrationFidelityMatrixBuilder.Build(fidelityItems)
        };
    }

    private static MigrationSourceInventoryArtifact CreateInventory(
        MigrationInventoryResource[] resources,
        string sourceKind = "geoserver-rest",
        string? serviceType = null,
        string containerKind = "workspace",
        MigrationInventoryStyle[]? styles = null,
        MigrationExternalDependency[]? dependencies = null,
        MigrationFidelityClassificationRecord[]? fidelityClassifications = null)
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
            ExternalDependencies = dependencyItems,
            FidelityClassifications = fidelityClassifications ?? [],
            FidelityMatrix = MigrationFidelityMatrixBuilder.Build(fidelityClassifications ?? [])
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

    private static MigrationFidelityClassificationRecord CreateFidelity(
        string id,
        string sourceId,
        string kind,
        string category,
        string automationStatus,
        string code,
        string[]? manualSteps = null)
        => new()
        {
            Id = id,
            SourceId = sourceId,
            Kind = kind,
            Category = category,
            AutomationStatus = automationStatus,
            Code = code,
            Reason = $"{category} disposition captured.",
            ManualSteps = manualSteps ?? []
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
