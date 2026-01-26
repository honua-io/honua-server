// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.FeatureServer;

internal static partial class FeatureServerEndpoints
{
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
}
