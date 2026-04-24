// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer;

internal static partial class FeatureServerEndpoints
{
    private static async Task<IResult> HandleQueryFeaturesGet(
        string serviceId,
        int layerId,
        HttpContext context,
        [FromServices] FeatureServerQueryHandler queryHandler,
        [FromServices] ICommonQueryValidator queryValidator)
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
        [FromServices] FeatureServerQueryHandler queryHandler,
        [FromServices] ICommonQueryValidator queryValidator)
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
            if (TryGetUnsupportedMediaType(readError, out var receivedContentType))
            {
                return CreateUnsupportedRequestContentTypeResult(context, receivedContentType);
            }

            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [readError ?? "Invalid request body."]);
        }

        if (!TryValidateAllowedParameters(values, queryValidator, AllowedQueryParameters.Query, out error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        var mergedValues = ToCaseInsensitiveDictionary(context.Request.Query);
        foreach (var pair in values)
        {
            mergedValues[pair.Key] = pair.Value;
        }

        if (!TryParseQueryParameters(mergedValues, out var queryParams, out var parseError))
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

    private static async Task<IResult> HandleServiceQueryFeaturesGet(
        string serviceId,
        HttpContext context,
        [FromServices] FeatureServerQueryHandler queryHandler,
        [FromServices] ICommonQueryValidator queryValidator)
    {
        if (!TryValidateAllowedParameters(
                context.Request.Query,
                queryValidator,
                FeatureServerServiceQueryAllowedParameters,
                out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        var values = ToCaseInsensitiveDictionary(context.Request.Query);
        var requestedFormat = GetValueString(values, "f");
        if (!TryValidateOutputFormat(requestedFormat, JsonOnlyFormats, out _, out var formatError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [formatError ?? "Output format is not supported."]);
        }

        var queryValues = new Dictionary<string, StringValues>(values, StringComparer.OrdinalIgnoreCase);
        queryValues.Remove("layerId");
        queryValues.Remove("layers");
        queryValues.Remove("layerDefs");

        if (!TryParseQueryParameters(queryValues, out var queryParams, out var parseError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [parseError ?? "Invalid query parameter."]);
        }

        using var scope = HonuaTelemetryScope.StartFeature(
            "query",
            HonuaTelemetry.Protocols.FeatureServer,
            "service",
            context.TraceIdentifier);
        scope.WithTag(HonuaTelemetry.Tags.ServiceId, serviceId);

        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var serviceValidationResult = await FeatureServerResourceValidationHelpers.ValidateServiceAsync(
            resourceValidator,
            serviceId,
            context,
            logger: null,
            cancellationToken);
        if (!serviceValidationResult.IsValid)
        {
            return serviceValidationResult.ErrorResult!;
        }

        var service = serviceValidationResult.Service!;
        if (!TryResolveRequestedServiceLayers(service, values, out var selectedLayers, out _, out var selectionError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [selectionError ?? "Invalid layer selection."]);
        }

        var accessError = AccessPolicyHelpers.RequireAnyLayerAccess(context, selectedLayers, service);
        if (accessError != null)
        {
            return accessError;
        }

        if (!LayerDefsParser.TryParse(GetValueString(values, "layerDefs"), queryValidator, out var layerDefs, out var layerDefsError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [layerDefsError ?? "Invalid layerDefs parameter."]);
        }

        var accessibleLayers = FilterAccessibleLayers(context, service, selectedLayers);
        var layerResults = new List<ServiceQueryLayerResponse>(accessibleLayers.Length);

        foreach (var layer in accessibleLayers)
        {
            var layerQueryParams = queryParams;
            if (layerDefs.TryGetValue(layer.Id, out var layerDefinitionExpression))
            {
                var layerQueryValues = new Dictionary<string, StringValues>(queryValues, StringComparer.OrdinalIgnoreCase);
                if (string.IsNullOrWhiteSpace(layerDefinitionExpression))
                {
                    layerQueryValues.Remove("where");
                }
                else
                {
                    layerQueryValues["where"] = layerDefinitionExpression;
                }

                if (!TryParseQueryParameters(layerQueryValues, out layerQueryParams, out parseError))
                {
                    return StandardErrorHelpers.CreateBadRequest(context,
                        "Invalid query parameters",
                        [parseError ?? "Invalid layerDefs parameter."]);
                }
            }

            var (queryResponse, errorResult) = await queryHandler.HandleServiceQueryLayerAsync(
                serviceId,
                layer.Id,
                layerQueryParams,
                context,
                queryValidator,
                requiredProtocol: null,
                cancellationToken);
            if (errorResult != null)
            {
                return errorResult;
            }

            layerResults.Add(MapServiceQueryLayerResponse(layer.Id, queryResponse!));
        }

        var response = new ServiceQueryResponse
        {
            Layers = [.. layerResults]
        };

        scope.SetSuccess(layerResults.Count);
        return Results.Json(
            response,
            FeatureServerJsonContext.Default.ServiceQueryResponse,
            contentType: "application/json");
    }

    internal static bool TryParseQueryParameters(
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

        if (resultOffset.HasValue && resultOffset.Value < 0)
        {
            error = "resultOffset cannot be negative";
            return false;
        }

        if (!TryParseIntValue(values, "resultRecordCount", out var resultRecordCount, out error))
        {
            return false;
        }

        if (resultRecordCount.HasValue && resultRecordCount.Value < 0)
        {
            error = "resultRecordCount cannot be negative";
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

        var outFields = GetValueString(values, "outFields");
        if (HasEmptyCommaSeparatedToken(outFields))
        {
            error = "outFields parameter contains an empty field name";
            return false;
        }

        parameters = new QueryParameters
        {
            Where = GetValueString(values, "where"),
            OutFields = NormalizeOutFields(outFields),
            OrderByFields = GetValueString(values, "orderByFields"),
            Geometry = GetValueString(values, "geometry"),
            InSr = GetValueString(values, "inSR"),
            InSrSpecified = values.ContainsKey("inSR"),
            OutSr = GetValueString(values, "outSR"),
            OutSrSpecified = values.ContainsKey("outSR"),
            GeometryType = GetValueString(values, "geometryType"),
            SpatialRel = GetValueString(values, "spatialRel"),
            Units = GetValueString(values, "units"),
            F = GetValueString(values, "f") ?? "json",
            FormatSpecified = values.ContainsKey("f"),
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

    private static bool HasEmptyCommaSeparatedToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var token in value.Split(',', StringSplitOptions.None))
        {
            if (token.Trim().Length == 0)
            {
                return true;
            }
        }

        return false;
    }
}
