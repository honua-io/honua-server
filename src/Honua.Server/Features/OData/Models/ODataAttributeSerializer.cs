// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Server.Features.OData.Models;

/// <summary>
/// Shared helpers for serializing and normalizing OData attribute payloads.
/// </summary>
internal static class ODataAttributeSerializer
{
    public static string Serialize(IReadOnlyDictionary<string, object?> attributes)
    {
        var normalized = NormalizeAttributes(attributes);
        return JsonSerializer.Serialize(normalized, ODataJsonContext.Default.DictionaryStringObject);
    }

    public static Dictionary<string, object?> Deserialize(string? attributesJson)
    {
        if (string.IsNullOrWhiteSpace(attributesJson))
        {
            return new Dictionary<string, object?>();
        }

        var parsed = JsonSerializer.Deserialize(attributesJson, ODataJsonContext.Default.DictionaryStringObject);
        return parsed ?? new Dictionary<string, object?>();
    }

    public static Dictionary<string, object?> NormalizeAttributes(IReadOnlyDictionary<string, object?> attributes)
        => attributes.ToDictionary(kvp => kvp.Key, kvp => NormalizeODataValue(kvp.Value));

    private static object? NormalizeODataValue(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is JsonElement element)
        {
            return ConvertJsonElement(element);
        }

        if (value is IReadOnlyDictionary<string, object?> readOnlyDict)
        {
            return readOnlyDict.ToDictionary(kvp => kvp.Key, kvp => NormalizeODataValue(kvp.Value));
        }

        if (value is IDictionary<string, object?> dict)
        {
            return dict.ToDictionary(kvp => kvp.Key, kvp => NormalizeODataValue(kvp.Value));
        }

        if (value is System.Collections.IEnumerable enumerable && value is not string)
        {
            var list = new List<object?>();
            foreach (var item in enumerable)
            {
                list.Add(NormalizeODataValue(item));
            }

            return list.ToArray();
        }

        return value;
    }

    private static object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var longVal) ? longVal :
                element.TryGetDouble(out var doubleVal) ? doubleVal :
                element.GetDecimal(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(prop => prop.Name, prop => ConvertJsonElement(prop.Value)),
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToArray(),
            _ => element.GetRawText()
        };
    }
}
