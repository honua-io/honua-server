// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Models;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Microsoft.Extensions.Primitives;

namespace Honua.Protocols.GeoServices.FeatureServer;

internal static partial class FeatureServerEndpoints
{
    /// <summary>
    /// Applies geometry, spatialRel, time, and objectIds onto an analytics query.
    /// queryBins, queryDateBins, and queryTopFeatures accept these parameters; the
    /// feature-query builders already honor <see cref="FeatureQuery.SpatialFilter"/>,
    /// <see cref="FeatureQuery.SqlFilter"/>, and <see cref="FeatureQuery.ObjectIds"/>.
    /// </summary>
    internal static async Task<(FeatureQuery Query, IResult? Error)> ApplyAnalyticsFiltersAsync(
        HttpContext context,
        MetadataV2Resource resource,
        IReadOnlyDictionary<string, StringValues> values,
        FeatureQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(values);

        if (!HasAnalyticsFilter(values))
        {
            return (query, null);
        }

        if (!TryParseQueryParameters(values, out var parameters, out var parseError))
        {
            return (query, StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [parseError ?? "Invalid query parameter."]));
        }

        if (!string.IsNullOrWhiteSpace(parameters.Geometry))
        {
            var (spatialQuery, spatialError) = await ApplyAnalyticsSpatialFilterAsync(
                context, resource, parameters, query, cancellationToken).ConfigureAwait(false);
            if (spatialError != null)
            {
                return (query, spatialError);
            }

            query = spatialQuery;
        }

        if (!string.IsNullOrWhiteSpace(parameters.Time))
        {
            var (temporalQuery, temporalError) = ApplyAnalyticsTemporalFilter(context, resource, parameters, query);
            if (temporalError != null)
            {
                return (query, temporalError);
            }

            query = temporalQuery;
        }

        if (parameters.ObjectIds is { Length: > 0 })
        {
            var (objectIdQuery, objectIdError) = ApplyAnalyticsObjectIds(context, resource, parameters.ObjectIds, query);
            if (objectIdError != null)
            {
                return (query, objectIdError);
            }

            query = objectIdQuery;
        }

        return (query, null);
    }

    private static bool HasAnalyticsFilter(IReadOnlyDictionary<string, StringValues> values)
    {
        foreach (var key in values.Keys)
        {
            if (key.Equals("geometry", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("spatialRel", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("time", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("timeFilter", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("objectIds", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("distance", StringComparison.OrdinalIgnoreCase))
            {
                var raw = values[key].ToString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static async Task<(FeatureQuery Query, IResult? Error)> ApplyAnalyticsSpatialFilterAsync(
        HttpContext context,
        MetadataV2Resource resource,
        QueryParameters parameters,
        FeatureQuery query,
        CancellationToken cancellationToken)
    {
        if (!GeoServicesGeometryParser.TryParseGeoServicesGeometry(
                parameters.Geometry,
                parameters.GeometryType,
                out var parsedGeometry,
                out var geometryError) ||
            parsedGeometry is null)
        {
            return (query, StandardErrorHelpers.CreateBadRequest(context,
                "Invalid geometry parameter",
                [geometryError ?? "Geometry parameter is invalid."]));
        }

        var queryServices = context.RequestServices.GetRequiredService<IFeatureServerQueryServices>();
        int? inputSrid;
        try
        {
            inputSrid = await queryServices
                .ResolveSridAsync(parameters.InSr, parsedGeometry.SpatialReference, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            return (query, StandardErrorHelpers.CreateBadRequest(context,
                "Invalid input spatial reference",
                [ex.Message]));
        }

        if (parameters.InSrSpecified && !inputSrid.HasValue)
        {
            return (query, StandardErrorHelpers.CreateBadRequest(context,
                "Invalid input spatial reference",
                ["inSR is not a supported spatial reference."]));
        }

        inputSrid ??= resource.ReadSrid() ?? SpatialReference.WGS84.Wkid;

        double? linearMetersPerUnit = null;
        if (parameters.Distance is > 0 && string.IsNullOrWhiteSpace(parameters.Units))
        {
            var classifier = context.RequestServices.GetService<IGeographicSridClassifier>();
            if (classifier is not null)
            {
                linearMetersPerUnit = await classifier
                    .TryResolveLinearUnitFactorAsync(inputSrid.Value, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        try
        {
            var spatialFilter = GeoServicesSpatialFilterBuilder.BuildSpatialFilter(
                parameters,
                parsedGeometry,
                inputSrid,
                GeoServicesGeometryConverter.ResolveConfiguredCurveVertexBudget(context.RequestServices),
                linearMetersPerUnit);
            return (query with { SpatialFilter = spatialFilter }, null);
        }
        catch (ArgumentException ex)
        {
            return (query, StandardErrorHelpers.CreateBadRequest(context,
                "Invalid spatial relationship",
                [ex.Message]));
        }
    }

    private static (FeatureQuery Query, IResult? Error) ApplyAnalyticsTemporalFilter(
        HttpContext context,
        MetadataV2Resource resource,
        QueryParameters parameters,
        FeatureQuery query)
    {
        FilterExpression? temporalExpression;
        try
        {
            temporalExpression = GeoServicesTemporalQueryBuilder.BuildTemporalExpression(
                parameters.Time,
                parameters.TimeRelation,
                resource);
        }
        catch (ArgumentException ex)
        {
            return (query, StandardErrorHelpers.CreateBadRequest(context,
                "Invalid time parameter",
                [ex.Message]));
        }

        if (temporalExpression is null)
        {
            return (query, null);
        }

        var filterService = context.RequestServices.GetRequiredService<IFilterExpressionService>();
        var translation = filterService.Translate(temporalExpression, resource);
        if (!translation.IsSuccess || translation.SqlFilter is null)
        {
            return (query, StandardErrorHelpers.CreateBadRequest(context,
                "Invalid time parameter",
                [translation.ErrorMessage ?? "Invalid time parameter."]));
        }

        return AndAnalyticsSqlFilter(context, resource, filterService, query, translation.SqlFilter);
    }

    private static (FeatureQuery Query, IResult? Error) ApplyAnalyticsObjectIds(
        HttpContext context,
        MetadataV2Resource resource,
        long[] objectIds,
        FeatureQuery query)
    {
        var objectIdField = GeoServicesObjectIdFieldResolver.ResolveObjectIdField(resource);
        if (objectIdField is null ||
            objectIdField.Name.Equals(FieldNames.ObjectId, StringComparison.OrdinalIgnoreCase))
        {
            return (query with { ObjectIds = objectIds.ToImmutableArray() }, null);
        }

        var expression = new BinaryExpression(
            new PropertyReference(objectIdField.Name),
            BinaryOperator.In,
            new ValueList(objectIds
                .Distinct()
                .Select(static objectId => (FilterExpression)new Literal(objectId, LiteralType.Number))
                .ToArray()));
        var filterService = context.RequestServices.GetRequiredService<IFilterExpressionService>();
        var translation = filterService.Translate(expression, resource);
        if (!translation.IsSuccess || translation.SqlFilter is null)
        {
            return (query, StandardErrorHelpers.CreateBadRequest(context,
                "Invalid objectIds parameter",
                [translation.ErrorMessage ?? "Invalid objectIds parameter."]));
        }

        return AndAnalyticsSqlFilter(context, resource, filterService, query, translation.SqlFilter);
    }

    private static (FeatureQuery Query, IResult? Error) AndAnalyticsSqlFilter(
        HttpContext context,
        MetadataV2Resource resource,
        IFilterExpressionService filterService,
        FeatureQuery query,
        SqlFragment addition)
    {
        if (query.SqlFilter is not null)
        {
            return (query with { SqlFilter = SqlFragmentHelpers.CombineSqlFilters(query.SqlFilter, addition) }, null);
        }

        if (string.IsNullOrWhiteSpace(query.Where) || query.Where.Trim() == "1=1")
        {
            return (query with { SqlFilter = addition, Where = null }, null);
        }

        var parseResult = filterService.Parse(FilterLanguage.ArcGisSql, query.Where);
        if (!parseResult.IsSuccess)
        {
            return (query, StandardErrorHelpers.CreateBadRequest(context,
                "Invalid where clause",
                [parseResult.ErrorMessage ?? "Invalid filter syntax."]));
        }

        if (parseResult.Expression is null)
        {
            return (query with { SqlFilter = addition, Where = null }, null);
        }

        var translation = filterService.Translate(parseResult.Expression, resource);
        if (!translation.IsSuccess || translation.SqlFilter is null)
        {
            return (query, StandardErrorHelpers.CreateBadRequest(context,
                "Invalid where clause",
                [translation.ErrorMessage ?? "Invalid filter syntax."]));
        }

        return (query with
        {
            SqlFilter = SqlFragmentHelpers.CombineSqlFilters(translation.SqlFilter, addition),
            Where = null
        }, null);
    }
}
