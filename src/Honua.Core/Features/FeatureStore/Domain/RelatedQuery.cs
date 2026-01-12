// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Queries.Filters;

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Represents a query for related features through layer relationships
/// </summary>
public readonly record struct RelatedQuery
{
    /// <summary>
    /// Object IDs of the origin features to find related records for
    /// </summary>
    public required long[] ObjectIds { get; init; }

    /// <summary>
    /// Relationship definition that describes the connection between layers
    /// </summary>
    public required Relationship Relationship { get; init; }

    /// <summary>
    /// WHERE clause filter expression for the related records (GeoServices REST SQL syntax)
    /// </summary>
    public string? Where { get; init; }

    /// <summary>
    /// Translated SQL filter fragment for related records.
    /// </summary>
    public SqlFragment? SqlFilter { get; init; }

    /// <summary>
    /// Fields to return from related records (null means all fields)
    /// </summary>
    public ImmutableArray<string>? OutFields { get; init; }

    /// <summary>
    /// Maximum number of related records to return per origin object
    /// </summary>
    public int? Limit { get; init; }

    /// <summary>
    /// Number of related records to skip for pagination
    /// </summary>
    public int? Offset { get; init; }

    /// <summary>
    /// Creates a simple related query for specific object IDs
    /// </summary>
    /// <param name="objectIds">Origin object IDs</param>
    /// <param name="relationship">Relationship definition</param>
    /// <returns>Related query instance</returns>
    public static RelatedQuery ForObjects(long[] objectIds, Relationship relationship)
        => new() { ObjectIds = objectIds, Relationship = relationship };

    /// <summary>
    /// Creates a filtered related query
    /// </summary>
    /// <param name="objectIds">Origin object IDs</param>
    /// <param name="relationship">Relationship definition</param>
    /// <param name="where">WHERE clause for related records</param>
    /// <returns>Related query instance</returns>
    public static RelatedQuery WithFilter(long[] objectIds, Relationship relationship, string where)
        => new() { ObjectIds = objectIds, Relationship = relationship, Where = where };
}
