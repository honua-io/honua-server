// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Infrastructure.Helpers;

internal static class FeatureAttributeValueNormalizer
{
    // A value advertised as a string must be a JSON string, because ArcGIS Pro drops the whole cursor when that field arrives as an object or array.
    public static object? Normalize(object? value)
        => value switch
        {
            bool b => b ? 1 : 0,
            JsonElement element => NormalizeJsonElement(element),
            JsonDocument document => NormalizeJsonElement(document.RootElement),
            _ => value
        };

    private static object? NormalizeJsonElement(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => element.GetRawText()
        };

    public static Dictionary<string, object?> NormalizeAttributes(IReadOnlyDictionary<string, object?> attributes)
    {
        var normalized = new Dictionary<string, object?>(attributes.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in attributes)
        {
            normalized[kvp.Key] = Normalize(kvp.Value);
        }

        return normalized;
    }
}
