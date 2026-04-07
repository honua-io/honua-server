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
    /// Parses the shared filter bundle accepted by every analytics endpoint:
    /// <c>where</c> (ArcGIS SQL filter) compiled via <see cref="IFilterExpressionService"/>.
    /// </summary>
    private static bool TryBuildFeatureQuery(
        HttpContext context,
        IReadOnlyDictionary<string, StringValues> values,
        LayerDefinition layer,
        out FeatureQuery featureQuery,
        out IResult? errorResult)
    {
        featureQuery = default;
        errorResult = null;

        var where = GetValueString(values, SpatialAnalyticsParameters.Where);
        SqlFragment? sqlFilter = null;

        if (!string.IsNullOrWhiteSpace(where))
        {
            var filterService = context.RequestServices.GetRequiredService<IFilterExpressionService>();
            var parseResult = filterService.Parse(FilterLanguage.ArcGisSql, where);
            if (!parseResult.IsSuccess)
            {
                errorResult = StandardErrorHelpers.CreateBadRequest(
                    context,
                    ErrorMessages.Validation.InvalidParameter,
                    [parseResult.ErrorMessage ?? "Invalid filter syntax."]);
                return false;
            }

            if (parseResult.Expression != null)
            {
                var translationResult = filterService.Translate(parseResult.Expression, layer);
                if (!translationResult.IsSuccess)
                {
                    errorResult = StandardErrorHelpers.CreateBadRequest(
                        context,
                        ErrorMessages.Validation.InvalidParameter,
                        [translationResult.ErrorMessage ?? "Invalid filter syntax."]);
                    return false;
                }

                sqlFilter = translationResult.SqlFilter;
            }
        }

        featureQuery = new FeatureQuery
        {
            Where = where,
            SqlFilter = sqlFilter,
            SpatialReferenceSrid = layer.SpatialReference.ToSrid()
        };

        return true;
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
    /// Detects overflow by comparing the row count to the <c>maxInputFeatures + 1</c>
    /// guard cap emitted by the SQL builders and logs a telemetry marker.
    /// </summary>
    private static (bool InputTruncated, bool ResultTruncated) DetectOverflow(
        int rowCount, int maxInputFeatures, int? maxOutputRows)
    {
        var resultTruncated = maxOutputRows.HasValue && rowCount > maxOutputRows.Value;
        // rowCount > maxInputFeatures is only reached when no per-result cap applies
        // (cluster per-feature mode, spatial join, buffer per-row), in which case the
        // SQL builder applied LIMIT maxInputFeatures+1.
        var inputTruncated = !maxOutputRows.HasValue && rowCount > maxInputFeatures;
        return (inputTruncated, resultTruncated);
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
