// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

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
}
