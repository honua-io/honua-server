// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Features.Shared.Models;

/// <summary>
/// Helpers for converting <see cref="JsonElement"/> values into CLR objects.
/// </summary>
public static class JsonElementConverter
{
    /// <summary>
    /// Converts a <see cref="JsonElement"/> into a CLR object, expanding arrays and objects recursively.
    /// </summary>
    public static object? ConvertToObject(JsonElement element)
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
                .ToDictionary(prop => prop.Name, prop => ConvertToObject(prop.Value)),
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertToObject).ToArray(),
            _ => element.GetRawText()
        };
    }

    /// <summary>
    /// Converts a <see cref="JsonElement"/> into a CLR scalar when possible.
    /// Non-scalar values are returned as the original element.
    /// </summary>
    public static object? ConvertToScalar(JsonElement element)
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
            _ => element
        };
    }
}
