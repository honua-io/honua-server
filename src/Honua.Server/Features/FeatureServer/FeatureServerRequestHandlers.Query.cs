// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.Infrastructure.Models;
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
}
