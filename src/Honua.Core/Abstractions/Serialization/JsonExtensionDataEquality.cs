// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Serialization;

internal static class JsonExtensionDataEquality
{
    public static bool Equals(IReadOnlyDictionary<string, JsonElement>? left, IReadOnlyDictionary<string, JsonElement>? right)
    {
        if ((left?.Count ?? 0) != (right?.Count ?? 0))
        {
            return false;
        }

        return left is null || left.All(property => right is not null &&
            right.TryGetValue(property.Key, out var value) && JsonElement.DeepEquals(property.Value, value));
    }

    public static int PropertyNamesHashCode(IReadOnlyDictionary<string, JsonElement>? properties)
    {
        var hash = 0;
        if (properties is not null)
        {
            foreach (var name in properties.Keys)
            {
                hash ^= StringComparer.Ordinal.GetHashCode(name);
            }
        }
        return hash;
    }
}
