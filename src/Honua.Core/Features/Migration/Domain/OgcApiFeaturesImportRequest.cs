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
/// Request to import features from an OGC API Features collection into a Honua catalog target.
/// </summary>
public sealed record OgcApiFeaturesImportRequest
{
    /// <summary>
    /// Source OGC API Features landing page URL. The importer derives the collection items endpoint
    /// from this URL when the collection metadata is reachable.
    /// </summary>
    public required string ServiceUrl { get; init; }

    /// <summary>
    /// Identifier of the OGC API Features collection to import.
    /// </summary>
    public required string CollectionId { get; init; }

    /// <summary>
    /// Target schema in the catalog. Required; the importer creates the schema-qualified target table.
    /// </summary>
    public required string TargetSchema { get; init; }

    /// <summary>
    /// Target table name. Defaults to <see cref="CollectionId"/> when omitted. The importer sanitizes
    /// the identifier before issuing DDL.
    /// </summary>
    public string? TargetTable { get; init; }

    /// <summary>
    /// Maximum number of features requested per items page. The source may return fewer.
    /// </summary>
    public int PageSize { get; init; } = 500;

    /// <summary>
    /// Maximum number of features imported before the run stops voluntarily. Zero means no operator-imposed cap.
    /// </summary>
    public int MaxFeatures { get; init; }

    /// <summary>
    /// Overall import timeout in seconds. Zero falls back to the default.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 300;

    /// <summary>
    /// Maximum number of paged HTTP requests the importer may issue before bailing.
    /// </summary>
    public int MaxPages { get; init; } = 1_000;

    /// <summary>
    /// Whether HTTP and local URLs are permitted, for test or controlled operator environments.
    /// </summary>
    public bool AllowUnsafeLocalUrls { get; init; }

    /// <summary>
    /// Optional CQL2-text filter passed through to the source items endpoint as
    /// <c>filter=...&amp;filter-lang=cql2-text</c>. When set, the importer requests only the matching
    /// subset of features. The filter is supplied verbatim to the source after URL escaping; the
    /// importer does not parse, normalize, or rewrite the expression. Empty or whitespace-only
    /// values are rejected by the importer (and by the API endpoint).
    /// </summary>
    public string? Filter { get; init; }

    /// <summary>
    /// Optional bounding box passed through to the source items endpoint as <c>bbox=</c>. The tuple
    /// MUST contain four numbers (<c>minLon,minLat,maxLon,maxLat</c>) or six numbers
    /// (<c>minLon,minLat,minElev,maxLon,maxLat,maxElev</c>). Any other length, NaN or infinite
    /// values, or a maximum less than the minimum on the same axis is rejected.
    /// </summary>
    public double[]? Bbox { get; init; }

    /// <summary>
    /// Optional datetime filter passed through to the source items endpoint as <c>datetime=</c>.
    /// Accepts an RFC3339 instant (<c>2020-01-01T00:00:00Z</c>), an interval with both endpoints
    /// (<c>start/end</c>), or an open-ended interval (<c>start/..</c> or <c>../end</c>). Whitespace
    /// is trimmed; otherwise the value is forwarded verbatim. Invalid syntax or an interval whose
    /// start is later than its end is rejected.
    /// </summary>
    public string? Datetime { get; init; }
}
