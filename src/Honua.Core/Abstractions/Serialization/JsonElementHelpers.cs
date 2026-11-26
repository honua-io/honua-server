// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Serialization;

public readonly record struct JsonStringPropertyResult
{
    public bool IsValid { get; init; }

    public string? Value { get; init; }

    public static JsonStringPropertyResult Invalid => new() { IsValid = false };

    public static JsonStringPropertyResult Valid(string? value) => new() { IsValid = true, Value = value };
}

public static class JsonElementHelpers
{
    public static JsonStringPropertyResult ReadStringProperty(JsonElement element, string propertyName)
    {
        ArgumentNullException.ThrowIfNull(propertyName);

        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return JsonStringPropertyResult.Valid(null);
        }

        return property.ValueKind == JsonValueKind.String
            ? JsonStringPropertyResult.Valid(property.GetString())
            : JsonStringPropertyResult.Invalid;
    }
}
