// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Shared.Models;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

/// <summary>
/// Reads a PostgreSQL JSONB attributes object directly into a pooled dictionary.
/// </summary>
internal static class FeatureAttributeJsonReader
{
    internal static void ReadInto(string? json, Dictionary<string, object?> destination)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        // Deserializing Dictionary<string, object?> first allocates a temporary
        // dictionary and an independently owned JsonElement for each scalar.
        // JSONB already guarantees unique property names; read its object once.
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Feature attributes must be a JSON object.");
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            var value = property.Value;
            destination[property.Name] = value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                ? value.Clone() // Nested values outlive this document, as in the original reader.
                : JsonElementConverter.ConvertToScalar(value);
        }
    }
}
