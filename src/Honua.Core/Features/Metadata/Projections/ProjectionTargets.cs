// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Projections;

/// <summary>
/// Supported catalog or protocol metadata projection target.
/// </summary>
public enum MetadataProjectionTarget
{
    OgcRecords,
    Dcat3,
    StacCollection,
    StacItem,
    Iso19115,
    EsriPortalItem,
    GeoServicesRest,
    OgcApiFeaturesCollection,
    ODataEdm
}

/// <summary>
/// Importance of a metadata projection requirement.
/// </summary>
public enum ProjectionRequirementImportance
{
    Required,
    Recommended
}

/// <summary>
/// Metadata semantic that may be required by a projection target.
/// </summary>
public enum ProjectionMetadataSemantic
{
    PrimaryIdentifier,
    Title,
    Summary,
    Description,
    Contact,
    License,
    Rights,
    CreatedDate,
    ModifiedDate,
    SpatialExtent,
    TemporalExtent,
    Lineage,
    Quality,
    Link,
    Distribution,
    PrimaryGeometryField,
    TemporalField,
    AssetHrefField,
    LicenseCodeField,
    QualityFlagField,
    LifecycleStatusField
}

/// <summary>
/// One semantic requirement for a projection target.
/// </summary>
public sealed record ProjectionRequirement(
    ProjectionMetadataSemantic Semantic,
    string Label,
    ProjectionRequirementImportance Importance);

/// <summary>
/// Definition of a metadata projection target and its readiness requirements.
/// </summary>
public sealed record MetadataProjectionTargetDefinition(
    MetadataProjectionTarget Target,
    string Label,
    string Slug,
    IReadOnlyList<ProjectionRequirement> Requirements);

/// <summary>
/// Registry of built-in metadata projection target definitions.
/// </summary>
public static class MetadataProjectionTargets
{
    private static readonly IReadOnlyList<MetadataProjectionTargetDefinition> Definitions = Array.AsReadOnly(
    [
        Define(
            MetadataProjectionTarget.OgcRecords,
            "OGC Records",
            "ogc-records",
            Required(ProjectionMetadataSemantic.PrimaryIdentifier, "primary identifier"),
            Required(ProjectionMetadataSemantic.Title, "title"),
            Recommended(ProjectionMetadataSemantic.Description, "description"),
            Recommended(ProjectionMetadataSemantic.SpatialExtent, "spatial extent"),
            Recommended(ProjectionMetadataSemantic.TemporalExtent, "temporal extent"),
            Recommended(ProjectionMetadataSemantic.Contact, "contact"),
            Recommended(ProjectionMetadataSemantic.License, "license"),
            Recommended(ProjectionMetadataSemantic.Link, "link")),
        Define(
            MetadataProjectionTarget.Dcat3,
            "DCAT 3",
            "dcat-3",
            Required(ProjectionMetadataSemantic.PrimaryIdentifier, "primary identifier"),
            Required(ProjectionMetadataSemantic.Title, "title"),
            Recommended(ProjectionMetadataSemantic.Description, "description"),
            Recommended(ProjectionMetadataSemantic.Contact, "contact"),
            Recommended(ProjectionMetadataSemantic.License, "license"),
            Recommended(ProjectionMetadataSemantic.Rights, "rights statement"),
            Recommended(ProjectionMetadataSemantic.SpatialExtent, "spatial extent"),
            Recommended(ProjectionMetadataSemantic.TemporalExtent, "temporal extent"),
            Recommended(ProjectionMetadataSemantic.Distribution, "distribution"),
            Recommended(ProjectionMetadataSemantic.ModifiedDate, "modified date")),
        Define(
            MetadataProjectionTarget.StacCollection,
            "STAC Collection",
            "stac-collection",
            Required(ProjectionMetadataSemantic.PrimaryIdentifier, "collection id"),
            Required(ProjectionMetadataSemantic.Title, "title"),
            Required(ProjectionMetadataSemantic.Description, "description"),
            Required(ProjectionMetadataSemantic.SpatialExtent, "spatial extent"),
            Required(ProjectionMetadataSemantic.TemporalExtent, "temporal extent"),
            Recommended(ProjectionMetadataSemantic.License, "license"),
            Recommended(ProjectionMetadataSemantic.Link, "link"),
            Recommended(ProjectionMetadataSemantic.Quality, "quality statement")),
        Define(
            MetadataProjectionTarget.StacItem,
            "STAC Item",
            "stac-item",
            Required(ProjectionMetadataSemantic.PrimaryIdentifier, "item id"),
            Required(ProjectionMetadataSemantic.SpatialExtent, "spatial extent"),
            Required(ProjectionMetadataSemantic.TemporalExtent, "temporal extent"),
            Required(ProjectionMetadataSemantic.AssetHrefField, "asset href field or distribution"),
            Recommended(ProjectionMetadataSemantic.Title, "title"),
            Recommended(ProjectionMetadataSemantic.Description, "description"),
            Recommended(ProjectionMetadataSemantic.License, "license")),
        Define(
            MetadataProjectionTarget.Iso19115,
            "ISO 19115/19139",
            "iso-19115-19139",
            Required(ProjectionMetadataSemantic.PrimaryIdentifier, "file identifier"),
            Required(ProjectionMetadataSemantic.Title, "citation title"),
            Required(ProjectionMetadataSemantic.Contact, "responsible party"),
            Recommended(ProjectionMetadataSemantic.Description, "abstract"),
            Recommended(ProjectionMetadataSemantic.CreatedDate, "created date"),
            Recommended(ProjectionMetadataSemantic.ModifiedDate, "modified date"),
            Recommended(ProjectionMetadataSemantic.SpatialExtent, "spatial extent"),
            Recommended(ProjectionMetadataSemantic.TemporalExtent, "temporal extent"),
            Recommended(ProjectionMetadataSemantic.Lineage, "lineage statement"),
            Recommended(ProjectionMetadataSemantic.Quality, "quality statement")),
        Define(
            MetadataProjectionTarget.EsriPortalItem,
            "Esri portal item/catalog item",
            "esri-portal-item",
            Required(ProjectionMetadataSemantic.Title, "title"),
            Required(ProjectionMetadataSemantic.Description, "description"),
            Recommended(ProjectionMetadataSemantic.PrimaryIdentifier, "item id"),
            Recommended(ProjectionMetadataSemantic.Summary, "summary"),
            Recommended(ProjectionMetadataSemantic.License, "license"),
            Recommended(ProjectionMetadataSemantic.Rights, "access and use constraints"),
            Recommended(ProjectionMetadataSemantic.Link, "item link"),
            Recommended(ProjectionMetadataSemantic.Distribution, "distribution")),
        Define(
            MetadataProjectionTarget.GeoServicesRest,
            "GeoServices REST service/layer metadata",
            "geoservices-rest",
            Required(ProjectionMetadataSemantic.Title, "name or title"),
            Required(ProjectionMetadataSemantic.PrimaryGeometryField, "primary geometry field"),
            Recommended(ProjectionMetadataSemantic.PrimaryIdentifier, "layer id"),
            Recommended(ProjectionMetadataSemantic.Description, "description"),
            Recommended(ProjectionMetadataSemantic.SpatialExtent, "extent"),
            Recommended(ProjectionMetadataSemantic.TemporalField, "time field"),
            Recommended(ProjectionMetadataSemantic.Rights, "copyright or rights")),
        Define(
            MetadataProjectionTarget.OgcApiFeaturesCollection,
            "OGC API Features collection metadata",
            "ogc-api-features-collection",
            Required(ProjectionMetadataSemantic.PrimaryIdentifier, "collection id"),
            Required(ProjectionMetadataSemantic.Title, "title"),
            Recommended(ProjectionMetadataSemantic.Description, "description"),
            Recommended(ProjectionMetadataSemantic.SpatialExtent, "spatial extent"),
            Recommended(ProjectionMetadataSemantic.TemporalExtent, "temporal extent"),
            Recommended(ProjectionMetadataSemantic.Link, "links")),
        Define(
            MetadataProjectionTarget.ODataEdm,
            "OData EDM metadata",
            "odata-edm",
            Required(ProjectionMetadataSemantic.PrimaryIdentifier, "entity key"),
            Recommended(ProjectionMetadataSemantic.Title, "entity set title"),
            Recommended(ProjectionMetadataSemantic.PrimaryGeometryField, "spatial property"),
            Recommended(ProjectionMetadataSemantic.TemporalField, "temporal property"),
            Recommended(ProjectionMetadataSemantic.CreatedDate, "created timestamp"),
            Recommended(ProjectionMetadataSemantic.ModifiedDate, "modified timestamp"),
            Recommended(ProjectionMetadataSemantic.LifecycleStatusField, "lifecycle status field"))
    ]);

    public static IReadOnlyList<MetadataProjectionTargetDefinition> All => Definitions;

    public static MetadataProjectionTargetDefinition Get(MetadataProjectionTarget target) =>
        Definitions.First(definition => definition.Target == target);

    public static string GetLabel(MetadataProjectionTarget target) => Get(target).Label;

    private static MetadataProjectionTargetDefinition Define(
        MetadataProjectionTarget target,
        string label,
        string slug,
        params ProjectionRequirement[] requirements) =>
        new(target, label, slug, Array.AsReadOnly(requirements.ToArray()));

    private static ProjectionRequirement Required(ProjectionMetadataSemantic semantic, string label) =>
        new(semantic, label, ProjectionRequirementImportance.Required);

    private static ProjectionRequirement Recommended(ProjectionMetadataSemantic semantic, string label) =>
        new(semantic, label, ProjectionRequirementImportance.Recommended);
}
