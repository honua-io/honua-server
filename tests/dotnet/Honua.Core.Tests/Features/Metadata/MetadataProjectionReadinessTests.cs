// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Projections;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Metadata;

[Protocol(Protocols.TestQuality)]
public sealed class MetadataProjectionReadinessTests
{
    [UnitTest]
    [Operation(Operations.Query)]
    public void TargetDefinitions_ExposeExpectedLabels()
    {
        MetadataProjectionTargets.GetLabel(MetadataProjectionTarget.OgcRecords).Should().Be("OGC Records");
        MetadataProjectionTargets.GetLabel(MetadataProjectionTarget.Dcat3).Should().Be("DCAT 3");
        MetadataProjectionTargets.GetLabel(MetadataProjectionTarget.StacCollection).Should().Be("STAC Collection");
        MetadataProjectionTargets.GetLabel(MetadataProjectionTarget.StacItem).Should().Be("STAC Item");
        MetadataProjectionTargets.GetLabel(MetadataProjectionTarget.Iso19115).Should().Be("ISO 19115/19139");
        MetadataProjectionTargets.GetLabel(MetadataProjectionTarget.EsriPortalItem)
            .Should().Be("Esri portal item/catalog item");
        MetadataProjectionTargets.GetLabel(MetadataProjectionTarget.GeoServicesRest)
            .Should().Be("GeoServices REST service/layer metadata");
        MetadataProjectionTargets.GetLabel(MetadataProjectionTarget.OgcApiFeaturesCollection)
            .Should().Be("OGC API Features collection metadata");
        MetadataProjectionTargets.GetLabel(MetadataProjectionTarget.ODataEdm).Should().Be("OData EDM metadata");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void FieldSemanticRole_Parse_PreservesKnownRoleAndRejectsBlank()
    {
        var role = FieldSemanticRole.Parse("geometry.primary");

        role.Value.Should().Be(FieldSemanticRoleVocabulary.GeometryPrimary);
        role.IsKnown.Should().BeTrue();
        FieldSemanticRoleVocabulary.All.Should().Contain(
            [
                FieldSemanticRoleVocabulary.IdentifierPrimary,
                FieldSemanticRoleVocabulary.DisplayTitle,
                FieldSemanticRoleVocabulary.GeometryPrimary,
                FieldSemanticRoleVocabulary.TemporalInstant,
                FieldSemanticRoleVocabulary.TemporalStart,
                FieldSemanticRoleVocabulary.TemporalEnd,
                FieldSemanticRoleVocabulary.MetadataCreated,
                FieldSemanticRoleVocabulary.MetadataModified,
                FieldSemanticRoleVocabulary.AssetHref,
                FieldSemanticRoleVocabulary.LicenseCode,
                FieldSemanticRoleVocabulary.QualityFlag,
                FieldSemanticRoleVocabulary.StatusLifecycle
            ]);

        FieldSemanticRole.TryParse(" ", out _).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void Evaluate_StacCollection_WithRequiredSemantics_IsReady()
    {
        var metadata = CreateBaselineMetadata();

        var result = ProjectionReadinessEvaluator.Evaluate(metadata, MetadataProjectionTarget.StacCollection);

        result.IsReady.Should().BeTrue();
        result.MissingRequired.Should().BeEmpty();
        result.MissingRecommended.Select(requirement => requirement.Semantic)
            .Should().NotContain(ProjectionMetadataSemantic.License);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void Evaluate_StacItem_CanUseDistributionAsAssetHref()
    {
        var metadata = CreateBaselineMetadata() with
        {
            Distributions =
            [
                new CatalogDistributionSemantic(
                    new Uri("https://example.test/data/buildings.parquet"),
                    CatalogDistributionKind.Asset)
            ]
        };

        var result = ProjectionReadinessEvaluator.Evaluate(metadata, MetadataProjectionTarget.StacItem);

        result.IsReady.Should().BeTrue();
        result.MissingRequired.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void Evaluate_OgcRecords_ReportsMissingRequiredBeforeRecommended()
    {
        var metadata = new CatalogMetadataSemantics
        {
            Description = "Parks and open space."
        };

        var result = ProjectionReadinessEvaluator.Evaluate(metadata, MetadataProjectionTarget.OgcRecords);

        result.IsReady.Should().BeFalse();
        result.MissingRequired.Select(requirement => requirement.Semantic)
            .Should().Equal(ProjectionMetadataSemantic.PrimaryIdentifier, ProjectionMetadataSemantic.Title);
        result.MissingRecommended.Select(requirement => requirement.Semantic)
            .Should().Contain(ProjectionMetadataSemantic.SpatialExtent);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void Evaluate_GeoServicesRest_UsesFieldRoles()
    {
        var metadata = new CatalogMetadataSemantics
        {
            Title = "Buildings",
            Fields =
            [
                new FieldSemanticBinding("shape", FieldSemanticRole.Parse(FieldSemanticRoleVocabulary.GeometryPrimary)),
                new FieldSemanticBinding("observed_at", FieldSemanticRole.Parse(FieldSemanticRoleVocabulary.TemporalInstant))
            ]
        };

        var result = ProjectionReadinessEvaluator.Evaluate(metadata, MetadataProjectionTarget.GeoServicesRest);

        result.IsReady.Should().BeTrue();
        result.MissingRequired.Should().BeEmpty();
        result.MissingRecommended.Select(requirement => requirement.Semantic)
            .Should().NotContain(ProjectionMetadataSemantic.TemporalField);
    }

    private static CatalogMetadataSemantics CreateBaselineMetadata() => new()
    {
        Title = "Building footprints",
        Summary = "Current building footprint catalog.",
        Description = "Building footprint polygons maintained for catalog projection tests.",
        Identifiers =
        [
            new CatalogIdentifierSemantic("buildings", CatalogIdentifierKind.Local, IsPrimary: true)
        ],
        Contacts =
        [
            new CatalogContactSemantic("Catalog Steward", CatalogContactRole.PointOfContact, "Honua")
        ],
        Rights = new CatalogRightsSemantic(LicenseCode: "CC-BY-4.0"),
        Extents = new CatalogExtentSemantic
        {
            Spatial = new CatalogSpatialExtentSemantic(-158.3, 21.2, -157.6, 21.8),
            Temporal = new CatalogTemporalExtentSemantic(Start: new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero))
        },
        Links =
        [
            new CatalogLinkSemantic(new Uri("https://example.test/catalog/buildings"), CatalogLinkRelation.Canonical)
        ]
    };
}
