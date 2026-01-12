// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Server.Tests.Features.OData;

internal static class ODataTestHelpers
{
    private static readonly HashSet<string> _reservedProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "ObjectId",
        "LayerId",
        "Geometry",
        "Attributes",
        "@odata.context",
        "@odata.type",
        "@odata.id",
        "@odata.etag"
    };

    internal static JsonElement ParseAttributes(JsonElement feature)
    {
        if (feature.TryGetProperty("Attributes", out var attributesElement))
        {
            if (attributesElement.ValueKind == JsonValueKind.String)
            {
                var json = attributesElement.GetString();
                if (string.IsNullOrWhiteSpace(json))
                {
                    return EmptyObject();
                }

                using var document = JsonDocument.Parse(json);
                return document.RootElement.Clone();
            }

            if (attributesElement.ValueKind == JsonValueKind.Object)
            {
                return attributesElement.Clone();
            }
        }

        var attributes = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in feature.EnumerateObject())
        {
            if (IsReservedProperty(property.Name))
            {
                continue;
            }

            attributes[property.Name] = property.Value.Clone();
        }

        var serialized = JsonSerializer.Serialize(attributes);
        using var attributesDoc = JsonDocument.Parse(serialized);
        return attributesDoc.RootElement.Clone();
    }

    private static bool IsReservedProperty(string propertyName)
    {
        if (propertyName.StartsWith("@odata.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return _reservedProperties.Contains(propertyName);
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}
