// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.FileImport.Services.FileGdb;
namespace Honua.Core.Features.Migration.Domain;

/// <summary>
/// Captured OGC API Features source facts used to build a migration source inventory.
/// </summary>
public sealed record OgcApiFeaturesMigrationSourceSnapshot
{
    /// <summary>
    /// Canonical landing page URL used as the scan root.
    /// </summary>
    public required string BaseUrl { get; init; }

    /// <summary>
    /// Human-readable landing page title when advertised.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Landing page description when advertised.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Source API version or implementation version when reported.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Links advertised by the landing page.
    /// </summary>
    public OgcApiFeaturesLink[] LandingPageLinks { get; init; } = [];

    /// <summary>
    /// Conformance classes returned by the source conformance endpoint.
    /// </summary>
    public string[] ConformanceClasses { get; init; } = [];

    /// <summary>
    /// Feature collections returned by the source collections endpoint.
    /// </summary>
    public OgcApiFeaturesCollectionSnapshot[] Collections { get; init; } = [];

    /// <summary>
    /// Source-level CRS declarations that apply when a collection omits its own CRS metadata.
    /// </summary>
    public OgcApiFeaturesCrsDeclaration[] CrsDeclarations { get; init; } = [];

    /// <summary>
    /// Source-level vendor extensions or non-standard capability names observed during the scan.
    /// </summary>
    public string[] VendorExtensions { get; init; } = [];
}

/// <summary>
/// Captured OGC API Features collection facts used for migration planning.
/// </summary>
public sealed record OgcApiFeaturesCollectionSnapshot
{
    /// <summary>
    /// OGC API Features collection identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Human-readable collection title when advertised.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Collection description when advertised.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Source-reported geometry type when known.
    /// </summary>
    public string? GeometryType { get; init; }

    /// <summary>
    /// Source-reported feature count when known.
    /// </summary>
    public int? FeatureCount { get; init; }

    /// <summary>
    /// Links advertised by the collection metadata document.
    /// </summary>
    public OgcApiFeaturesLink[] Links { get; init; } = [];

    /// <summary>
    /// Pagination links observed from an items page probe.
    /// </summary>
    public OgcApiFeaturesLink[] PaginationLinks { get; init; } = [];

    /// <summary>
    /// CRS declarations advertised by the collection metadata document.
    /// </summary>
    public OgcApiFeaturesCrsDeclaration[] CrsDeclarations { get; init; } = [];

    /// <summary>
    /// Representation formats advertised for collection items.
    /// </summary>
    public string[] ItemEncodings { get; init; } = [];

    /// <summary>
    /// Field metadata captured from collection schema or queryables documents.
    /// </summary>
    public MigrationInventoryField[] Fields { get; init; } = [];

    /// <summary>
    /// Collection-level vendor extensions or non-standard capability names observed during the scan.
    /// </summary>
    public string[] VendorExtensions { get; init; } = [];
}

/// <summary>
/// Link relation, media type, and href captured from an OGC API Features document.
/// </summary>
public sealed record OgcApiFeaturesLink
{
    /// <summary>
    /// Link relation value.
    /// </summary>
    public required string Rel { get; init; }

    /// <summary>
    /// Link target URL. Relative values are resolved against the scan base URL.
    /// </summary>
    public required string Href { get; init; }

    /// <summary>
    /// Link media type when advertised.
    /// </summary>
    public string? Type { get; init; }

    /// <summary>
    /// Link title when advertised.
    /// </summary>
    public string? Title { get; init; }
}

/// <summary>
/// CRS declaration captured from an OGC API Features service or collection document.
/// </summary>
public sealed record OgcApiFeaturesCrsDeclaration
{
    /// <summary>
    /// Declaration role such as <c>storage</c>, <c>supported</c>, or <c>items</c>.
    /// </summary>
    public required string Role { get; init; }

    /// <summary>
    /// Source-provided CRS identifier or URI.
    /// </summary>
    public required string Value { get; init; }
}
