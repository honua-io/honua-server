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

internal sealed class FeatureAttributeReadContext
{
    // pg_type OIDs of json and jsonb (fixed in the PostgreSQL catalog).
    private const uint JsonOid = 114;
    private const uint JsonbOid = 3802;

    private readonly FeatureAttributeNameCache? _names;

    internal static FeatureAttributeReadContext Uncached { get; } = new(null);

    public FeatureAttributeReadContext() : this(new FeatureAttributeNameCache())
    {
    }

    private FeatureAttributeReadContext(FeatureAttributeNameCache? names)
    {
        _names = names;
    }

    // json/jsonb columns are parsed from the UTF-8 bytes Npgsql already holds; GetString would
    // first copy the whole object into a UTF-16 string for every row.
    internal void ReadInto(
        NpgsqlDataReader reader,
        int ordinal,
        Dictionary<string, object?> destination)
    {
        if (reader.IsDBNull(ordinal))
        {
            return;
        }

        if (reader.GetDataTypeOID(ordinal) is JsonbOid or JsonOid)
        {
            using var document = reader.GetFieldValue<JsonDocument>(ordinal);
            FeatureAttributeJsonReader.ReadInto(document.RootElement, destination, _names);
            return;
        }

        FeatureAttributeJsonReader.ReadInto(reader.GetString(ordinal), destination, _names);
    }
}

// JSONB stores object keys in a fixed order (by length, then bytes), so the rows of one result set
// present the same names at the same positions. A hit is checked in place and reuses the cached
// string; a miss allocates what an uncached read would. One instance per result set, one thread.
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
