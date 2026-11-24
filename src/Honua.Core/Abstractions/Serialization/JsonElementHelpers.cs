// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Serialization;

public static class JsonElementHelpers
{
    public static bool TryReadStringProperty(JsonElement element, string propertyName, out string? value)
    {
        ArgumentNullException.ThrowIfNull(propertyName);

        if (!element.TryGetProperty(propertyName, out var property))
        {
            value = null;
            return true;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            value = null;
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            value = null;
            return false;
        }

        value = property.GetString();
        return true;
    }
}
