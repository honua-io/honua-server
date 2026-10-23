// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Infrastructure.Helpers;

/// <summary>
/// Normalizes feature attribute values for protocol wire formats that represent booleans as small integers.
/// </summary>
internal static class FeatureAttributeValueNormalizer
{
    /// <summary>
    /// Converts booleans to small-integer values and JSON objects or arrays to text.
    /// </summary>
    /// <remarks>
    /// A JSONB value advertised as <c>esriFieldTypeString</c> must be a JSON string.
    /// ArcGIS Pro drops the entire cursor result when that field arrives as a JSON
    /// object or array.
    /// </remarks>
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

    /// <summary>
    /// Creates a normalized copy of an attribute dictionary.
    /// </summary>
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
