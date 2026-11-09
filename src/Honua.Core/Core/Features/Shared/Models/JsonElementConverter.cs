// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Features.Shared.Models;

public static class JsonElementConverter
{
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
