// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Tiles;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.FeatureServer;

internal static partial class FeatureServerEndpoints
{
    private static async Task<IResult> HandleQueryFeaturesGet(
        string serviceId,
        int layerId,
        HttpContext context,
        FeatureServerQueryHandler queryHandler,
        ICommonQueryValidator queryValidator)
    {
        if (!TryValidateAllowedParameters(context.Request.Query, queryValidator, AllowedQueryParameters.Query, out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        if (!TryParseQueryParameters(ToCaseInsensitiveDictionary(context.Request.Query), out var queryParams, out var parseError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [parseError ?? "Invalid query parameter."]);
        }

        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        return await queryHandler.HandleQueryFeaturesAsync(
            serviceId,
            layerId,
            queryParams,
            context,
            queryValidator,
            cancellationToken);
    }

    private static async Task<IResult> HandleQueryFeaturesPost(
        string serviceId,
        int layerId,
        HttpContext context,
        FeatureServerQueryHandler queryHandler,
        ICommonQueryValidator queryValidator)
    {
        if (!TryValidateAllowedParameters(context.Request.Query, queryValidator, AllowedQueryParameters.Query, out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var (values, readError) = await TryReadRequestValuesAsync(context.Request, cancellationToken);
        if (values == null)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [readError ?? "Invalid request body."]);
        }

        if (!TryParseQueryParameters(values, out var queryParams, out var parseError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [parseError ?? "Invalid query parameter."]);
        }

        return await queryHandler.HandleQueryFeaturesAsync(
            serviceId,
            layerId,
            queryParams,
            context,
            queryValidator,
            cancellationToken);
    }

    private static async Task<IResult> HandleGenerateRenderer(
        HttpContext context)
    {
        var queryValidator = context.RequestServices.GetRequiredService<ICommonQueryValidator>();
        if (!TryValidateAllowedParameters(context.Request.Query, queryValidator, AllowedQueryParameters.GenerateRenderer, out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        if (!RouteValidationHelpers.TryValidateServiceId(context, out var serviceId))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Service ID is required");
        }

        if (!RouteValidationHelpers.TryValidateLayerId(context, out var layerId))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Layer ID is required");
        }

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var resourceResult = await resourceValidator.ValidateServiceLayerAsync(serviceId, layerId, cancellationToken);
        if (!resourceResult.IsValid)
        {
            var errorMessage = resourceResult.ErrorMessage ?? "Resource not found.";
            if (resourceResult.ErrorCode == ResourceValidationError.InvalidIdentifier)
            {
                return StandardErrorHelpers.CreateBadRequest(context, errorMessage);
            }

            return StandardErrorHelpers.CreateNotFound(context, errorMessage);
        }

        var service = resourceResult.Resource!.Service;
        var layer = resourceResult.Resource.Layer;
        var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer, service);
        if (accessError != null)
        {
            return accessError;
        }

        var values = ToCaseInsensitiveDictionary(context.Request.Query);
        var classificationDef = GetValueString(values, "classificationDef");
        if (!string.IsNullOrWhiteSpace(classificationDef)
            && !TryParseJsonPayload(classificationDef, out var jsonError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid classificationDef",
                [jsonError ?? "classificationDef must be valid JSON."]);
        }

        if (!layer.HasGeometry)
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Layer does not support renderers");
        }

        var symbol = BuildSimpleSymbol(layer.GeometryType);
        if (symbol == null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Layer geometry type is not supported");
        }

        var renderer = new Dictionary<string, object?>
        {
            ["type"] = "simple",
            ["symbol"] = symbol
        };

        return Results.Json(renderer, FeatureServerJsonContext.Default.DictionaryStringObject, contentType: "application/json");
    }

    private static Dictionary<string, object?>? BuildSimpleSymbol(GeometryType geometryType)
    {
        var strokeColor = new[] { 45, 105, 165, 255 };
        var fillColor = new[] { 45, 105, 165, 64 };
        var outline = new Dictionary<string, object?>
        {
            ["type"] = "esriSLS",
            ["style"] = "esriSLSSolid",
            ["color"] = strokeColor,
            ["width"] = 1
        };

        return geometryType switch
        {
            GeometryType.Point or GeometryType.MultiPoint => new Dictionary<string, object?>
            {
                ["type"] = "esriSMS",
                ["style"] = "esriSMSCircle",
                ["color"] = strokeColor,
                ["size"] = 6,
                ["outline"] = outline
            },
            GeometryType.LineString or GeometryType.MultiLineString => new Dictionary<string, object?>
            {
                ["type"] = "esriSLS",
                ["style"] = "esriSLSSolid",
                ["color"] = strokeColor,
                ["width"] = 2
            },
            GeometryType.Polygon or GeometryType.MultiPolygon => new Dictionary<string, object?>
            {
                ["type"] = "esriSFS",
                ["style"] = "esriSFSSolid",
                ["color"] = fillColor,
                ["outline"] = outline
            },
            _ => null
        };
    }

    private static bool TryParseJsonPayload(string payload, out string? error)
    {
        error = null;

        try
        {
            using var _ = JsonDocument.Parse(payload);
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static async Task<IResult> HandleApplyEdits(
        string serviceId,
        int layerId,
        HttpContext context,
        FeatureServerEditsHandler editsHandler,
        IOptions<LimitsOptions> limitsOptions)
    {
        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var (request, readError) = await TryReadApplyEditsRequestAsync(context.Request, cancellationToken);
        if (request == null)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid applyEdits request",
                [readError ?? "Invalid request body."]);
        }

        if (request.UseGlobalIds)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "useGlobalIds is not supported",
                ["Set useGlobalIds to false and supply objectIds in attributes."]);
        }

        return await editsHandler.HandleApplyEditsAsync(
            serviceId,
            layerId,
            request,
            limitsOptions.Value.Edits,
            cancellationToken);
    }

    private static async Task<IResult> HandleQueryRelatedRecordsGet(
        string serviceId,
        int layerId,
        HttpContext context,
        FeatureServerRelatedRecordsHandler relatedRecordsHandler)
    {
        var queryValidator = context.RequestServices.GetRequiredService<ICommonQueryValidator>();
        if (!TryValidateAllowedParameters(context.Request.Query, queryValidator, AllowedQueryParameters.QueryRelatedRecords, out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        if (!TryParseRelatedRecordsParameters(ToCaseInsensitiveDictionary(context.Request.Query), out var queryParams, out var parseError))
        {
            return StandardErrorHelpers.CreateBadRequest(context, parseError ?? "Invalid query parameters");
        }

        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        return await relatedRecordsHandler.HandleQueryRelatedRecordsAsync(
            serviceId,
            layerId,
            queryParams,
            cancellationToken);
    }

    private static async Task<IResult> HandleQueryRelatedRecordsPost(
        string serviceId,
        int layerId,
        HttpContext context,
        FeatureServerRelatedRecordsHandler relatedRecordsHandler)
    {
        var queryValidator = context.RequestServices.GetRequiredService<ICommonQueryValidator>();
        if (!TryValidateAllowedParameters(context.Request.Query, queryValidator, AllowedQueryParameters.QueryRelatedRecords, out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var (values, readError) = await TryReadRequestValuesAsync(context.Request, cancellationToken);
        if (values == null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, readError ?? "Invalid request body.");
        }

        if (!TryParseRelatedRecordsParameters(values, out var queryParams, out var parseError))
        {
            return StandardErrorHelpers.CreateBadRequest(context, parseError ?? "Invalid query parameters");
        }

        return await relatedRecordsHandler.HandleQueryRelatedRecordsAsync(
            serviceId,
            layerId,
            queryParams,
            cancellationToken);
    }

    private static async Task<IResult> HandleLayerTile(
        int layerId,
        int z,
        int x,
        int y,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var queryValidator = context.RequestServices.GetRequiredService<ICommonQueryValidator>();
        if (!TryValidateAllowedParameters(context.Request.Query, queryValidator, AllowedQueryParameters.Tiles, out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        var tileOptions = context.RequestServices.GetRequiredService<IOptions<TileOptions>>().Value;
        var tileLimits = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value.Tiles;
        if (z < tileLimits.MinTileZoom || z > tileLimits.MaxTileZoom)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                $"Zoom level {z} is outside supported range",
                [$"Supported zoom range is {tileLimits.MinTileZoom}-{tileLimits.MaxTileZoom}"]);
        }

        var maxIndex = 1 << z;
        if (x < 0 || y < 0 || x >= maxIndex || y >= maxIndex)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                $"Invalid tile coordinates: x={x}, y={y}, z={z}");
        }

        var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessAsync(
            context,
            layerId,
            cancellationToken: cancellationToken);
        if (!layerValidation.IsValid)
        {
            return layerValidation.ErrorResult!;
        }
        var layer = layerValidation.Layer!;

        var where = GetValueString(ToCaseInsensitiveDictionary(context.Request.Query), "where");
        SqlFragment? sqlFilter = null;
        if (!string.IsNullOrWhiteSpace(where))
        {
            var filterService = context.RequestServices.GetRequiredService<IFilterExpressionService>();
            var parseResult = filterService.Parse(FilterLanguage.ArcGisSql, where);
            if (!parseResult.IsSuccess)
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    ErrorMessages.Validation.InvalidParameter,
                    [parseResult.ErrorMessage ?? "Invalid filter syntax."]);
            }

            if (parseResult.Expression != null)
            {
                var translationResult = filterService.Translate(parseResult.Expression, layer);
                if (!translationResult.IsSuccess)
                {
                    return StandardErrorHelpers.CreateBadRequest(context,
                        ErrorMessages.Validation.InvalidParameter,
                        [translationResult.ErrorMessage ?? "Invalid filter syntax."]);
                }

                sqlFilter = translationResult.SqlFilter;
            }
        }
        var query = new FeatureQuery
        {
            Where = where,
            SqlFilter = sqlFilter,
            SpatialReferenceSrid = layer.SpatialReference.ToSrid()
        };

        var tileProvider = context.RequestServices.GetRequiredService<ITileProvider>();
        var tileData = await tileProvider.GetMvtTileAsync(
            layerId,
            x,
            y,
            z,
            query,
            tileOptions,
            tileLimits,
            cancellationToken);

        if (tileData == null || tileData.Length == 0)
        {
            return Results.NoContent();
        }

        context.Response.Headers["Cache-Control"] = $"public, max-age={tileOptions.CacheMaxAge}";
        return Results.Bytes(tileData, "application/vnd.mapbox-vector-tile");
    }

    private static async Task<(IReadOnlyDictionary<string, StringValues>? Values, string? Error)> TryReadRequestValuesAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(cancellationToken);
            var values = form.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            return (values, null);
        }

        if (request.ContentLength is 0)
        {
            return (null, "Request body is required.");
        }

        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, "Invalid request body.");
            }

            var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var converted = ConvertJsonValue(property.Value);
                if (!StringValues.IsNullOrEmpty(converted))
                {
                    values[property.Name] = converted;
                }
            }

            return (values, null);
        }
        catch (JsonException)
        {
            return (null, "Invalid JSON payload.");
        }
    }

    private static async Task<(ApplyEditsRequest? Request, string? Error)> TryReadApplyEditsRequestAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(cancellationToken);
            var values = form.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            return TryParseApplyEditsRequest(values);
        }

        if (request.ContentLength is 0)
        {
            return (new ApplyEditsRequest(), null);
        }

        try
        {
            var parsed = await JsonSerializer.DeserializeAsync(
                request.Body,
                FeatureServerJsonContext.Default.ApplyEditsRequest,
                cancellationToken);
            if (parsed == null)
            {
                return (null, "Invalid JSON payload.");
            }

            return (parsed, null);
        }
        catch (JsonException)
        {
            return (null, "Invalid JSON payload.");
        }
    }

    private static (ApplyEditsRequest? Request, string? Error) TryParseApplyEditsRequest(
        IReadOnlyDictionary<string, StringValues> values)
    {
        var request = new ApplyEditsRequest();

        if (!TryParseGeoServicesFeatures(values, "adds", out var adds, out var error))
        {
            return (null, error);
        }

        if (!TryParseGeoServicesFeatures(values, "updates", out var updates, out error))
        {
            return (null, error);
        }

        if (!TryParseDeletes(values, out var deletes, out error))
        {
            return (null, error);
        }

        if (!TryParseBoolValue(values, "rollbackOnFailure", false, out var rollbackOnFailure, out error))
        {
            return (null, error);
        }

        if (!TryParseBoolValue(values, "useGlobalIds", false, out var useGlobalIds, out error))
        {
            return (null, error);
        }

        request.Adds = adds;
        request.Updates = updates;
        request.Deletes = deletes;
        request.RollbackOnFailure = rollbackOnFailure;
        request.UseGlobalIds = useGlobalIds;

        return (request, null);
    }

    private static bool TryParseGeoServicesFeatures(
        IReadOnlyDictionary<string, StringValues> values,
        string key,
        out GeoServicesFeature[]? features,
        out string? error)
    {
        features = null;
        error = null;

        if (!TryGetValue(values, key, out var raw) || StringValues.IsNullOrEmpty(raw))
        {
            return true;
        }

        var payload = raw.ToString();
        if (string.IsNullOrWhiteSpace(payload))
        {
            return true;
        }

        try
        {
            features = JsonSerializer.Deserialize(payload, FeatureServerJsonContext.Default.GeoServicesFeatureArray);
            return true;
        }
        catch (JsonException)
        {
            error = $"{key} must be valid JSON.";
            return false;
        }
    }

    private static bool TryParseDeletes(
        IReadOnlyDictionary<string, StringValues> values,
        out object[]? deletes,
        out string? error)
    {
        deletes = null;
        error = null;

        if (!TryGetValue(values, "deletes", out var raw) || StringValues.IsNullOrEmpty(raw))
        {
            return true;
        }

        var payload = raw.ToString();
        if (string.IsNullOrWhiteSpace(payload))
        {
            return true;
        }

        var trimmedPayload = payload.TrimStart();
        if (trimmedPayload.Length > 0 && trimmedPayload[0] == '[')
        {
            try
            {
                using var document = JsonDocument.Parse(payload);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    error = "deletes must be a JSON array.";
                    return false;
                }

                var items = new List<object>();
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    items.Add(ConvertDeleteValue(element));
                }

                deletes = items.ToArray();
                return true;
            }
            catch (JsonException)
            {
                error = "deletes must be valid JSON.";
                return false;
            }
        }

        var tokens = payload.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return true;
        }

        var parsed = new object[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
        {
            if (long.TryParse(tokens[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                parsed[i] = id;
            }
            else
            {
                parsed[i] = tokens[i];
            }
        }

        deletes = parsed;
        return true;
    }

    private static object ConvertDeleteValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt64(out var id) => id,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => element.GetRawText()
        };
    }

    private static StringValues ConvertJsonValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Array => new StringValues(element.EnumerateArray().Select(item => item.ToString()).ToArray()),
            JsonValueKind.String => new StringValues(element.GetString() ?? string.Empty),
            JsonValueKind.Number => new StringValues(element.ToString()),
            JsonValueKind.True => new StringValues("true"),
            JsonValueKind.False => new StringValues("false"),
            JsonValueKind.Object => new StringValues(element.GetRawText()),
            _ => StringValues.Empty
        };
    }

    private static bool TryParseQueryParameters(
        IReadOnlyDictionary<string, StringValues> values,
        out QueryParameters parameters,
        out string? error)
    {
        error = null;
        parameters = new QueryParameters();

        if (!TryParseBoolValue(values, "returnGeometry", true, out var returnGeometry, out error))
        {
            return false;
        }

        if (!TryParseBoolValue(values, "returnIdsOnly", false, out var returnIdsOnly, out error))
        {
            return false;
        }

        if (!TryParseBoolValue(values, "returnCountOnly", false, out var returnCountOnly, out error))
        {
            return false;
        }

        if (!TryParseBoolValue(values, "returnExtentOnly", false, out var returnExtentOnly, out error))
        {
            return false;
        }

        if (!TryParseBoolValue(values, "returnDistance", false, out var returnDistance, out error))
        {
            return false;
        }

        if (!TryParseBoolValue(values, "returnCentroid", false, out var returnCentroid, out error))
        {
            return false;
        }

        if (!TryParseBoolValue(values, "returnDistinctValues", false, out var returnDistinctValues, out error))
        {
            return false;
        }

        if (!TryParseBoolValue(values, "returnZ", false, out var returnZ, out error))
        {
            return false;
        }

        if (!TryParseBoolValue(values, "returnM", false, out var returnM, out error))
        {
            return false;
        }

        if (!TryParseBoolValue(values, "returnTrueCurves", false, out var returnTrueCurves, out error))
        {
            return false;
        }

        if (!TryParseBoolValue(values, "returnExceededLimitFeatures", false, out var returnExceededLimitFeatures, out error))
        {
            return false;
        }

        if (!TryParseIntValue(values, "resultOffset", out var resultOffset, out error))
        {
            return false;
        }

        if (!TryParseIntValue(values, "resultRecordCount", out var resultRecordCount, out error))
        {
            return false;
        }

        if (!TryParseIntValue(values, "geometryPrecision", out var geometryPrecision, out error))
        {
            return false;
        }

        if (!TryParseIntValue(values, "nearestCount", out var nearestCount, out error))
        {
            return false;
        }

        if (!TryParseDoubleValue(values, "maxAllowableOffset", out var maxAllowableOffset, out error))
        {
            return false;
        }

        if (!TryParseDoubleValue(values, "distance", out var distance, out error))
        {
            return false;
        }

        if (!TryParseLongArray(values, "objectIds", out var objectIds, out error))
        {
            return false;
        }

        parameters = new QueryParameters
        {
            Where = GetValueString(values, "where"),
            OutFields = NormalizeOutFields(GetValueString(values, "outFields")),
            OrderByFields = GetValueString(values, "orderByFields"),
            Geometry = GetValueString(values, "geometry"),
            InSr = GetValueString(values, "inSR"),
            OutSr = GetValueString(values, "outSR"),
            GeometryType = GetValueString(values, "geometryType"),
            SpatialRel = GetValueString(values, "spatialRel"),
            Units = GetValueString(values, "units"),
            F = GetValueString(values, "f") ?? "json",
            Time = GetValueString(values, "time"),
            TimeRelation = GetValueString(values, "timeRelation"),
            ReturnGeometry = returnGeometry,
            ReturnIdsOnly = returnIdsOnly,
            ReturnCountOnly = returnCountOnly,
            ReturnExtentOnly = returnExtentOnly,
            ReturnDistance = returnDistance,
            ReturnCentroid = returnCentroid,
            ReturnDistinctValues = returnDistinctValues,
            ReturnZ = returnZ,
            ReturnM = returnM,
            ReturnTrueCurves = returnTrueCurves,
            ReturnExceededLimitFeatures = returnExceededLimitFeatures,
            ResultOffset = resultOffset,
            ResultRecordCount = resultRecordCount,
            GeometryPrecision = geometryPrecision,
            NearestCount = nearestCount,
            Distance = distance,
            ObjectIds = objectIds,
            MaxAllowableOffset = maxAllowableOffset,
            ResultType = GetValueString(values, "resultType"),
            OutStatistics = GetValueString(values, "outStatistics"),
            GroupByFieldsForStatistics = GetValueString(values, "groupByFieldsForStatistics"),
            Having = GetValueString(values, "having"),
            SqlFormat = GetValueString(values, "sqlFormat"),
            GdbVersion = GetValueString(values, "gdbVersion"),
            QuantizationParameters = GetValueString(values, "quantizationParameters"),
            DatumTransformation = GetValueString(values, "datumTransformation")
        };

        return true;
    }

    private static Dictionary<string, StringValues> ToCaseInsensitiveDictionary(IQueryCollection values)
    {
        if (values.Count == 0)
        {
            return new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase);
        }

        return values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryParseRelatedRecordsParameters(
        IReadOnlyDictionary<string, StringValues> values,
        out QueryRelatedRecordsParameters parameters,
        out string? errorMessage)
    {
        parameters = null!;
        errorMessage = null;

        if (!TryParseRequiredLongArray(values, "objectIds", out var objectIds, out errorMessage))
        {
            return false;
        }

        if (!TryParseRequiredIntValue(values, "relationshipId", out var relationshipId, out errorMessage))
        {
            return false;
        }

        if (!TryParseBoolValue(values, "returnGeometry", true, out var returnGeometry, out errorMessage))
        {
            return false;
        }

        if (!TryParseIntValue(values, "resultOffset", out var resultOffset, out errorMessage))
        {
            return false;
        }

        if (!TryParseIntValue(values, "resultRecordCount", out var resultRecordCount, out errorMessage))
        {
            return false;
        }

        var where = GetValueString(values, "where");
        var definitionExpression = GetValueString(values, "definitionExpression");
        if (!string.IsNullOrWhiteSpace(definitionExpression))
        {
            where = string.IsNullOrWhiteSpace(where)
                ? definitionExpression
                : $"({where}) AND ({definitionExpression})";
        }

        parameters = new QueryRelatedRecordsParameters
        {
            ObjectIds = objectIds,
            RelationshipId = relationshipId,
            OutFields = NormalizeOutFields(GetValueString(values, "outFields")),
            Where = where,
            ReturnGeometry = returnGeometry,
            F = GetValueString(values, "f") ?? "json",
            ResultOffset = resultOffset,
            ResultRecordCount = resultRecordCount
        };

        return true;
    }

    private static string? NormalizeOutFields(string? outFields)
    {
        if (string.IsNullOrWhiteSpace(outFields))
        {
            return null;
        }

        var tokens = outFields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return null;
        }

        if (tokens.Any(token => token.Equals("*", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return string.Join(',', tokens);
    }

    private static bool TryParseRequiredIntValue(
        IReadOnlyDictionary<string, StringValues> values,
        string key,
        out int result,
        out string? error)
    {
        error = null;
        result = default;

        if (!TryGetValue(values, key, out var raw) || StringValues.IsNullOrEmpty(raw))
        {
            error = $"{key} parameter is required";
            return false;
        }

        var value = raw.ToString();
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
        {
            error = $"{key} must be an integer";
            return false;
        }

        return true;
    }

    private static bool TryParseIntValue(
        IReadOnlyDictionary<string, StringValues> values,
        string key,
        out int? result,
        out string? error)
    {
        error = null;
        result = null;

        if (!TryGetValue(values, key, out var raw) || StringValues.IsNullOrEmpty(raw))
        {
            return true;
        }

        var value = raw.ToString();
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            error = $"{key} must be an integer";
            return false;
        }

        result = parsed;
        return true;
    }

    private static bool TryParseDoubleValue(
        IReadOnlyDictionary<string, StringValues> values,
        string key,
        out double? result,
        out string? error)
    {
        error = null;
        result = null;

        if (!TryGetValue(values, key, out var raw) || StringValues.IsNullOrEmpty(raw))
        {
            return true;
        }

        var value = raw.ToString();
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            error = $"{key} must be a number";
            return false;
        }

        result = parsed;
        return true;
    }

    private static bool TryParseBoolValue(
        IReadOnlyDictionary<string, StringValues> values,
        string key,
        bool defaultValue,
        out bool result,
        out string? error)
    {
        error = null;
        result = defaultValue;

        if (!TryGetValue(values, key, out var raw) || StringValues.IsNullOrEmpty(raw))
        {
            return true;
        }

        var value = raw.ToString();
        if (bool.TryParse(value, out var parsed))
        {
            result = parsed;
            return true;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            result = numeric != 0;
            return true;
        }

        error = $"{key} must be a boolean value";
        return false;
    }

    private static bool TryParseLongArray(
        IReadOnlyDictionary<string, StringValues> values,
        string key,
        out long[]? result,
        out string? error)
    {
        result = null;
        error = null;

        if (!TryGetValue(values, key, out var raw) || StringValues.IsNullOrEmpty(raw))
        {
            return true;
        }

        if (!TryParseLongArray(raw, key, out result, out error))
        {
            return false;
        }

        return true;
    }

    private static bool TryParseRequiredLongArray(
        IReadOnlyDictionary<string, StringValues> values,
        string key,
        out long[] result,
        out string? error)
    {
        error = null;
        result = [];

        if (!TryGetValue(values, key, out var raw) || StringValues.IsNullOrEmpty(raw))
        {
            error = $"{key} parameter is required";
            return false;
        }

        if (!TryParseLongArray(raw, key, out var parsed, out error))
        {
            error ??= $"Invalid {key} values";
            return false;
        }

        result = parsed ?? [];
        if (result.Length == 0)
        {
            error = $"{key} parameter is required";
            return false;
        }

        return true;
    }

    private static bool TryParseLongArray(
        StringValues raw,
        string key,
        out long[]? result,
        out string? error)
    {
        error = null;
        result = null;

        var tokens = new List<string>();
        foreach (var value in raw)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            tokens.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        if (tokens.Count == 0)
        {
            return true;
        }

        var ids = new List<long>(tokens.Count);
        foreach (var token in tokens)
        {
            if (!long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                error = string.Equals(key, "objectIds", StringComparison.OrdinalIgnoreCase)
                    ? "Invalid objectId value. objectIds parameter must contain only numeric values."
                    : $"{key} parameter must contain only numeric values";
                return false;
            }

            ids.Add(id);
        }

        result = ids.ToArray();
        return true;
    }

    private static string? GetValueString(IReadOnlyDictionary<string, StringValues> values, string key)
    {
        return TryGetValue(values, key, out var raw) ? raw.ToString() : null;
    }

    private static bool TryGetValue(IReadOnlyDictionary<string, StringValues> values, string key, out StringValues value)
    {
        if (values.TryGetValue(key, out value))
        {
            return true;
        }

        foreach (var pair in values)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
