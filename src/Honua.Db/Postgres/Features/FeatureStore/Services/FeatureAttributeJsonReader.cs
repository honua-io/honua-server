// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Shared.Models;
using Npgsql;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

/// <summary>
/// Reads a PostgreSQL JSONB attributes object directly into a pooled dictionary.
/// </summary>
internal static class FeatureAttributeJsonReader
{
    // pg_type OIDs of json and jsonb (fixed in the PostgreSQL catalog).
    private const uint JsonOid = 114;
    private const uint JsonbOid = 3802;

    /// <summary>
    /// Reads the attributes column at <paramref name="ordinal"/>. A json/jsonb column is parsed from
    /// the UTF-8 bytes Npgsql already holds, which skips the UTF-16 copy of the whole object that
    /// <see cref="NpgsqlDataReader.GetString(int)"/> would allocate for every row.
    /// </summary>
    internal static void ReadInto(
        NpgsqlDataReader reader,
        int ordinal,
        Dictionary<string, object?> destination,
        FeatureAttributeNameCache? names = null)
    {
        if (reader.IsDBNull(ordinal))
        {
            return;
        }

        if (reader.GetDataTypeOID(ordinal) is JsonbOid or JsonOid)
        {
            using var document = reader.GetFieldValue<JsonDocument>(ordinal);
            ReadInto(document.RootElement, destination, names);
            return;
        }

        ReadInto(reader.GetString(ordinal), destination, names);
    }

    internal static void ReadInto(
        string? json,
        Dictionary<string, object?> destination,
        FeatureAttributeNameCache? names = null)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        // Deserializing Dictionary<string, object?> first allocates a temporary
        // dictionary and an independently owned JsonElement for each scalar.
        // JSONB already guarantees unique property names; read its object once.
        using var document = JsonDocument.Parse(json);
        ReadInto(document.RootElement, destination, names);
    }

    internal static void ReadInto(
        JsonElement attributes,
        Dictionary<string, object?> destination,
        FeatureAttributeNameCache? names = null)
    {
        if (attributes.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (attributes.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Feature attributes must be a JSON object.");
        }

        var index = 0;
        foreach (var property in attributes.EnumerateObject())
        {
            var name = names is null ? property.Name : names.GetName(property, index);
            index++;
            var value = property.Value;
            destination[name] = value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                ? value.Clone() // Nested values outlive this document, as in the original reader.
                : JsonElementConverter.ConvertToScalar(value);
        }
    }
}

/// <summary>
/// Reuses attribute-name strings across the rows of one result set. JSONB stores object
/// keys in a fixed order (by length, then bytes), so rows of one layer present the same names at
/// the same positions. A hit compares the name in place and returns the cached string instead of
/// allocating a new one; a miss allocates exactly what an uncached read would. One instance per
/// result set; not thread-safe.
/// </summary>
internal sealed class FeatureAttributeNameCache
{
    private string?[] _names = new string?[16];

    internal string GetName(JsonProperty property, int index)
    {
        if (index >= _names.Length)
        {
            Array.Resize(ref _names, Math.Max(index + 1, _names.Length * 2));
        }

        var cached = _names[index];
        if (cached is not null && property.NameEquals(cached))
        {
            return cached;
        }

        var name = property.Name;
        _names[index] = name;
        return name;
    }
}
