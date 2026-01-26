// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.FeatureServer;

internal static partial class FeatureServerEndpoints
{
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

    private static Dictionary<string, StringValues> ToCaseInsensitiveDictionary(IQueryCollection values)
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
