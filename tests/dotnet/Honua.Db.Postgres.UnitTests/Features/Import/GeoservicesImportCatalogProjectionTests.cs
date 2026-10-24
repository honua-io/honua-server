// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Db.Postgres.Features.Migration;

namespace Honua.Db.Postgres.Tests.Features.Import;

[Trait("Tier", "Fast")]
public sealed class GeoservicesImportCatalogProjectionTests
{
    [Theory]
    [InlineData("Shape")]
    [InlineData("SHAPE")]
    [InlineData("Footprint")]
    public void ReconcileApplyInventory_SourceGeometryNameDiffersFromPhysicalColumn_Passes(string sourceName)
    {
        var layer = SourceLayer(sourceName);

        var inventory = GeoservicesLayerPublicationService.BuildInventoryResourceFromLayerInfo(layer);
        var result = MigrationCatalogReconciler.ReconcileResource(inventory, PublishedResource());

        result.Classification.Should().Be(MigrationCatalogReconciliationClassifications.Pass);
        result.Findings.Should().BeEmpty();
        inventory.Fields.Should().Contain(field => field.Name == "geom" && field.FieldType == "esriFieldTypeGeometry");
        layer.Fields[0].Name.Should().Be(sourceName, "the source snapshot must remain unchanged");
    }

    [Theory]
    [InlineData("missing", MigrationCatalogReconciliationCodes.FieldMissing)]
    [InlineData("source-name-only", MigrationCatalogReconciliationCodes.FieldMissing)]
    [InlineData("attribute-type", MigrationCatalogReconciliationCodes.FieldTypeMismatch)]
    [InlineData("incompatible-geometry", MigrationCatalogReconciliationCodes.GeometryTypeMismatch)]
    public void ReconcileApplyInventory_TargetGeometryIsInvalid_RemainsBlocking(string fault, string expectedCode)
    {
        var inventory = GeoservicesLayerPublicationService.BuildInventoryResourceFromLayerInfo(SourceLayer("Shape"));
        var published = PublishedResource();
        published = fault switch
        {
            "missing" => published with { SchemaFields = [] },
            "source-name-only" => published with
            {
                SchemaFields = [new MetadataV2Field { Name = "Shape", Type = MetadataV2FieldType.Geometry }],
                Spatial = published.Spatial! with { PrimaryGeometryField = "Shape" }
            },
            "attribute-type" => published with
            {
                SchemaFields = [new MetadataV2Field { Name = "geom", Type = MetadataV2FieldType.String }]
            },
            "incompatible-geometry" => published with
            {
                Spatial = published.Spatial! with { GeometryType = MetadataV2GeometryType.Point }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };

        var result = MigrationCatalogReconciler.ReconcileResource(inventory, published);

        result.Classification.Should().Be(MigrationCatalogReconciliationClassifications.Fail);
        result.Findings.Should().Contain(finding => finding.Code == expectedCode &&
            finding.Severity == MigrationCatalogReconciliationSeverities.Fail);
    }

    [Fact]
    public void ReconcileApplyInventory_AttributeNamedShape_IsNotTreatedAsGeometry()
    {
        var layer = SourceLayer("Shape") with
        {
            Fields = [new GeoservicesFieldInfo { Name = "Shape", Type = "esriFieldTypeString" }]
        };
        var inventory = GeoservicesLayerPublicationService.BuildInventoryResourceFromLayerInfo(layer);

        var result = MigrationCatalogReconciler.ReconcileResource(inventory, PublishedResource());

        result.Findings.Should().Contain(finding => finding.Code == MigrationCatalogReconciliationCodes.FieldMissing &&
            finding.Subject == "Shape");
    }

    [Fact]
    public void ReconcileApplyInventory_NoSpatialLayer_DoesNotInventGeometryMapping()
    {
        var layer = SourceLayer("Shape") with { GeometryType = null };

        var inventory = GeoservicesLayerPublicationService.BuildInventoryResourceFromLayerInfo(layer);
        var result = MigrationCatalogReconciler.ReconcileResource(inventory, PublishedResource());

        inventory.Fields.Should().ContainSingle().Which.Name.Should().Be("Shape");
        result.Findings.Should().Contain(finding => finding.Code == MigrationCatalogReconciliationCodes.FieldMissing &&
            finding.Subject == "Shape");
    }

    private static GeoservicesLayerInfo SourceLayer(string geometryField) => new()
    {
        Id = 3,
        Name = "States",
        GeometryType = "esriGeometryPolygon",
        Fields = [new GeoservicesFieldInfo { Name = geometryField, Type = "esriFieldTypeGeometry" }]
    };

    private static MetadataV2Resource PublishedResource() => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = "res-layer-7", Name = "States" },
        Type = MetadataV2ResourceType.FeatureDataset,
        SchemaFields = [new MetadataV2Field { Name = "geom", Type = MetadataV2FieldType.Geometry }],
        Spatial = new MetadataV2ResourceSpatial
        {
            PrimaryGeometryField = "geom",
            GeometryType = MetadataV2GeometryType.MultiPolygon
        }
    };
}
