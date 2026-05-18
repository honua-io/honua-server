// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Projections;

/// <summary>
/// Minimal, projection-scoped catalog metadata semantics used to evaluate metadata v2 readiness.
/// </summary>
public sealed record CatalogMetadataSemantics
{
    public string? Title { get; init; }

    public string? Summary { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<CatalogIdentifierSemantic> Identifiers { get; init; } = Array.Empty<CatalogIdentifierSemantic>();

    public IReadOnlyList<CatalogContactSemantic> Contacts { get; init; } = Array.Empty<CatalogContactSemantic>();

    public CatalogRightsSemantic? Rights { get; init; }

    public IReadOnlyList<CatalogDateSemantic> Dates { get; init; } = Array.Empty<CatalogDateSemantic>();

    public CatalogExtentSemantic? Extents { get; init; }

    public CatalogLineageSemantic? Lineage { get; init; }

    public IReadOnlyList<CatalogQualitySemantic> Quality { get; init; } = Array.Empty<CatalogQualitySemantic>();

    public IReadOnlyList<CatalogLinkSemantic> Links { get; init; } = Array.Empty<CatalogLinkSemantic>();

    public IReadOnlyList<CatalogDistributionSemantic> Distributions { get; init; } = Array.Empty<CatalogDistributionSemantic>();

    public IReadOnlyList<FieldSemanticBinding> Fields { get; init; } = Array.Empty<FieldSemanticBinding>();
}

/// <summary>
/// Identifier metadata for a catalog resource.
/// </summary>
public sealed record CatalogIdentifierSemantic(
    string Value,
    CatalogIdentifierKind Kind = CatalogIdentifierKind.Local,
    string? Authority = null,
    bool IsPrimary = false);

/// <summary>
/// Identifier authority or representation kind.
/// </summary>
public enum CatalogIdentifierKind
{
    Local,
    Uri,
    Uuid,
    Doi,
    Catalog,
    External
}

/// <summary>
/// Contact metadata for a catalog resource.
/// </summary>
public sealed record CatalogContactSemantic(
    string Name,
    CatalogContactRole Role = CatalogContactRole.PointOfContact,
    string? Organization = null,
    string? Email = null,
    string? Url = null);

/// <summary>
/// Catalog contact responsibility role.
/// </summary>
public enum CatalogContactRole
{
    PointOfContact,
    Originator,
    Author,
    Publisher,
    Custodian,
    Distributor,
    Owner
}

/// <summary>
/// Rights, license, and attribution metadata for a catalog resource.
/// </summary>
public sealed record CatalogRightsSemantic(
    string? LicenseCode = null,
    string? LicenseTitle = null,
    Uri? LicenseUri = null,
    string? RightsStatement = null,
    string? Attribution = null);

/// <summary>
/// Date value with a catalog-specific semantic role.
/// </summary>
public sealed record CatalogDateSemantic(
    DateTimeOffset Value,
    CatalogDateRole Role);

/// <summary>
/// Semantic role for a catalog date value.
/// </summary>
public enum CatalogDateRole
{
    Created,
    Modified,
    Published,
    Issued,
    ValidFrom,
    ValidTo,
    TemporalInstant,
    TemporalStart,
    TemporalEnd
}

/// <summary>
/// Spatial and temporal extent metadata for a catalog resource.
/// </summary>
public sealed record CatalogExtentSemantic
{
    public CatalogSpatialExtentSemantic? Spatial { get; init; }

    public CatalogTemporalExtentSemantic? Temporal { get; init; }
}

/// <summary>
/// Bounding-box extent metadata for catalog projections.
/// </summary>
public sealed record CatalogSpatialExtentSemantic(
    double West,
    double South,
    double East,
    double North,
    string Crs = "EPSG:4326");

/// <summary>
/// Temporal extent metadata for catalog projections.
/// </summary>
public sealed record CatalogTemporalExtentSemantic(
    DateTimeOffset? Start = null,
    DateTimeOffset? End = null,
    DateTimeOffset? Instant = null);

/// <summary>
/// Lineage metadata describing source and processing history.
/// </summary>
public sealed record CatalogLineageSemantic(
    string Statement,
    CatalogLineageScope Scope = CatalogLineageScope.Dataset,
    IReadOnlyList<string>? Sources = null,
    IReadOnlyList<string>? ProcessSteps = null);

/// <summary>
/// Scope for a lineage statement.
/// </summary>
public enum CatalogLineageScope
{
    Dataset,
    Collection,
    Item,
    Field,
    Service
}

/// <summary>
/// Quality metadata used by catalog projection readiness checks.
/// </summary>
public sealed record CatalogQualitySemantic(
    string Name,
    CatalogQualityKind Kind = CatalogQualityKind.Statement,
    string? Value = null,
    bool? Flag = null);

/// <summary>
/// Quality metadata category.
/// </summary>
public enum CatalogQualityKind
{
    Statement,
    Completeness,
    LogicalConsistency,
    PositionalAccuracy,
    TemporalAccuracy,
    ThematicAccuracy,
    Flag
}

/// <summary>
/// Hypermedia link metadata for catalog resources.
/// </summary>
public sealed record CatalogLinkSemantic(
    Uri Href,
    CatalogLinkRelation Relation = CatalogLinkRelation.Related,
    string? MediaType = null,
    string? Title = null);

/// <summary>
/// Catalog link relation vocabulary.
/// </summary>
public enum CatalogLinkRelation
{
    Self,
    Alternate,
    Canonical,
    DescribedBy,
    License,
    Preview,
    Related,
    Service,
    Data
}

/// <summary>
/// Distribution endpoint or artifact metadata for catalog resources.
/// </summary>
public sealed record CatalogDistributionSemantic(
    Uri Href,
    CatalogDistributionKind Kind = CatalogDistributionKind.Download,
    string? MediaType = null,
    string? Title = null,
    string? Format = null);

/// <summary>
/// Distribution access kind.
/// </summary>
public enum CatalogDistributionKind
{
    Download,
    Api,
    Tile,
    Asset,
    Service,
    Documentation
}
