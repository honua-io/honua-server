// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.SpatialAnalytics.Abstractions;
using Honua.Core.Features.SpatialAnalytics.Domain;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.FeatureServer;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.FeatureServer.Services;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.SpatialAnalytics.Models;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.SpatialAnalytics;

/// <summary>
/// Shared request handlers for the Pro-tier spatial analytics endpoints.
/// The REST (<c>/rest/services/...</c>) and OGC (<c>/ogc/features/collections/...</c>)
/// route families both delegate to the core handlers in this file so clients see
/// identical behavior regardless of which family they call.
/// </summary>
internal static partial class SpatialAnalyticsRequestHandlers
{
    /// <summary>
    /// Parameter names shared between REST and OGC analytics endpoints.
    /// </summary>
    private static readonly FrozenSet<string> EmptyQueryParameters =
        Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Verifies that the active edition is at least Pro, emitting an HTTP 403
    /// with a clear error message otherwise. Pro-tier gating for the analytics
    /// slice mirrors the PrintingTools layout template flow. Exposed as
    /// <c>internal</c> so the gate decision can be unit-tested directly.
    /// </summary>
    internal static IResult? RequireProEdition(HttpContext context, string operation, ILogger? logger)
    {
        var licenseProvider = context.RequestServices.GetRequiredService<ILicenseStatusProvider>();
        var edition = licenseProvider.GetCurrentStatus().Edition;
        if (edition >= HonuaEdition.Pro)
        {
            return null;
        }

        if (logger != null)
        {
            SpatialAnalyticsLog.EditionGateBlocked(logger, operation, edition.ToString());
        }

        return StandardErrorHelpers.CreateForbidden(
            context,
            $"Spatial analytics ({operation}) requires the Pro edition or higher. Current edition: {edition}.");
    }

    /// <summary>
    /// Resolves the <see cref="ISpatialAnalyticsReader"/> from DI. Returns an HTTP 501
    /// Not Implemented response when no backend is registered (for example when the
    /// host is configured to run against the DuckDB read-only provider, which does not
    /// ship a spatial-analytics backend). The analytics endpoints are mapped
    /// unconditionally in <c>FeatureRegistrationExtensions</c> so all deployments expose
    /// the same route surface; this gate converts the otherwise-unhelpful
    /// <c>InvalidOperationException</c> (→ HTTP 500) into a contract-compliant
    /// unsupported-feature response mirroring the H3 capability gate.
    /// </summary>
    internal static bool TryGetAnalyticsReader(
        HttpContext context,
        string operation,
        ILogger? logger,
        out ISpatialAnalyticsReader reader,
        out IResult? errorResult)
    {
        var resolved = context.RequestServices.GetService<ISpatialAnalyticsReader>();
        if (resolved == null)
        {
            if (logger != null)
            {
                SpatialAnalyticsLog.ReaderUnavailable(logger, operation);
            }

            reader = null!;
            errorResult = StandardErrorHelpers.CreateNotImplemented(
                context,
                "Spatial analytics backend not available",
                [$"Spatial analytics ({operation}) is not supported by the active feature-store provider."]);
            return false;
        }

        reader = resolved;
        errorResult = null;
        return true;
    }

    /// <summary>
    /// Parses the shared filter bundle accepted by every analytics endpoint and
    /// turns it into a <see cref="FeatureQuery"/> the SQL builders can consume:
    /// <list type="bullet">
    ///   <item><c>where</c> — ArcGIS SQL filter (folded into <see cref="FeatureQuery.SqlFilter"/>).</item>
    ///   <item><c>time</c>/<c>timeRelation</c> — FeatureServer-style temporal filter,
    ///     ANDed with the where clause and translated through the same
    ///     <see cref="IFilterExpressionService"/> the main query handler uses.</item>
    ///   <item><c>objectIds</c> — comma-separated long list mapped to
    ///     <see cref="FeatureQuery.ObjectIds"/> (the SQL builder picks this up via
    ///     <c>AppendWhereClause</c>).</item>
    ///   <item><c>geometry</c>/<c>geometryType</c>/<c>inSR</c>/<c>spatialRel</c>
    ///     — GeoServices-style spatial filter mapped to
    ///     <see cref="FeatureQuery.SpatialFilter"/>. Distance-based relationships
    ///     (<c>esriSpatialRelWithinDistance</c>/<c>esriSpatialRelBeyondDistance</c>)
    ///     are rejected because the analytics endpoints already overload
    ///     <c>distance</c> for spatial-join (DWithin radius) and buffer-aggregate
    ///     (buffer radius); reusing the same key for two different concepts would
    ///     hide caller errors.</item>
    /// </list>
    /// <para>
    /// The helper is async because <see cref="IFeatureServerQueryServices.ResolveSridAsync"/>
    /// can hit the catalog when the supplied <c>inSR</c> is a WKT string. Callers
    /// pattern-match on <c>(featureQuery, errorResult)</c>: when <c>errorResult</c>
    /// is non-null they should return it directly, otherwise <c>featureQuery</c> is
    /// the populated value.
    /// </para>
    /// </summary>
    private static async Task<(FeatureQuery? Query, IResult? Error)> TryBuildFeatureQueryAsync(
        HttpContext context,
        IReadOnlyDictionary<string, StringValues> values,
        LayerDefinition layer,
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
                temporalExpression = FeatureServerTemporalQueryBuilder.BuildTemporalExpression(
                    time, timeRelation, layer);
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
            var translationResult = filterService.Translate(combinedExpression, layer);
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
            if (!FeatureServerGeometryParser.TryParseGeoServicesGeometry(
                    geometryRaw, geometryType, out var parsedGeometry, out var geometryError) ||
                parsedGeometry == null)
            {
                return (null, StandardErrorHelpers.CreateBadRequest(
                    context,
                    "Invalid geometry parameter",
                    [geometryError ?? "geometry could not be parsed."]));
            }

            var queryServices = context.RequestServices.GetRequiredService<IFeatureServerQueryServices>();
            var inSr = GetValueString(values, SpatialAnalyticsParameters.InSr);
            int? inputSrid;
            try
            {
                inputSrid = await queryServices.ResolveSridAsync(
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
                spatialFilter = FeatureServerSpatialFilterBuilder.BuildSpatialFilter(
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
            SpatialReferenceSrid = layer.SpatialReference.ToSrid()
        };

        return (featureQuery, null);
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
    /// `distance` parameter has a single, unambiguous meaning per operation.
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

    /// <summary>
    /// Parses the optional <c>outStatistics</c> parameter. POST bodies with a
    /// native JSON array are flattened by <see cref="FeatureServerEndpoints.TryReadRequestValuesAsync"/>
    /// into comma-separated entries so the logic matches queryH3 / queryBins.
    /// </summary>
    private static bool TryParseOutStatistics(
        HttpContext context,
        IReadOnlyDictionary<string, StringValues> values,
        out ImmutableArray<StatisticDefinition>? outStatistics,
        out IResult? errorResult)
    {
        outStatistics = null;
        errorResult = null;

        var outStatsJson = GetValueString(values, SpatialAnalyticsParameters.OutStatistics);
        if (string.IsNullOrWhiteSpace(outStatsJson))
        {
            return true;
        }

        if (!outStatsJson.TrimStart().StartsWith('['))
        {
            outStatsJson = $"[{outStatsJson}]";
        }

        if (!TryParseOutStatisticsJson(outStatsJson, out var parsed, out var statsError))
        {
            errorResult = StandardErrorHelpers.CreateBadRequest(
                context,
                "Invalid outStatistics parameter",
                [statsError ?? "outStatistics must be valid JSON."]);
            return false;
        }

        outStatistics = parsed;
        return true;
    }

    private static bool TryParseOutStatisticsJson(
        string json, out ImmutableArray<StatisticDefinition> outStats, out string? error)
    {
        outStats = ImmutableArray<StatisticDefinition>.Empty;
        error = null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Array)
            {
                error = "outStatistics must be a JSON array.";
                return false;
            }

            var builder = ImmutableArray.CreateBuilder<StatisticDefinition>();
            foreach (var element in root.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    error = "Each outStatistics entry must be a JSON object.";
                    return false;
                }

                var statisticType = element.TryGetProperty("statisticType", out var typeElement)
                    ? typeElement.GetString()
                    : null;
                var onField = element.TryGetProperty("onStatisticField", out var fieldElement)
                    ? fieldElement.GetString()
                    : null;
                var outFieldName = element.TryGetProperty("outStatisticFieldName", out var outElement)
                    ? outElement.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(statisticType) ||
                    string.IsNullOrWhiteSpace(onField) ||
                    string.IsNullOrWhiteSpace(outFieldName))
                {
                    error = "Each outStatistics entry requires statisticType, onStatisticField, and outStatisticFieldName.";
                    return false;
                }

                if (!TryParseStatisticType(statisticType, out var parsedType))
                {
                    error = $"Unsupported statisticType: {statisticType}.";
                    return false;
                }

                builder.Add(new StatisticDefinition
                {
                    StatisticType = parsedType,
                    OnStatisticField = onField,
                    OutStatisticFieldName = outFieldName
                });
            }

            outStats = builder.ToImmutable();
            return true;
        }
        catch (JsonException)
        {
            error = "outStatistics is not valid JSON.";
            return false;
        }
    }

    private static bool TryParseStatisticType(string type, out StatisticType result)
    {
        result = type.ToLowerInvariant() switch
        {
            "count" => StatisticType.Count,
            "sum" => StatisticType.Sum,
            "min" => StatisticType.Min,
            "max" => StatisticType.Max,
            "avg" => StatisticType.Avg,
            "stddev" => StatisticType.Stddev,
            "var" => StatisticType.Var,
            _ => default
        };

        return type.ToLowerInvariant() is "count" or "sum" or "min" or "max" or "avg" or "stddev" or "var";
    }

    private static string? GetValueString(IReadOnlyDictionary<string, StringValues> values, string key)
        => values.TryGetValue(key, out var raw) ? raw.ToString() : null;

    /// <summary>
    /// Reads the POST body (JSON or form-encoded) into a case-insensitive dictionary
    /// using the same <c>TryReadRequestValuesAsync</c> as FeatureServer so the error
    /// shapes and media-type handling stay consistent across the analytics surface.
    /// </summary>
    private static async Task<(IReadOnlyDictionary<string, StringValues>? Values, IResult? Error)> ReadRequestValuesAsync(
        HttpContext context, CancellationToken cancellationToken)
    {
        if (context.Request.Method == HttpMethods.Post)
        {
            var (values, readError) = await FeatureServerEndpoints.TryReadRequestValuesAsync(
                context.Request, cancellationToken);
            if (values == null)
            {
                if (FeatureServerEndpoints.TryGetUnsupportedMediaType(readError, out var receivedContentType))
                {
                    return (null, FeatureServerEndpoints.CreateUnsupportedRequestContentTypeResult(context, receivedContentType));
                }

                return (null, StandardErrorHelpers.CreateBadRequest(
                    context,
                    "Invalid request body",
                    [readError ?? "Invalid request body."]));
            }

            return (values, null);
        }

        var query = FeatureServerEndpoints.ToCaseInsensitiveDictionary(context.Request.Query);
        return (query, null);
    }

    /// <summary>
    /// Shapes an analytics row into a GeoJSON-style feature by extracting the
    /// <c>geometry</c>, <c>clusterGeometry</c>, <c>bufferGeometry</c> or
    /// <c>cellGeometry</c> column (whichever is present) as a
    /// <see cref="SpatialAnalyticsGeometry"/> and leaving the remaining columns as
    /// feature properties.
    /// </summary>
    private static SpatialAnalyticsFeature MapRowToFeature(IReadOnlyDictionary<string, object?> row)
    {
        var properties = new Dictionary<string, object?>(row);
        SpatialAnalyticsGeometry? geometry = null;

        // Cluster-hull mode → clusterGeometry, buffer mode → bufferGeometry,
        // density mode → cellGeometry, per-feature cluster / spatial-join → geometry.
        if (TryExtractGeoJsonGeometry(properties, "geometry", out var extracted) ||
            TryExtractGeoJsonGeometry(properties, "clusterGeometry", out extracted) ||
            TryExtractGeoJsonGeometry(properties, "bufferGeometry", out extracted) ||
            TryExtractGeoJsonGeometry(properties, "cellGeometry", out extracted))
        {
            geometry = extracted;
        }

        return new SpatialAnalyticsFeature
        {
            Geometry = geometry,
            Properties = properties
        };
    }

    private static bool TryExtractGeoJsonGeometry(
        Dictionary<string, object?> properties,
        string key,
        out SpatialAnalyticsGeometry? geometry)
    {
        geometry = null;
        if (!properties.TryGetValue(key, out var raw) || raw is not string geoJsonStr)
        {
            return false;
        }

        properties.Remove(key);

        try
        {
            using var doc = JsonDocument.Parse(geoJsonStr);
            var root = doc.RootElement;
            var geoType = root.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
                ? typeEl.GetString() ?? "Point"
                : "Point";

            string? coordsJson = root.TryGetProperty("coordinates", out var coords)
                ? coords.GetRawText()
                : null;

            geometry = new SpatialAnalyticsGeometry
            {
                Type = geoType,
                CoordinatesJson = coordsJson
            };
            return true;
        }
        catch (JsonException)
        {
            // Restore so the raw GeoJSON string is surfaced as a property if parsing failed
            properties[key] = geoJsonStr;
            geometry = null;
            return false;
        }
    }

    /// <summary>
    /// Writes the assembled feature collection using the analytics JSON context.
    /// Content type is <c>application/geo+json</c> so the response matches the
    /// OGC Features mirror even when called via the REST route.
    /// </summary>
    private static IResult WriteAnalyticsResponse(SpatialAnalyticsFeatureCollection featureCollection)
    {
        return Results.Json(
            featureCollection,
            SpatialAnalyticsJsonContext.Default.SpatialAnalyticsFeatureCollection,
            contentType: "application/geo+json");
    }

    /// <summary>
    /// Validates the REST-style (serviceId + layerId) resource bundle and checks
    /// that the caller can read the layer. Calls <see cref="IResourceValidator"/>
    /// directly so the slice does not take a hard dependency on the
    /// FeatureServer-internal validation helper (vertical slice isolation).
    /// </summary>
    private static async Task<(LayerDefinition? Layer, ServiceDefinition? Service, IResult? Error)> ValidateRestResourceAsync(
        HttpContext context, string serviceId, int layerId, CancellationToken cancellationToken)
    {
        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var resourceResult = await resourceValidator.ValidateServiceLayerAsync(serviceId, layerId, cancellationToken);

        if (!resourceResult.IsValid)
        {
            var errorMessage = resourceResult.ErrorMessage ?? "Resource not found.";
            var errorResult = resourceResult.ErrorCode == ResourceValidationError.InvalidIdentifier
                ? StandardErrorHelpers.CreateBadRequest(context, errorMessage)
                : StandardErrorHelpers.CreateNotFound(context, errorMessage);
            return (null, null, errorResult);
        }

        var service = resourceResult.Resource!.Service;
        var layer = resourceResult.Resource.Layer;

        var protocolError = ProtocolValidationHelpers.ValidateProtocolEnabled(
            context, service, ServiceProtocols.FeatureServer);
        if (protocolError != null)
        {
            return (null, null, protocolError);
        }

        var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer, service);
        if (accessError != null)
        {
            return (null, null, accessError);
        }

        return (layer, service, null);
    }

    /// <summary>
    /// Validates the OGC-style (collectionId) resource bundle and checks that
    /// the caller can read the layer. Mirrors <see cref="LayerValidationHelpers.ValidateCollectionWithAccessAsync"/>.
    /// </summary>
    private static async Task<(LayerDefinition? Layer, IResult? Error)> ValidateOgcResourceAsync(
        HttpContext context, string collectionId, CancellationToken cancellationToken)
    {
        var layerValidation = await LayerValidationHelpers.ValidateCollectionWithAccessAsync(
            context,
            collectionId,
            cancellationToken: cancellationToken,
            requiredProtocol: ServiceProtocols.OgcFeatures);
        if (!layerValidation.IsValid)
        {
            return (null, layerValidation.ErrorResult!);
        }

        return (layerValidation.Layer!, null);
    }

    private static Activity? StartAnalyticsActivity(string operation, string protocol, string? serviceId, int layerId)
    {
        var activity = HonuaTelemetry.ActivitySource.StartActivity($"honua.analytics.{operation}");
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, protocol);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, operation);
        if (serviceId != null)
        {
            activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);
        }
        activity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId);
        return activity;
    }

    /// <summary>
    /// Detects overflow against the <c>MaxInputFeatures + 1</c> and
    /// <c>MaxOutputRows + 1</c> guard caps emitted by the SQL builders.
    /// <paramref name="inputRowCount"/> reflects the number of rows that survived
    /// the input cap (<c>src</c> CTE) — for per-feature paths the handler passes
    /// <c>rows.Length</c> (since each output row corresponds to one input row),
    /// for aggregated paths the handler passes the value extracted from the
    /// <c>_inputCount</c> column emitted by the SQL builder.
    /// <paramref name="outputRowCount"/> is always the row count returned to the
    /// client. This split is necessary because aggregated paths can shrink the
    /// output row count to a single value while still having truncated the input.
    /// </summary>
    private static (bool InputTruncated, bool ResultTruncated) DetectOverflow(
        int inputRowCount, int outputRowCount, int maxInputFeatures, int? maxOutputRows)
    {
        var inputTruncated = inputRowCount > maxInputFeatures;
        var resultTruncated = maxOutputRows.HasValue && outputRowCount > maxOutputRows.Value;
        return (inputTruncated, resultTruncated);
    }

    /// <summary>
    /// Reads the <c>_inputCount</c> column emitted by aggregated SQL builders
    /// (cluster hull, buffer dissolve, density) and strips it from every row so
    /// the property bag handed to <see cref="MapRowToFeature"/> stays clean.
    /// Returns 0 when no rows are present, or when the column is missing
    /// (per-feature paths that did not request it).
    /// </summary>
    /// <remarks>
    /// The SQL builders cap the <c>src</c> CTE at <c>MaxInputFeatures + 1</c>, so
    /// the value read here is at most <c>MaxInputFeatures + 1</c> and the
    /// <see cref="DetectOverflow"/> guard interprets it consistently with the
    /// per-feature paths. When the result is empty (e.g. cluster hull mode where
    /// every input feature was DBSCAN noise) the handler reports
    /// <c>InputTruncated = false</c>; that corner case is acceptable because
    /// callers can re-issue the query with stricter filters and the
    /// <c>numberReturned = 0</c> signal already tells them no clusters formed.
    /// </remarks>
    private static int ExtractInputCount(ref ImmutableArray<IReadOnlyDictionary<string, object?>> rows)
    {
        if (rows.IsDefaultOrEmpty)
        {
            return 0;
        }

        if (!rows[0].TryGetValue("_inputCount", out var raw) || raw == null)
        {
            return 0;
        }

        var inputCount = Convert.ToInt32(raw, CultureInfo.InvariantCulture);

        // Strip "_inputCount" from every row so MapRowToFeature does not surface
        // it as a feature property. Reuse the case-insensitive equality the
        // dictionary already provides instead of allocating a fresh comparer.
        var trimmed = ImmutableArray.CreateBuilder<IReadOnlyDictionary<string, object?>>(rows.Length);
        foreach (var row in rows)
        {
            var copy = new Dictionary<string, object?>(row);
            copy.Remove("_inputCount");
            trimmed.Add(copy);
        }

        rows = trimmed.MoveToImmutable();
        return inputCount;
    }

    private static ImmutableArray<IReadOnlyDictionary<string, object?>> TrimOverflowRows(
        ImmutableArray<IReadOnlyDictionary<string, object?>> rows,
        int maxInputFeatures,
        int? maxOutputRows)
    {
        var limit = maxOutputRows ?? maxInputFeatures;
        if (rows.Length <= limit)
        {
            return rows;
        }

        return rows.Take(limit).ToImmutableArray();
    }

    /// <summary>
    /// Common error result for analytics operations that fail inside the reader.
    /// Keeps the status code / shape consistent with the rest of FeatureServer.
    /// </summary>
    private static IResult CreateReaderFailureResult(HttpContext context, string operation, Exception exception)
    {
        var message = exception is ArgumentException ? exception.Message : "Spatial analytics request failed.";
        return StandardErrorHelpers.CreateBadRequest(context, $"Spatial analytics {operation} failed", [message]);
    }
}
