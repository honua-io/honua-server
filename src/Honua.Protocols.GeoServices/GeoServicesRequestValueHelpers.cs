// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Validation;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.Protocols.GeoServices;

internal static class GeoServicesRequestValueHelpers
{
    private const string UnsupportedMediaTypeErrorPrefix = "__unsupported_media_type__:";
    private const string QueryTimeoutTokenKey = "GeoServicesQueryTimeoutToken";

    internal static readonly FrozenSet<string> LayerQueryAllowedParameters = new[]
        {
            "where",
            "objectIds",
            "outFields",
            "orderByFields",
            "geometry",
            "inSR",
            "outSR",
            "geometryType",
            "spatialRel",
            "units",
            "f",
            "resultOffset",
            "resultRecordCount",
            "nearestCount",
            "distance",
            "returnGeometry",
            "returnIdsOnly",
            "returnCountOnly",
            "returnExtentOnly",
            "returnDistance",
            "returnCentroid",
            "returnDistinctValues",
            "returnZ",
            "returnM",
            "returnTrueCurves",
            "returnExceededLimitFeatures",
            "time",
            "timeRelation",
            "geometryPrecision",
            "maxAllowableOffset",
            "resultType",
            "outStatistics",
            "groupByFieldsForStatistics",
            "having",
            "sqlFormat",
            "gdbVersion",
            "quantizationParameters",
            "datumTransformation"
        }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    internal static readonly FrozenSet<string> ServiceQueryAllowedParameters = LayerQueryAllowedParameters
        .Append("layerId")
        .Append("layers")
        .Append("layerDefs")
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly ISet<string> SupportedRequestContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "application/json",
        "application/*+json",
        "application/x-www-form-urlencoded",
        "multipart/form-data"
    };

    internal static async Task<(IReadOnlyDictionary<string, StringValues>? Values, string? Error)> TryReadRequestValuesAsync(
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

        if (!TryValidateRequestContentType(request, out var receivedContentType))
        {
            return (null, UnsupportedMediaTypeErrorPrefix + (receivedContentType ?? "(missing)"));
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

    internal static bool TryValidateRequestContentType(HttpRequest request, out string? receivedContentType)
    {
        receivedContentType = null;

        if (request.HasFormContentType)
        {
            return true;
        }

        if (request.ContentLength is 0)
        {
            return true;
        }

        if (IsSupportedJsonContentType(request.ContentType))
        {
            return true;
        }

        receivedContentType = string.IsNullOrWhiteSpace(request.ContentType)
            ? "(missing)"
            : request.ContentType.Split(';', 2)[0].Trim();
        return false;
    }

    internal static bool TryGetUnsupportedMediaType(string? error, out string? receivedContentType)
    {
        receivedContentType = null;

        if (string.IsNullOrWhiteSpace(error) ||
            !error.StartsWith(UnsupportedMediaTypeErrorPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        receivedContentType = error[UnsupportedMediaTypeErrorPrefix.Length..];
        return true;
    }

    internal static IResult CreateUnsupportedRequestContentTypeResult(HttpContext context, string? receivedContentType)
    {
        return ValidationErrorHelpers.CreateUnsupportedMediaType(
            context,
            receivedContentType ?? "(missing)",
            SupportedRequestContentTypes);
    }

    internal static Dictionary<string, StringValues> ToCaseInsensitiveDictionary(IQueryCollection values)
    {
        if (values.Count == 0)
        {
            return new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase);
        }

        return values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    internal static CancellationToken GetTimeoutAwareCancellationToken(HttpContext context)
    {
        if (context.Items.TryGetValue(QueryTimeoutTokenKey, out var existing) && existing is CancellationToken cachedToken)
        {
            return cachedToken;
        }

        var limits = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value;
        var queryTimeout = limits.Query.QueryTimeout;

        var baseToken = context.RequestAborted;
        if (context.Items.TryGetValue("LimitsTimeoutToken", out var tokenObj) && tokenObj is CancellationToken timeoutToken)
        {
            baseToken = timeoutToken;
        }

        if (queryTimeout <= TimeSpan.Zero)
        {
            context.Items[QueryTimeoutTokenKey] = baseToken;
            return baseToken;
        }

        var timeoutCts = new CancellationTokenSource(queryTimeout);
        var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(baseToken, timeoutCts.Token);

        context.Response.RegisterForDispose(timeoutCts);
        context.Response.RegisterForDispose(combinedCts);

        context.Items[QueryTimeoutTokenKey] = combinedCts.Token;
        return combinedCts.Token;
    }

    internal static bool TryValidateAllowedParameters(
        IQueryCollection query,
        ICommonQueryValidator queryValidator,
        FrozenSet<string> allowedParameters,
        out string? error)
    {
        error = QueryParameterValidationHelpers.GetValidationError(
            queryValidator,
            query.Keys.ToArray(),
            allowedParameters);
        return error == null;
    }

    internal static bool TryValidateAllowedParameters(
        IReadOnlyDictionary<string, StringValues> values,
        ICommonQueryValidator queryValidator,
        FrozenSet<string> allowedParameters,
        out string? error)
    {
        error = QueryParameterValidationHelpers.GetValidationError(
            queryValidator,
            values.Keys.ToArray(),
            allowedParameters);
        return error == null;
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

    private static bool IsSupportedJsonContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var mediaType = contentType.Split(';', 2)[0].Trim();
        return string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
            mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }
}
