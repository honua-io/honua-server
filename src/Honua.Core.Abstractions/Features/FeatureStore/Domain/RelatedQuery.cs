// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
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
    /// Storage layer identifier of the related resource.
    /// </summary>
    public int? RelatedLayerId { get; init; }

    /// <summary>
    /// Field on the source resource whose values are used to locate related rows.
    /// </summary>
    public string? OriginForeignKeyField { get; init; }

    /// <summary>
    /// Field on the related resource matched against the source values.
    /// </summary>
    public string? DestinationForeignKeyField { get; init; }

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
    /// Creates a related query without requiring protocol-specific relationship metadata.
    /// </summary>
    /// <param name="objectIds">Origin object IDs</param>
    /// <param name="relatedLayerId">Storage layer identifier of the related resource</param>
    /// <param name="originForeignKeyField">Source resource field to read</param>
    /// <param name="destinationForeignKeyField">Related resource field to match</param>
    /// <returns>Related query instance</returns>
    public static RelatedQuery ForObjects(
        long[] objectIds,
        int relatedLayerId,
        string originForeignKeyField,
        string destinationForeignKeyField)
        => new()
        {
            ObjectIds = objectIds,
            RelatedLayerId = relatedLayerId,
            OriginForeignKeyField = originForeignKeyField,
            DestinationForeignKeyField = destinationForeignKeyField
        };
}
