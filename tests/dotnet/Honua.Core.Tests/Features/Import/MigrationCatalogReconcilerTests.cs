// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;

namespace Honua.Core.Tests.Features.Import;

/// <summary>
/// Drives the post-publish catalog reconciler over an inventory + published Metadata v2 resource
/// pair and asserts each acceptance finding code emits when the published catalog entry diverges
/// from the captured source service definition (issue #1248).
/// </summary>
public sealed class MigrationCatalogReconcilerTests
{
    [Fact]
    public void Reconcile_WhenPublishedCatalogMatchesInventory_ReportsPassWithNoFindings()
    {
        var inventory = BuildInventoryResource();
        var published = BuildPublishedResource();

        var outcome = MigrationCatalogReconciler.ReconcileResource(inventory, published);

        outcome.Classification.Should().Be(MigrationCatalogReconciliationClassifications.Pass);
        outcome.Findings.Should().BeEmpty();
        outcome.TargetResourceId.Should().Be("svc.parcels");
    }

    [Fact]
    public void Reconcile_WhenPublishedResourceMissing_ReportsNotApplicableAndResourceMissingFinding()
    {
        var inventory = BuildInventoryResource();

        var outcome = MigrationCatalogReconciler.ReconcileResource(inventory, publishedResource: null);

        outcome.Classification.Should().Be(MigrationCatalogReconciliationClassifications.NotApplicable);
        outcome.Findings.Should().ContainSingle()
            .Which.Code.Should().Be(MigrationCatalogReconciliationCodes.ResourceMissing);
    }

    [Fact]
    public void Reconcile_WhenPublishedFieldMissing_EmitsFieldMissingFailFinding()
    {
        var inventory = BuildInventoryResource();
        var published = BuildPublishedResource() with
        {
            SchemaFields = BuildPublishedResource().SchemaFields
                .Where(field => !string.Equals(field.Name, "TYPE", StringComparison.Ordinal))
                .ToArray()
        };

        var outcome = MigrationCatalogReconciler.ReconcileResource(inventory, published);

        outcome.Classification.Should().Be(MigrationCatalogReconciliationClassifications.Fail);
        outcome.Findings.Should().ContainSingle(finding => finding.Code == MigrationCatalogReconciliationCodes.FieldMissing)
            .Which.Subject.Should().Be("TYPE");
    }

    [Fact]
    public void Reconcile_WhenPublishedFieldTypeDiffers_EmitsFieldTypeMismatchFinding()
    {
        var inventory = BuildInventoryResource();
        var published = BuildPublishedResource() with
        {
            SchemaFields = BuildPublishedResource().SchemaFields
                .Select(field => field.Name == "TYPE" ? field with { Type = MetadataV2FieldType.Integer } : field)
                .ToArray()
        };

        var outcome = MigrationCatalogReconciler.ReconcileResource(inventory, published);

        var finding = outcome.Findings.Should().ContainSingle(f => f.Code == MigrationCatalogReconciliationCodes.FieldTypeMismatch).Subject;
        finding.Subject.Should().Be("TYPE");
        finding.Expected.Should().Be("String");
        finding.Actual.Should().Be("Integer");
    }

    [Fact]
    public void Reconcile_WhenSourceIntegerWidensToBigIntegerOnTarget_DoesNotEmitTypeMismatch()
    {
        var inventory = BuildInventoryResource();
        var published = BuildPublishedResource() with
        {
            SchemaFields = BuildPublishedResource().SchemaFields
                .Select(field => field.Name == "OBJECTID" ? field with { Type = MetadataV2FieldType.BigInteger } : field)
                .ToArray()
        };

        var outcome = MigrationCatalogReconciler.ReconcileResource(inventory, published);

        outcome.Findings.Should().NotContain(finding => finding.Code == MigrationCatalogReconciliationCodes.FieldTypeMismatch);
    }

    [Fact]
    public void Reconcile_WhenPublishedFieldMissesInventoryDomain_EmitsDomainMissingFinding()
    {
        var inventory = BuildInventoryResource();
        var published = BuildPublishedResource() with
        {
            SchemaFields = BuildPublishedResource().SchemaFields
                .Select(field => field.Name == "TYPE" ? field with { Domain = null } : field)
                .ToArray()
        };

        var outcome = MigrationCatalogReconciler.ReconcileResource(inventory, published);

        outcome.Findings.Should().ContainSingle(f => f.Code == MigrationCatalogReconciliationCodes.DomainMissing)
            .Which.Subject.Should().Be("TYPE");
    }

    [Fact]
    public void Reconcile_WhenPublishedDomainNameDiffers_EmitsWarnSeverityDomainNameMismatch()
    {
        var inventory = BuildInventoryResource();
        var published = BuildPublishedResource() with
        {
            SchemaFields = BuildPublishedResource().SchemaFields
                .Select(field => field.Name == "TYPE"
                    ? field with { Domain = field.Domain! with { Name = "ParcelTypeDomain" } }
                    : field)
                .ToArray()
        };

        var outcome = MigrationCatalogReconciler.ReconcileResource(inventory, published);

        var finding = outcome.Findings.Should().ContainSingle(f => f.Code == MigrationCatalogReconciliationCodes.DomainNameMismatch).Subject;
        finding.Severity.Should().Be(MigrationCatalogReconciliationSeverities.Warn);
        finding.Expected.Should().Be("ParcelType");
        finding.Actual.Should().Be("ParcelTypeDomain");
        outcome.Classification.Should().Be(MigrationCatalogReconciliationClassifications.Warn);
    }

    [Fact]
    public void Reconcile_WhenInventoryCodedValuesAbsentOnPublishedDomain_EmitsDomainValuesMismatch()
    {
        var inventory = BuildInventoryResource();
        var published = BuildPublishedResource() with
        {
            SchemaFields = BuildPublishedResource().SchemaFields
                .Select(field => field.Name == "TYPE"
                    ? field with
                    {
                        Domain = field.Domain! with
                        {
                            CodedValues =
                            [
                                new MetadataV2CodedValue { Code = JsonDocument.Parse("\"R\"").RootElement, Name = "Residential" }
                            ]
                        }
                    }
                    : field)
                .ToArray()
        };

        var outcome = MigrationCatalogReconciler.ReconcileResource(inventory, published);

        var finding = outcome.Findings.Should().ContainSingle(f => f.Code == MigrationCatalogReconciliationCodes.DomainValuesMismatch).Subject;
        finding.Severity.Should().Be(MigrationCatalogReconciliationSeverities.Fail);
        finding.Summary.Should().Contain("C");
    }

    [Fact]
    public void Reconcile_WhenGeometryTypeDiffers_EmitsGeometryTypeMismatch()
    {
        var inventory = BuildInventoryResource();
        var published = BuildPublishedResource() with
        {
            Spatial = BuildPublishedResource().Spatial! with { GeometryType = MetadataV2GeometryType.Point }
        };

        var outcome = MigrationCatalogReconciler.ReconcileResource(inventory, published);

        outcome.Findings.Should().ContainSingle(f => f.Code == MigrationCatalogReconciliationCodes.GeometryTypeMismatch)
            .Which.Subject.Should().Be("spatial");
    }

    [Fact]
    public void Reconcile_WhenSridDiffers_EmitsSridMismatch()
    {
        var inventory = BuildInventoryResource();
        var published = BuildPublishedResource() with
        {
            Spatial = BuildPublishedResource().Spatial! with
            {
                SpatialReference = new MetadataV2SpatialReference { Srid = 3857, Crs = "EPSG:3857" }
            }
        };

        var outcome = MigrationCatalogReconciler.ReconcileResource(inventory, published);

        var finding = outcome.Findings.Should().ContainSingle(f => f.Code == MigrationCatalogReconciliationCodes.SridMismatch).Subject;
        finding.Expected.Should().Be("2227");
        finding.Actual.Should().Be("3857");
    }

    [Fact]
    public void Reconcile_WhenPublishedHasNoBboxButSourceDoes_EmitsExtentMissingWarn()
    {
        var inventory = BuildInventoryResource();
        var published = BuildPublishedResource() with
        {
            Spatial = BuildPublishedResource().Spatial! with { Bbox = null }
        };

        var outcome = MigrationCatalogReconciler.ReconcileResource(
            inventory,
            published,
            sourceBbox: [6_000_000, 1_900_000, 6_100_000, 2_000_000]);

        var finding = outcome.Findings.Should().ContainSingle(f => f.Code == MigrationCatalogReconciliationCodes.ExtentMissing).Subject;
        finding.Severity.Should().Be(MigrationCatalogReconciliationSeverities.Warn);
    }

    [Fact]
    public void Reconcile_WhenPublishedBboxDisjointFromSource_EmitsExtentMismatchWarn()
    {
        var inventory = BuildInventoryResource();
        var published = BuildPublishedResource();

        var outcome = MigrationCatalogReconciler.ReconcileResource(
            inventory,
            published,
            sourceBbox: [10_000_000, 5_000_000, 10_100_000, 5_100_000]);

        outcome.Findings.Should().ContainSingle(f => f.Code == MigrationCatalogReconciliationCodes.ExtentMismatch)
            .Which.Severity.Should().Be(MigrationCatalogReconciliationSeverities.Warn);
    }

    [Fact]
    public void Reconcile_WhenInventoryHasObjectIdButPublishedHasNoPrimaryIdentifier_EmitsObjectIdMissing()
    {
        var inventory = BuildInventoryResource();
        var published = BuildPublishedResource() with
        {
            SchemaFields = BuildPublishedResource().SchemaFields
                .Select(field => field.Name == "OBJECTID"
                    ? field with { SemanticRoles = Array.Empty<string>() }
                    : field)
                .ToArray()
        };

        var outcome = MigrationCatalogReconciler.ReconcileResource(inventory, published);

        outcome.Findings.Should().ContainSingle(f => f.Code == MigrationCatalogReconciliationCodes.ObjectIdMissing)
            .Which.Severity.Should().Be(MigrationCatalogReconciliationSeverities.Fail);
    }

    [Fact]
    public void Reconcile_WhenInventoryHasGlobalIdButPublishedHasNoGlobalIdBinding_EmitsGlobalIdMissingWarn()
    {
        var inventory = BuildInventoryResource();
        var published = BuildPublishedResource() with { Editing = null };

        var outcome = MigrationCatalogReconciler.ReconcileResource(inventory, published);

        outcome.Findings.Should().ContainSingle(f => f.Code == MigrationCatalogReconciliationCodes.GlobalIdMissing)
            .Which.Severity.Should().Be(MigrationCatalogReconciliationSeverities.Warn);
    }

    [Fact]
    public void Reconcile_WhenExpectedRelationshipMissingFromPublished_EmitsRelationshipMissing()
    {
        var inventory = BuildInventoryResource();
        var published = BuildPublishedResource() with { Relationships = Array.Empty<MetadataV2Relationship>() };
        var expectedRelationship = new MigrationManifestRelationshipRecord
        {
            SourceRelationshipId = "rel:0",
            SourceResourceId = inventory.Id,
            Name = "ParcelToOwner",
            Cardinality = "1:N",
            Classification = MigrationManifestRelationshipClassifications.Automated
        };

        var outcome = MigrationCatalogReconciler.ReconcileResource(
            inventory,
            published,
            expectedRelationships: [expectedRelationship]);

        outcome.Findings.Should().ContainSingle(f => f.Code == MigrationCatalogReconciliationCodes.RelationshipMissing)
            .Which.Subject.Should().Be("rel:0");
    }

    [Fact]
    public void Reconcile_WhenExpectedRelationshipIsManualReview_DoesNotEmitRelationshipMissing()
    {
        var inventory = BuildInventoryResource();
        var published = BuildPublishedResource() with { Relationships = Array.Empty<MetadataV2Relationship>() };
        var deferred = new MigrationManifestRelationshipRecord
        {
            SourceRelationshipId = "rel:1",
            SourceResourceId = inventory.Id,
            Name = "ParcelToTaxDistrict",
            Cardinality = "M:N",
            Classification = MigrationManifestRelationshipClassifications.ManualReview
        };

        var outcome = MigrationCatalogReconciler.ReconcileResource(
            inventory,
            published,
            expectedRelationships: [deferred]);

        outcome.Findings.Should().NotContain(finding => finding.Code == MigrationCatalogReconciliationCodes.RelationshipMissing);
    }

    [Fact]
    public void BuildReport_OrdersResourcesAndRollsUpSummaryDeterministically()
    {
        var passInventory = BuildInventoryResource("layer:b", "Parcels");
        var failInventory = BuildInventoryResource("layer:a", "Roads");
        var missingInventory = BuildInventoryResource("layer:c", "Buildings");

        var passPublished = BuildPublishedResource();
        var failPublished = BuildPublishedResource() with
        {
            SchemaFields = BuildPublishedResource().SchemaFields
                .Where(field => field.Name != "TYPE")
                .ToArray()
        };

        var report = MigrationCatalogReconciler.BuildReport(
            runId: "reconcile-2026-05-30",
            sourceKind: "arcgis-geoservices-rest",
            inputs:
            [
                new MigrationCatalogReconciliationInput { Resource = passInventory, PublishedResource = passPublished },
                new MigrationCatalogReconciliationInput { Resource = failInventory, PublishedResource = failPublished },
                new MigrationCatalogReconciliationInput { Resource = missingInventory, PublishedResource = null }
            ]);

        report.Resources.Select(static r => r.SourceResourceId).Should().Equal("layer:a", "layer:b", "layer:c");
        report.Summary.ResourceCount.Should().Be(3);
        report.Summary.PassResourceCount.Should().Be(1);
        report.Summary.FailResourceCount.Should().Be(1);
        report.Summary.NotApplicableResourceCount.Should().Be(1);
        report.Summary.FindingCount.Should().Be(2);
        report.ArtifactKind.Should().Be("honua.migration.catalog-reconciliation-report");
        report.ArtifactVersion.Should().Be("1.0");
    }

    private static MigrationInventoryResource BuildInventoryResource(string id = "layer:parcels", string name = "Parcels")
        => new()
        {
            Id = id,
            ContainerId = "svc",
            Kind = "layer",
            Name = name,
            GeometryType = "esriGeometryPolygon",
            FeatureCount = 1234,
            SpatialReferences =
            [
                new MigrationSpatialReferenceInfo
                {
                    Role = "declared",
                    SourceValue = "PCS_StatePlane_California_3_FIPS_0403_Feet",
                    Srid = 2227,
                    CrsUri = "EPSG:2227"
                }
            ],
            Fields =
            [
                new MigrationInventoryField
                {
                    Name = "OBJECTID",
                    FieldType = "esriFieldTypeOID",
                    Nullable = false
                },
                new MigrationInventoryField
                {
                    Name = "GLOBALID",
                    FieldType = "esriFieldTypeGlobalID",
                    Nullable = false
                },
                new MigrationInventoryField
                {
                    Name = "TYPE",
                    FieldType = "esriFieldTypeString",
                    Nullable = true,
                    DomainType = "codedValue",
                    DomainName = "ParcelType",
                    DomainValues =
                    [
                        new MigrationInventoryCodedValue { Code = "R", Name = "Residential" },
                        new MigrationInventoryCodedValue { Code = "C", Name = "Commercial" }
                    ]
                }
            ],
            Compatibility = new MigrationCompatibilityAssessment
            {
                Level = "compatible",
                Reason = "Schema and CRS map cleanly."
            }
        };

    private static MetadataV2Resource BuildPublishedResource()
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "svc.parcels", Name = "parcels" },
            SchemaFields =
            [
                new MetadataV2Field
                {
                    Name = "OBJECTID",
                    Type = MetadataV2FieldType.Integer,
                    Nullable = false,
                    SemanticRoles = ["id.primary"]
                },
                new MetadataV2Field
                {
                    Name = "GLOBALID",
                    Type = MetadataV2FieldType.Uuid,
                    Nullable = false
                },
                new MetadataV2Field
                {
                    Name = "TYPE",
                    Type = MetadataV2FieldType.String,
                    Nullable = true,
                    Domain = new MetadataV2FieldDomain
                    {
                        Name = "ParcelType",
                        Type = "codedValue",
                        CodedValues =
                        [
                            new MetadataV2CodedValue { Code = JsonDocument.Parse("\"R\"").RootElement, Name = "Residential" },
                            new MetadataV2CodedValue { Code = JsonDocument.Parse("\"C\"").RootElement, Name = "Commercial" }
                        ]
                    }
                }
            ],
            Spatial = new MetadataV2ResourceSpatial
            {
                SpatialReference = new MetadataV2SpatialReference { Srid = 2227, Crs = "EPSG:2227" },
                GeometryType = MetadataV2GeometryType.MultiPolygon,
                Bbox = new MetadataV2Bbox { West = 6_000_000, South = 1_900_000, East = 6_100_000, North = 2_000_000 }
            },
            Editing = new MetadataV2ResourceEditing { GlobalIdField = "GLOBALID" }
        };

    [Fact]
    public void Reconcile_WhenSourceAdvertisesAttachmentsButPublishedDoesNot_EmitsAttachmentMissingFinding()
    {
        var inventory = BuildInventoryResource() with { HasAttachments = true };
        var published = BuildPublishedResource() with
        {
            Editing = new MetadataV2ResourceEditing { GlobalIdField = "GLOBALID", SupportsAttachments = false }
        };

        var outcome = MigrationCatalogReconciler.ReconcileResource(inventory, published);

        outcome.Classification.Should().Be(MigrationCatalogReconciliationClassifications.Fail);
        outcome.Findings.Should().ContainSingle(f => f.Code == MigrationCatalogReconciliationCodes.AttachmentMissing)
            .Which.Severity.Should().Be(MigrationCatalogReconciliationSeverities.Fail);
    }

    [Fact]
    public void Reconcile_WhenSourceAndPublishedBothSupportAttachments_EmitsNoAttachmentFinding()
    {
        var inventory = BuildInventoryResource() with { HasAttachments = true };
        var published = BuildPublishedResource() with
        {
            Editing = new MetadataV2ResourceEditing { GlobalIdField = "GLOBALID", SupportsAttachments = true }
        };

        var outcome = MigrationCatalogReconciler.ReconcileResource(inventory, published);

        outcome.Findings.Should().NotContain(f => f.Code == MigrationCatalogReconciliationCodes.AttachmentMissing);
    }

    [Fact]
    public void Reconcile_WhenSourceDidNotAdvertiseAttachments_DoesNotFalseFailOnMissingAttachmentSupport()
    {
        // HasAttachments == null means "source did not report" — must not be treated as a fail.
        var inventory = BuildInventoryResource() with { HasAttachments = null };
        var published = BuildPublishedResource() with { Editing = null };

        var outcome = MigrationCatalogReconciler.ReconcileResource(inventory, published);

        outcome.Findings.Should().NotContain(f => f.Code == MigrationCatalogReconciliationCodes.AttachmentMissing);
    }

    [Fact]
    public void Reconcile_WhenSourceSubtypesMissingFromPublished_EmitsSubtypeMissingFinding()
    {
        var inventory = BuildInventoryResource();
        var published = BuildPublishedResource() with { Subtypes = null };
        var expectedSubtypes = BuildSubtypes("STATUS", ("1", "Active"), ("2", "Retired"));

        var outcome = MigrationCatalogReconciler.ReconcileResource(
            inventory, published, expectedSubtypes: expectedSubtypes);

        outcome.Classification.Should().Be(MigrationCatalogReconciliationClassifications.Fail);
        outcome.Findings.Should().ContainSingle(f => f.Code == MigrationCatalogReconciliationCodes.SubtypeMissing)
            .Which.Expected.Should().Be("STATUS");
    }

    [Fact]
    public void Reconcile_WhenPublishedSubtypeFieldDiffers_EmitsSubtypeFieldMismatchFinding()
    {
        var inventory = BuildInventoryResource();
        var published = BuildPublishedResource() with
        {
            Subtypes = BuildSubtypes("KIND", ("1", "Active"), ("2", "Retired"))
        };
        var expectedSubtypes = BuildSubtypes("STATUS", ("1", "Active"), ("2", "Retired"));

        var outcome = MigrationCatalogReconciler.ReconcileResource(
            inventory, published, expectedSubtypes: expectedSubtypes);

        outcome.Findings.Should().ContainSingle(f => f.Code == MigrationCatalogReconciliationCodes.SubtypeFieldMismatch)
            .Which.Actual.Should().Be("KIND");
    }

    [Fact]
    public void Reconcile_WhenPublishedMissingSubtypeCode_EmitsSubtypeCodeMissingFinding()
    {
        var inventory = BuildInventoryResource();
        var published = BuildPublishedResource() with
        {
            Subtypes = BuildSubtypes("STATUS", ("1", "Active"))
        };
        var expectedSubtypes = BuildSubtypes("STATUS", ("1", "Active"), ("2", "Retired"));

        var outcome = MigrationCatalogReconciler.ReconcileResource(
            inventory, published, expectedSubtypes: expectedSubtypes);

        outcome.Findings.Should().ContainSingle(f => f.Code == MigrationCatalogReconciliationCodes.SubtypeCodeMissing)
            .Which.Summary.Should().Contain("2");
    }

    [Fact]
    public void Reconcile_WhenSubtypesMatch_EmitsNoSubtypeFindings()
    {
        var inventory = BuildInventoryResource();
        var subtypes = BuildSubtypes("STATUS", ("1", "Active"), ("2", "Retired"));
        var published = BuildPublishedResource() with { Subtypes = subtypes };

        var outcome = MigrationCatalogReconciler.ReconcileResource(
            inventory, published, expectedSubtypes: subtypes);

        outcome.Findings.Should().NotContain(f =>
            f.Code == MigrationCatalogReconciliationCodes.SubtypeMissing ||
            f.Code == MigrationCatalogReconciliationCodes.SubtypeFieldMismatch ||
            f.Code == MigrationCatalogReconciliationCodes.SubtypeCodeMissing);
    }

    private static MetadataV2Subtypes BuildSubtypes(string subtypeField, params (string Code, string Name)[] subtypes)
        => new()
        {
            SubtypeField = subtypeField,
            Subtypes = subtypes
                .Select(s => new MetadataV2Subtype
                {
                    Code = JsonDocument.Parse($"\"{s.Code}\"").RootElement,
                    Name = s.Name
                })
                .ToArray()
        };
}
