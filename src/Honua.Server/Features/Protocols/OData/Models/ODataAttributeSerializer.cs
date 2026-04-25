// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Shared.Models;

namespace Honua.Server.Features.Protocols.OData.Models;

/// <summary>
/// Shared helpers for serializing and normalizing OData attribute payloads.
/// </summary>
internal static class ODataAttributeSerializer
{
    public static Dictionary<string, object?> Serialize(IReadOnlyDictionary<string, object?> attributes)
        => NormalizeAttributes(attributes);

    public static Dictionary<string, object?> Deserialize(IReadOnlyDictionary<string, object?>? attributes)
        => attributes == null ? new Dictionary<string, object?>() : NormalizeAttributes(attributes);

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
            return JsonElementConverter.ConvertToObject(element);
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

}
