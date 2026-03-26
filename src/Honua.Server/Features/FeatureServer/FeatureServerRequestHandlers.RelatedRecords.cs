// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Configuration;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.FeatureServer;

internal static partial class FeatureServerEndpoints
{
    private const int MaxRelatedRecordsObjectIdCountUpperBound = 1000;

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
            if (TryGetUnsupportedMediaType(readError, out var receivedContentType))
            {
                return CreateUnsupportedRequestContentTypeResult(receivedContentType);
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

        if (!TryParseRequiredLongArray(values, "objectIds", maxObjectIdCount, out var objectIds, out errorMessage))
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
            ResultRecordCount = resultRecordCount
        };

        return true;
    }

    private static int ResolveMaxRelatedRecordsObjectIdCount(HttpContext context)
    {
        var limits = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value;
        return Math.Clamp(limits.Query.MaxRecordCount, 1, MaxRelatedRecordsObjectIdCountUpperBound);
    }
}
