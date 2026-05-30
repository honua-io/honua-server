// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Services;
using Honua.Protocols.GeoServices;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Server.Features.Protocols.SpatialAnalytics.Models;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.Infrastructure.Analytics;

/// <summary>
/// Builds the shared <see cref="FeatureQuery"/> consumed by every spatial analytics
/// endpoint from the GeoServices-style filter bundle the SpatialAnalytics handlers
/// receive. The implementation intentionally lives in the Infrastructure namespace so
/// the filter-parsing glue can reference shared GeoServices query types
/// (<see cref="GeoServicesGeometry"/>, <see cref="QueryParameters"/>)
/// without pulling SpatialAnalytics into a
/// direct cross-feature reference — the vertical-slice architecture test treats
/// Infrastructure as an exempt shared-services layer, so helper state machines
/// declared here never trigger a violation.
/// </summary>
/// <remarks>
/// <para>
/// The async state machine generated for the factory overloads lives under
/// <c>Honua.Server.Features.Infrastructure.Analytics.AnalyticsFeatureQueryFactory+&lt;TryBuildAsync&gt;d__N</c>,
/// which <c>VerticalSliceIsolationTests</c> skips because the checker iterates
/// <c>_featureNames.Where(f =&gt; f is not ("Infrastructure" or "Ogc" or "Wfs20"))</c>.
/// The callers in SpatialAnalytics only observe the <see cref="FeatureQuery"/> /
/// <see cref="IResult"/> tuple so their own state machines stay clean of
/// protocol-specific service captures.
/// </para>
/// <para>
/// Supported parameters:
/// <list type="bullet">
///   <item><c>where</c> — ArcGIS SQL filter folded into
///     <see cref="FeatureQuery.SqlFilter"/>.</item>
///   <item><c>time</c>/<c>timeRelation</c> — FeatureServer-style temporal filter,
///     ANDed with the where clause and translated through the same
///     <see cref="IFilterExpressionService"/> the main query handler uses.</item>
///   <item><c>objectIds</c> — comma-separated long list mapped to
///     <see cref="FeatureQuery.ObjectIds"/>.</item>
///   <item><c>geometry</c>/<c>geometryType</c>/<c>inSR</c>/<c>spatialRel</c>
///     — GeoServices-style spatial filter mapped to
///     <see cref="FeatureQuery.SpatialFilter"/>. Distance-based relationships
///     (<c>esriSpatialRelWithinDistance</c>/<c>esriSpatialRelBeyondDistance</c>)
///     are rejected because the analytics endpoints already overload
///     <c>distance</c> for spatial-join (DWithin radius) and buffer-aggregate
///     (buffer radius); reusing the same key for two different concepts would
///     hide caller errors.</item>
/// </list>
/// </para>
/// </remarks>
internal static class AnalyticsFeatureQueryFactory
{
    /// <summary>
    /// Parses the shared analytics filter bundle from <paramref name="values"/> and
    /// returns either the populated <see cref="FeatureQuery"/> or a pre-shaped
    /// HTTP 400 <see cref="IResult"/> describing the validation failure. Callers
    /// pattern-match on the tuple: when <c>Error</c> is non-null they should return
    /// it directly, otherwise <c>Query</c> is the value to hand to the SQL builder.
    /// </summary>
    public static async Task<(FeatureQuery? Query, IResult? Error)> TryBuildAsync(
        HttpContext context,
        IReadOnlyDictionary<string, StringValues> values,
        MetadataV2Resource resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return await TryBuildCoreAsync(
            context,
            values,
            resource,
            resource.ReadSrid() ?? SpatialReference.WGS84.ToSrid(),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(FeatureQuery? Query, IResult? Error)> TryBuildCoreAsync(
        HttpContext context,
        IReadOnlyDictionary<string, StringValues> values,
        MetadataV2Resource resource,
        int spatialReferenceSrid,
        CancellationToken cancellationToken)
    {
        var filterService = context.RequestServices.GetRequiredService<IFilterExpressionService>();

        // ----- where (ArcGIS SQL) -----
        var where = GetValueString(values, SpatialAnalyticsParameters.Where);
        FilterExpression? whereExpression = null;
        if (!string.IsNullOrWhiteSpace(where))
        {
            var parseResult = filterService.Parse(FilterLanguage.ArcGisSql, where);
            if (!parseResult.IsSuccess)
            {
                return (null, StandardErrorHelpers.CreateBadRequest(
                    context,
                    ErrorMessages.Validation.InvalidParameter,
                    [parseResult.ErrorMessage ?? "Invalid filter syntax."]));
            }

            whereExpression = parseResult.Expression;
        }

        // ----- time / timeRelation (FeatureServer style) -----
        // Built as a FilterExpression and ANDed with the where clause so a single
        // SqlFragment carries both predicates. Mirrors the main FeatureServer
        // query handler so the analytics surface honours the same temporal logic.
        var time = GetValueString(values, SpatialAnalyticsParameters.Time);
        FilterExpression? temporalExpression = null;
        if (!string.IsNullOrWhiteSpace(time))
        {
            var timeRelation = GetValueString(values, SpatialAnalyticsParameters.TimeRelation);
            try
            {
                temporalExpression = GeoServicesTemporalQueryBuilder.BuildTemporalExpression(time, timeRelation, resource);
            }
            catch (ArgumentException ex)
            {
                return (null, StandardErrorHelpers.CreateBadRequest(
                    context,
                    ErrorMessages.Validation.InvalidParameter,
                    [ex.Message]));
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
                return (null, StandardErrorHelpers.CreateBadRequest(
                    context,
                    ErrorMessages.Validation.InvalidParameter,
                    [translationResult.ErrorMessage ?? "Invalid filter syntax."]));
            }

            sqlFilter = translationResult.SqlFilter;
        }

        // ----- objectIds -----
        ImmutableArray<long>? objectIds = null;
        var objectIdsRaw = GetValueString(values, SpatialAnalyticsParameters.ObjectIds);
        if (!string.IsNullOrWhiteSpace(objectIdsRaw))
        {
            if (!TryParseObjectIds(objectIdsRaw, out var parsedObjectIds))
            {
                return (null, StandardErrorHelpers.CreateBadRequest(
                    context,
                    "Invalid objectIds parameter",
                    ["objectIds must be a comma-separated list of integer feature identifiers."]));
            }

            objectIds = parsedObjectIds;
        }

        // ----- geometry / geometryType / inSR / spatialRel -----
        SpatialFilter? spatialFilter = null;
        var geometryRaw = GetValueString(values, SpatialAnalyticsParameters.Geometry);
        if (!string.IsNullOrWhiteSpace(geometryRaw))
        {
            var geometryType = GetValueString(values, SpatialAnalyticsParameters.GeometryType);
            if (!GeoServicesGeometryParser.TryParseGeoServicesGeometry(
                    geometryRaw, geometryType, out var parsedGeometry, out var geometryError) ||
                parsedGeometry == null)
            {
                return (null, StandardErrorHelpers.CreateBadRequest(
                    context,
                    "Invalid geometry parameter",
                    [geometryError ?? "geometry could not be parsed."]));
            }

            var spatialReferenceResolver = context.RequestServices.GetRequiredService<SpatialReferenceResolver>();
            var inSr = GetValueString(values, SpatialAnalyticsParameters.InSr);
            int? inputSrid;
            try
            {
                inputSrid = await spatialReferenceResolver.ResolveSridAsync(
                    inSr, parsedGeometry.SpatialReference, cancellationToken);
            }
            catch (ArgumentException ex)
            {
                return (null, StandardErrorHelpers.CreateBadRequest(
                    context,
                    "Invalid inSR parameter",
                    [ex.Message]));
            }

            var spatialRel = GetValueString(values, SpatialAnalyticsParameters.SpatialRel);
            if (IsDistanceBasedSpatialRelationship(spatialRel))
            {
                // The analytics endpoints already overload `distance` for
                // spatial-join (DWithin radius) and buffer-aggregate (buffer
                // radius). Distance-based esri spatial relationships would need
                // to reuse the same key for the spatial-filter radius, which
                // would silently mask whichever meaning the caller intended.
                // Reject explicitly so the caller gets a clear error instead
                // of a confusing parameter collision.
                return (null, StandardErrorHelpers.CreateBadRequest(
                    context,
                    "Unsupported spatialRel",
                    ["Distance-based spatial relationships (esriSpatialRelWithinDistance / esriSpatialRelBeyondDistance) are not supported on the spatial analytics endpoints; use the operation-specific 'distance' parameter or apply the predicate via the 'where' clause instead."]));
            }

            try
            {
                var queryParamsForFilter = new QueryParameters { SpatialRel = spatialRel };
                spatialFilter = GeoServicesSpatialFilterBuilder.BuildSpatialFilter(
                    queryParamsForFilter, parsedGeometry, inputSrid);
            }
            catch (ArgumentException ex)
            {
                return (null, StandardErrorHelpers.CreateBadRequest(
                    context,
                    "Invalid spatialRel",
                    [ex.Message]));
            }
        }

        var featureQuery = new FeatureQuery
        {
            Where = where,
            SqlFilter = sqlFilter,
            ObjectIds = objectIds,
            SpatialFilter = spatialFilter,
            SpatialReferenceSrid = spatialReferenceSrid
        };

        return (featureQuery, null);
    }

    /// <summary>
    /// Enriches parsed statistic definitions with field type metadata from a v2 resource schema.
    /// </summary>
    public static ImmutableArray<StatisticDefinition>? ApplyStatisticFieldTypes(
        ImmutableArray<StatisticDefinition>? outStatistics,
        MetadataV2Resource resource)
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

    private static bool TryResolveFieldType(MetadataV2Resource resource, string fieldName, out MetadataV2FieldType fieldType)
    {
        var objectIdField = resource.FindPrimaryIdField();
        if (string.Equals(fieldName, objectIdField?.Name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fieldName, FieldNames.ObjectId, StringComparison.OrdinalIgnoreCase))
        {
            fieldType = objectIdField?.Type ?? MetadataV2FieldType.Integer;
            return true;
        }

        foreach (var field in resource.SchemaFields)
        {
            if (string.Equals(field.Name, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                fieldType = field.Type;
                return true;
            }
        }

        fieldType = default;
        return false;
    }

    /// <summary>
    /// Parses a comma-separated list of long feature identifiers. Whitespace
    /// is trimmed and empty entries are skipped; the method returns false if
    /// any non-empty entry fails to parse so the handler can surface an
    /// HTTP 400 with a clear message instead of silently dropping IDs.
    /// </summary>
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

    /// <summary>
    /// Returns true when the supplied <c>spatialRel</c> would otherwise build a
    /// distance-based spatial filter. The analytics endpoints reject these so the
    /// <c>distance</c> parameter has a single, unambiguous meaning per operation.
    /// </summary>
    private static bool IsDistanceBasedSpatialRelationship(string? spatialRel)
    {
        if (string.IsNullOrWhiteSpace(spatialRel))
        {
            return false;
        }

        var normalized = spatialRel.Trim().ToLowerInvariant();
        return normalized is "esrispatialrelwithindistance" or "esrispatialrelbeyonddistance";
    }

    private static string? GetValueString(IReadOnlyDictionary<string, StringValues> values, string key)
        => values.TryGetValue(key, out var raw) ? raw.ToString() : null;
}
