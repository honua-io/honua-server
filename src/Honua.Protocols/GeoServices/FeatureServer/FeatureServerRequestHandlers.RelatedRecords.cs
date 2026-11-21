// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Configuration;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Infrastructure.Models;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Honua.Protocols.GeoServices.FeatureServer;

internal static partial class FeatureServerEndpoints
{
    private const int MaxRelatedRecordsObjectIdCountUpperBound = 1000;

    private static Task<IResult> HandleQueryRelatedRecordsGet(
        string serviceId,
        int layerId,
        HttpContext context,
        FeatureServerRelatedRecordsHandler relatedRecordsHandler)
        => HandleQueryRelatedRecordsGetCore(serviceId, layerId, context, relatedRecordsHandler, null);

    private static async Task<IResult> HandleQueryRelatedRecordsGetCore(
        string serviceId,
        int layerId,
        HttpContext context,
        FeatureServerRelatedRecordsHandler relatedRecordsHandler,
        string? requiredProtocol)
    {
        var queryValidator = context.RequestServices.GetRequiredService<ICommonQueryValidator>();
        if (!TryValidateAllowedParameters(context.Request.Query, queryValidator, AllowedQueryParameters.QueryRelatedRecords, out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        var maxObjectIdCount = ResolveMaxRelatedRecordsObjectIdCount(context);
        if (!TryParseRelatedRecordsParameters(
                ToCaseInsensitiveDictionary(context.Request.Query),
                maxObjectIdCount,
                out var queryParams,
                out var parseError))
        {
            return StandardErrorHelpers.CreateBadRequest(context, parseError ?? "Invalid query parameters");
        }

        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        return await relatedRecordsHandler.HandleQueryRelatedRecordsAsync(
            serviceId,
            layerId,
            queryParams,
            requiredProtocol,
            cancellationToken);
    }

    private static Task<IResult> HandleQueryRelatedRecordsPost(
        string serviceId,
        int layerId,
        HttpContext context,
        FeatureServerRelatedRecordsHandler relatedRecordsHandler)
        => HandleQueryRelatedRecordsPostCore(serviceId, layerId, context, relatedRecordsHandler, null);

    private static async Task<IResult> HandleQueryRelatedRecordsPostCore(
        string serviceId,
        int layerId,
        HttpContext context,
        FeatureServerRelatedRecordsHandler relatedRecordsHandler,
        string? requiredProtocol)
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
            if (TryGetUnsupportedMediaType(readError, out var receivedContentType))
            {
                return CreateUnsupportedRequestContentTypeResult(context, receivedContentType);
            }

            return StandardErrorHelpers.CreateBadRequest(context, readError ?? "Invalid request body.");
        }

        if (!TryValidateAllowedParameters(values, queryValidator, AllowedQueryParameters.QueryRelatedRecords, out error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        var maxObjectIdCount = ResolveMaxRelatedRecordsObjectIdCount(context);
        if (!TryParseRelatedRecordsParameters(values, maxObjectIdCount, out var queryParams, out var parseError))
        {
            return StandardErrorHelpers.CreateBadRequest(context, parseError ?? "Invalid query parameters");
        }

        return await relatedRecordsHandler.HandleQueryRelatedRecordsAsync(
            serviceId,
            layerId,
            queryParams,
            requiredProtocol,
            cancellationToken);
    }

    private static bool TryParseRelatedRecordsParameters(
        IReadOnlyDictionary<string, StringValues> values,
        int maxObjectIdCount,
        out QueryRelatedRecordsParameters parameters,
        out string? errorMessage)
    {
        parameters = null!;
        errorMessage = null;

        var (objectIds, globalIds, selectorError) = ParseRelatedRecordSelectors(values, maxObjectIdCount);
        if (selectorError is not null)
        {
            errorMessage = selectorError;
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

        if (resultOffset.HasValue && resultOffset.Value < 0)
        {
            errorMessage = "resultOffset cannot be negative";
            return false;
        }

        if (!TryParseIntValue(values, "resultRecordCount", out var resultRecordCount, out errorMessage))
        {
            return false;
        }

        if (resultRecordCount.HasValue && resultRecordCount.Value < 0)
        {
            errorMessage = "resultRecordCount cannot be negative";
            return false;
        }

        if (!TryParseBoolValue(values, "returnCountOnly", false, out var returnCountOnly, out errorMessage))
        {
            return false;
        }

        var orderByFields = GetValueString(values, "orderByFields");
        if (HasEmptyCommaSeparatedToken(orderByFields))
        {
            errorMessage = "orderByFields parameter contains an empty field name";
            return false;
        }

        if (!TryParseBoolValue(values, "returnZ", false, out var returnZ, out errorMessage))
        {
            return false;
        }

        if (!TryParseBoolValue(values, "returnM", false, out var returnM, out errorMessage))
        {
            return false;
        }

        if (!TryParseBoolValue(values, "returnTrueCurves", false, out var returnTrueCurves, out errorMessage))
        {
            return false;
        }

        if (!TryParseIntValue(values, "geometryPrecision", out var geometryPrecision, out errorMessage))
        {
            return false;
        }

        if (geometryPrecision.HasValue && geometryPrecision.Value < 0)
        {
            errorMessage = "geometryPrecision cannot be negative";
            return false;
        }

        if (!TryParseDoubleValue(values, "maxAllowableOffset", out var maxAllowableOffset, out errorMessage))
        {
            return false;
        }

        if (maxAllowableOffset.HasValue && maxAllowableOffset.Value < 0)
        {
            errorMessage = "maxAllowableOffset cannot be negative";
            return false;
        }

        long? historicMoment = null;
        var historicMomentValue = GetValueString(values, "historicMoment");
        if (!string.IsNullOrWhiteSpace(historicMomentValue))
        {
            if (!long.TryParse(historicMomentValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedHistoricMoment))
            {
                errorMessage = "historicMoment must be an integer";
                return false;
            }

            historicMoment = parsedHistoricMoment;
        }

        var where = GetValueString(values, "where");
        var definitionExpression = GetValueString(values, "definitionExpression");
        if (!string.IsNullOrWhiteSpace(definitionExpression))
        {
            where = string.IsNullOrWhiteSpace(where)
                ? definitionExpression
                : $"({where}) AND ({definitionExpression})";
        }

        var outFields = GetValueString(values, "outFields");
        if (HasEmptyCommaSeparatedToken(outFields))
        {
            errorMessage = "outFields parameter contains an empty field name";
            return false;
        }

        parameters = new QueryRelatedRecordsParameters
        {
            ObjectIds = objectIds,
            GlobalIds = globalIds,
            RelationshipId = relationshipId,
            OutFields = NormalizeOutFields(outFields),
            Where = where,
            ReturnGeometry = returnGeometry,
            F = GetValueString(values, "f") ?? "json",
            OutSr = GetValueString(values, "outSR"),
            ReturnZ = returnZ,
            ReturnM = returnM,
            ReturnTrueCurves = returnTrueCurves,
            GeometryPrecision = geometryPrecision,
            MaxAllowableOffset = maxAllowableOffset,
            GdbVersion = GetValueString(values, "gdbVersion"),
            SqlFormat = GetValueString(values, "sqlFormat"),
            HistoricMoment = historicMoment,
            ResultOffset = resultOffset,
            ResultRecordCount = resultRecordCount,
            OrderByFields = orderByFields,
            ReturnCountOnly = returnCountOnly
        };

        return true;
    }

    private static int ResolveMaxRelatedRecordsObjectIdCount(HttpContext context)
    {
        var limits = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value;
        return Math.Clamp(limits.Query.MaxRecordCount, 1, MaxRelatedRecordsObjectIdCountUpperBound);
    }

    private static (long[] ObjectIds, string[]? GlobalIds, string? Error) ParseRelatedRecordSelectors(
        IReadOnlyDictionary<string, StringValues> values,
        int maxCount)
    {
        var hasObjectIds = TryGetValue(values, "objectIds", out var rawObjectIds) && !StringValues.IsNullOrEmpty(rawObjectIds);
        var hasGlobalIds = TryGetValue(values, "globalIds", out var rawGlobalIds) && !StringValues.IsNullOrEmpty(rawGlobalIds);
        if (!hasObjectIds && !hasGlobalIds)
        {
            return ([], null, "objectIds or globalIds parameter is required");
        }

        long[] objectIds = [];
        if (hasObjectIds && !TryParseRequiredLongArray(values, "objectIds", maxCount, out objectIds, out var objectIdError))
        {
            return ([], null, objectIdError);
        }

        if (!hasGlobalIds)
        {
            return (objectIds, null, null);
        }

        var (globalIds, globalIdError) = ParseGlobalIdArray(rawGlobalIds.ToString(), maxCount);
        if (globalIdError is not null)
        {
            return ([], null, globalIdError);
        }

        if (objectIds.Length + globalIds.Length > maxCount)
        {
            return ([], null, $"objectIds and globalIds combined cannot exceed {maxCount} items");
        }

        return (objectIds, globalIds, null);
    }

    private static (string[] GlobalIds, string? Error) ParseGlobalIdArray(string raw, int maxCount)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ([], "globalIds parameter cannot be empty");
        }

        var parts = raw.Split(',', StringSplitOptions.None);
        if (parts.Length > maxCount)
        {
            return ([], $"globalIds parameter cannot exceed {maxCount} items");
        }

        var parsed = new string[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var normalized = FeatureServerGlobalIds.ParseGlobalId(parts[i].Trim());
            if (normalized is null)
            {
                return ([], "globalIds must contain valid UUID values");
            }

            parsed[i] = normalized;
        }

        return (parsed, null);
    }
}
