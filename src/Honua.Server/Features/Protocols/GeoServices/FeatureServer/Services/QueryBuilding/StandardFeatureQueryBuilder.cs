// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Protocols.GeoServices;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Services;
using Honua.Server.Features.Infrastructure.Validation;

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer.Services.QueryBuilding;

/// <summary>
/// Standard implementation of feature query builder for typical GeoServices REST queries.
/// Handles spatial filters, object ID filters, field selection, and ordering.
/// </summary>
internal sealed class StandardFeatureQueryBuilder : IFeatureQueryBuilder
{
    /// <summary>
    /// Builds a standard feature query from the provided context
    /// </summary>
    public FeatureQuery BuildQuery(QueryBuildingContext context)
    {
        var hasObjectIds = context.QueryParams.ObjectIds is { Length: > 0 };
        var effectiveSqlFilter = hasObjectIds ? null : context.SqlFilter;
        var effectiveWhere = hasObjectIds ? null : context.QueryParams.Where;

        var query = new FeatureQuery
        {
            Where = effectiveWhere,
            SqlFilter = effectiveSqlFilter,
            ObjectIds = hasObjectIds ? context.QueryParams.ObjectIds?.ToImmutableArray() : null,
            Offset = context.QueryParams.ResultOffset,
            Limit = context.QueryParams.ResultRecordCount,
            SpatialReferenceSrid = context.Layer.SpatialReference.ToSrid(),
            OutputSrid = context.OutputSrid,
            OrderBy = OrderByParsing.ParseFeatureServerOrderBy(
                context.QueryParams.OrderByFields,
                context.Layer,
                FeatureServerOrderByFields.AllowedCoreOrderByFields)
        };

        // Apply field selection
        query = ApplyFieldSelection(query, context.QueryParams.OutFields);

        // Apply spatial filter if geometry or nearest count is specified
        query = ApplySpatialFilter(query, context);

        return query;
    }

    private static FeatureQuery ApplyFieldSelection(FeatureQuery query, string? outFields)
    {
        if (string.IsNullOrEmpty(outFields))
        {
            return query;
        }

        if (outFields == "*")
        {
            // Return all fields - no filtering needed
            return query with { OutFields = null };
        }

        var fields = outFields.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim())
            .ToImmutableArray();

        return query with { OutFields = fields };
    }

    private static FeatureQuery ApplySpatialFilter(FeatureQuery query, QueryBuildingContext context)
    {
        if (context.ParsedGeometry == null && !context.QueryParams.NearestCount.HasValue)
        {
            return query;
        }

        try
        {
            // For KNN queries without explicit geometry, geometry is required
            if (context.QueryParams.NearestCount.HasValue && context.ParsedGeometry == null)
            {
                throw new InvalidOperationException("Geometry is required for nearest neighbor queries");
            }

            var spatialFilter = GeoServicesSpatialFilterBuilder.BuildSpatialFilter(
                context.QueryParams,
                context.ParsedGeometry!,
                context.InputSrid);

            if (CanUsePointEnvelopeIntersectsFastPath(context, spatialFilter))
            {
                spatialFilter = spatialFilter with { SpatialRelationship = SpatialRelationship.EnvelopeIntersects };
            }

            return query with { SpatialFilter = spatialFilter };
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException("Invalid spatial parameters.", ex);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException("Invalid geometry.", ex);
        }
    }

    private static bool CanUsePointEnvelopeIntersectsFastPath(
        QueryBuildingContext context,
        SpatialFilter spatialFilter)
    {
        if (context.Layer.GeometryType != GeometryType.Point ||
            spatialFilter.SpatialRelationship != SpatialRelationship.Intersects)
        {
            return false;
        }

        if (!string.Equals(context.QueryParams.GeometryType, "esriGeometryEnvelope", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var geometry = context.ParsedGeometry;
        return geometry is
        {
            Xmin: not null,
            Ymin: not null,
            Xmax: not null,
            Ymax: not null
        };
    }

}
