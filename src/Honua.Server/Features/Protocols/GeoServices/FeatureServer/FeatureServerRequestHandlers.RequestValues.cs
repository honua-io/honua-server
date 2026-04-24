// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Server.Features.Infrastructure.Validation;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer;

internal static partial class FeatureServerEndpoints
{
    private const string UnsupportedMediaTypeErrorPrefix = "__unsupported_media_type__:";
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

    internal static Dictionary<string, StringValues> ToCaseInsensitiveDictionary(IQueryCollection values)
    {
        if (values.Count == 0)
        {
            return new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase);
        }

        return values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static string? GetValueString(IReadOnlyDictionary<string, StringValues> values, string key)
    {
        return TryGetValue(values, key, out var raw) ? raw.ToString() : null;
    }

    private static bool TryGetValue(IReadOnlyDictionary<string, StringValues> values, string key, out StringValues value)
        => values.TryGetValue(key, out value);

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
