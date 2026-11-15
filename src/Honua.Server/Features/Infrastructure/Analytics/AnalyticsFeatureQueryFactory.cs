// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Services;
using Honua.Protocols.GeoServices;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Server.Features.Protocols.SpatialAnalytics.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Honua.Infrastructure.Analytics;

// Distance-based relationships are rejected because these endpoints already use distance for two different radii.
internal static class AnalyticsFeatureQueryFactory
{
    public static async Task<(FeatureQuery? Query, IResult? Error)> TryBuildAsync(
        HttpContext context,
        IReadOnlyDictionary<string, StringValues> values,
        MetadataV2Object resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var selection = new LayerSelectionFilter
        {
            Where = GetValueString(values, SpatialAnalyticsParameters.Where),
            ObjectIds = GetValueString(values, SpatialAnalyticsParameters.ObjectIds),
            Geometry = GetValueString(values, SpatialAnalyticsParameters.Geometry),
            GeometryType = GetValueString(values, SpatialAnalyticsParameters.GeometryType),
            InSr = GetValueString(values, SpatialAnalyticsParameters.InSr),
            SpatialRel = GetValueString(values, SpatialAnalyticsParameters.SpatialRel),
            Time = GetValueString(values, SpatialAnalyticsParameters.Time),
            TimeRelation = GetValueString(values, SpatialAnalyticsParameters.TimeRelation),
        };

        var translation = await TranslateAsync(
            selection,
            resource,
            resource.ReadSrid() ?? SpatialReference.WGS84.ToSrid(),
            context.RequestServices,
            cancellationToken).ConfigureAwait(false);

        return translation.Query is { } query
            ? (query, null)
            : (null, StandardErrorHelpers.CreateBadRequest(
                context,
                translation.ErrorTitle ?? ErrorMessages.Validation.InvalidParameter,
                [translation.ErrorDetail ?? "Invalid filter syntax."]));
    }

    internal static async Task<LayerSelectionTranslation> TranslateAsync(
        LayerSelectionFilter selection,
        MetadataV2Object resource,
        int spatialReferenceSrid,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(services);

        var filterService = services.GetRequiredService<IFilterExpressionService>();

        // ----- where (ArcGIS SQL) -----
        var where = selection.Where;
        FilterExpression? whereExpression = null;
        if (!string.IsNullOrWhiteSpace(where))
        {
            var parseResult = filterService.Parse(FilterLanguage.GeoServicesSql, where);
            if (!parseResult.IsSuccess)
            {
                return LayerSelectionTranslation.Failure(
                    ErrorMessages.Validation.InvalidParameter,
                    parseResult.ErrorMessage ?? "Invalid filter syntax.");
            }

            whereExpression = parseResult.Expression;
        }

        // ----- time / timeRelation (FeatureServer style) -----
        // Built as a FilterExpression and ANDed with the where clause so a single
        // SqlFragment carries both predicates. Mirrors the main FeatureServer
        // query handler so the analytics surface honours the same temporal logic.
        FilterExpression? temporalExpression = null;
        if (!string.IsNullOrWhiteSpace(selection.Time))
        {
            try
            {
                temporalExpression = GeoServicesTemporalQueryBuilder.BuildTemporalExpression(
                    selection.Time, selection.TimeRelation, resource);
            }
            catch (ArgumentException ex)
            {
                return LayerSelectionTranslation.Failure(ErrorMessages.Validation.InvalidParameter, ex.Message);
            }
        }

        FilterExpression? combinedExpression = whereExpression;
        if (combinedExpression != null && temporalExpression != null)
        {
            combinedExpression = new BinaryExpression(combinedExpression, BinaryOperator.And, temporalExpression);
        }
        else
        {
            combinedExpression ??= temporalExpression;
        }

        SqlFragment? sqlFilter = null;
        if (combinedExpression != null)
        {
            var translationResult = filterService.Translate(combinedExpression, resource);
            if (!translationResult.IsSuccess)
            {
                return LayerSelectionTranslation.Failure(
                    ErrorMessages.Validation.InvalidParameter,
                    translationResult.ErrorMessage ?? "Invalid filter syntax.");
            }

            sqlFilter = translationResult.SqlFilter;
        }

        // ----- objectIds -----
        ImmutableArray<long>? objectIds = null;
        if (!string.IsNullOrWhiteSpace(selection.ObjectIds))
        {
            if (!TryParseObjectIds(selection.ObjectIds, out var parsedObjectIds))
            {
                return LayerSelectionTranslation.Failure(
                    "Invalid objectIds parameter",
                    "objectIds must be a comma-separated list of integer feature identifiers.");
            }

            objectIds = parsedObjectIds;
        }

        // ----- geometry / geometryType / inSR / spatialRel -----
        SpatialFilter? spatialFilter = null;
        if (!string.IsNullOrWhiteSpace(selection.Geometry))
        {
            if (!GeoServicesGeometryParser.TryParseGeoServicesGeometry(
                    selection.Geometry, selection.GeometryType, out var parsedGeometry, out var geometryError) ||
                parsedGeometry == null)
            {
                return LayerSelectionTranslation.Failure(
                    "Invalid geometry parameter",
                    geometryError ?? "geometry could not be parsed.");
            }

            var spatialReferenceResolver = services.GetRequiredService<SpatialReferenceResolver>();
            int? inputSrid;
            try
            {
                inputSrid = await spatialReferenceResolver.ResolveSridAsync(
                    selection.InSr, parsedGeometry.SpatialReference, cancellationToken).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                return LayerSelectionTranslation.Failure("Invalid inSR parameter", ex.Message);
            }

            var spatialRel = selection.SpatialRel;
            if (IsDistanceBasedSpatialRelationship(spatialRel))
            {
                // The analytics endpoints already overload `distance` for
                // spatial-join (DWithin radius) and buffer-aggregate (buffer
                // radius). Distance-based esri spatial relationships would need
                // to reuse the same key for the spatial-filter radius, which
                // would silently mask whichever meaning the caller intended.
                // Reject explicitly so the caller gets a clear error instead
                // of a confusing parameter collision.
                return LayerSelectionTranslation.Failure(
                    "Unsupported spatialRel",
                    "Distance-based spatial relationships (withinDistance / beyondDistance) are not supported on the spatial analytics endpoints; use the operation-specific 'distance' parameter or apply the predicate via the 'where' clause instead.");
            }

            try
            {
                var queryParamsForFilter = new QueryParameters { SpatialRel = spatialRel };
                spatialFilter = GeoServicesSpatialFilterBuilder.BuildSpatialFilter(
                    queryParamsForFilter,
                    parsedGeometry,
                    inputSrid,
                    services.GetService<IOptions<LimitsOptions>>()?.Value.Geometry.MaxVerticesPerGeometry);
            }
            catch (ArgumentException ex)
            {
                return LayerSelectionTranslation.Failure("Invalid spatialRel", ex.Message);
            }
        }

        return LayerSelectionTranslation.Success(new FeatureQuery
        {
            Where = where,
            SqlFilter = sqlFilter,
            ObjectIds = objectIds,
            SpatialFilter = spatialFilter,
            SpatialReferenceSrid = spatialReferenceSrid
        });
    }

    public static ImmutableArray<StatisticDefinition>? ApplyStatisticFieldTypes(
        ImmutableArray<StatisticDefinition>? outStatistics,
        MetadataV2Object resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (!outStatistics.HasValue || outStatistics.Value.IsDefaultOrEmpty)
        {
            return outStatistics;
        }

        var builder = ImmutableArray.CreateBuilder<StatisticDefinition>(outStatistics.Value.Length);
        foreach (var statistic in outStatistics.Value)
        {
            builder.Add(statistic with
            {
                FieldType = TryResolveFieldType(resource, statistic.OnStatisticField, out var fieldType)
                    ? fieldType
                    : statistic.FieldType
            });
        }

        return builder.ToImmutable();
    }

    private static bool TryResolveFieldType(MetadataV2Object resource, string fieldName, out MetadataV2FieldType fieldType)
    {
        var objectIdField = resource.FindPrimaryIdField();
        if (string.Equals(fieldName, objectIdField?.Name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fieldName, FieldNames.ObjectId, StringComparison.OrdinalIgnoreCase))
        {
            fieldType = objectIdField?.Type ?? MetadataV2FieldType.Integer;
            return true;
        }

        var match = resource.SchemaFields.FirstOrDefault(field =>
            string.Equals(field.Name, fieldName, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            fieldType = match.Type;
            return true;
        }

        fieldType = default;
        return false;
    }

    private static bool TryParseObjectIds(string raw, out ImmutableArray<long> objectIds)
    {
        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            objectIds = ImmutableArray<long>.Empty;
            return true;
        }

        var builder = ImmutableArray.CreateBuilder<long>(parts.Length);
        foreach (var part in parts)
        {
            if (!long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                objectIds = ImmutableArray<long>.Empty;
                return false;
            }

            builder.Add(parsed);
        }

        objectIds = builder.ToImmutable();
        return true;
    }

    private static bool IsDistanceBasedSpatialRelationship(string? spatialRel)
    {
        if (string.IsNullOrWhiteSpace(spatialRel))
        {
            return false;
        }

        var normalized = spatialRel.Trim().ToLowerInvariant();
        return normalized is "withindistance" or "beyonddistance";
    }

    private static string? GetValueString(IReadOnlyDictionary<string, StringValues> values, string key)
        => values.TryGetValue(key, out var raw) ? raw.ToString() : null;
}
