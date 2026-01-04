// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.FeatureServer.Models;

namespace Honua.Server.Features.FeatureServer.Services.QueryBuilding;

/// <summary>
/// Strategy interface for building feature queries from various parameter sources.
/// Supports different query building strategies for different protocols and requirements.
/// </summary>
internal interface IFeatureQueryBuilder
{
    /// <summary>
    /// Builds a feature query from the provided parameters and context
    /// </summary>
    /// <param name="context">Query building context with parameters and metadata</param>
    /// <returns>Configured feature query</returns>
    FeatureQuery BuildQuery(QueryBuildingContext context);
}

/// <summary>
/// Context object containing all data needed to build a feature query
/// </summary>
public sealed record QueryBuildingContext
{
    public required QueryParameters QueryParams { get; init; }
    public required ServiceDefinition Service { get; init; }
    public required LayerDefinition Layer { get; init; }
    public GeoServicesGeometry? ParsedGeometry { get; init; }
    public int? InputSrid { get; init; }
    public int? OutputSrid { get; init; }
    public SqlFragment? SqlFilter { get; init; }
}

/// <summary>
/// Context object for building related record queries
/// </summary>
public sealed record RelatedQueryBuildingContext
{
    public required QueryRelatedRecordsParameters QueryParams { get; init; }
    public required long[] ObjectIds { get; init; }
    public required Relationship Relationship { get; init; }
}
