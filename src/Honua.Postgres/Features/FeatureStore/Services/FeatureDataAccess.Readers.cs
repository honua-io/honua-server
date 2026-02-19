// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Npgsql;

namespace Honua.Postgres.Features.FeatureStore.Services;

internal sealed partial class FeatureDataAccess
{
    private Task<Feature> ReadFeatureAsync(NpgsqlDataReader reader, CancellationToken cancellationToken = default)
    {
        var id = reader.GetInt64(0);
        var geometry = reader.IsDBNull(1) ? null : reader.GetFieldValue<byte[]>(1);
        var attributesJson = reader.IsDBNull(2) ? null : reader.GetString(2);

        // Use pooled dictionary for performance
        var attributesDictionary = _dictionaryPool.Get();
        try
        {
            // Deserialize JSON using AOT-compatible source generators
            var deserializedDict = string.IsNullOrWhiteSpace(attributesJson)
                ? new Dictionary<string, object?>()
                : DeserializeFromJsonString(attributesJson) ?? new Dictionary<string, object?>();

            // Convert JsonElement values to primitive types for compatibility
            foreach (var (key, value) in deserializedDict)
            {
                attributesDictionary[key] = ConvertJsonElementToObject(value);
            }

            // Inject objectid into attributes for GeoServices FeatureServer compatibility
            attributesDictionary["objectid"] = id;

            if (reader.FieldCount > 3)
            {
                for (var i = 3; i < reader.FieldCount; i++)
                {
                    var fieldName = reader.GetName(i);
                    if (fieldName.Equals("total_count", StringComparison.OrdinalIgnoreCase))
                    {
                        attributesDictionary["total_count"] = reader.IsDBNull(i) ? null : reader.GetFieldValue<long>(i);
                        continue;
                    }

                    if (fieldName.Equals("distance", StringComparison.OrdinalIgnoreCase))
                    {
                        attributesDictionary["distance"] = reader.IsDBNull(i) ? null : reader.GetFieldValue<double>(i);
                    }
                }
            }

            var attributes = attributesDictionary.ToImmutableDictionary();
            return Task.FromResult(Feature.Create(id, geometry, attributes));
        }
        finally
        {
            _dictionaryPool.Return(attributesDictionary);
        }
    }

    private Task<GmlFeature> ReadGmlFeatureAsync(NpgsqlDataReader reader, CancellationToken cancellationToken = default)
    {
        var id = reader.GetInt64(0);
        var geometryGml = reader.IsDBNull(1) ? null : reader.GetString(1);
        var attributesJson = reader.IsDBNull(2) ? null : reader.GetString(2);

        // Use pooled dictionary for performance
        var attributesDictionary = _dictionaryPool.Get();
        try
        {
            // Deserialize JSON using AOT-compatible source generators
            var deserializedDict = string.IsNullOrWhiteSpace(attributesJson)
                ? new Dictionary<string, object?>()
                : DeserializeFromJsonString(attributesJson) ?? new Dictionary<string, object?>();

            // Convert JsonElement values to primitive types for compatibility
            foreach (var (key, value) in deserializedDict)
            {
                attributesDictionary[key] = ConvertJsonElementToObject(value);
            }

            // Inject objectid into attributes for GeoServices FeatureServer compatibility
            attributesDictionary["objectid"] = id;

            if (reader.FieldCount > 3)
            {
                for (var i = 3; i < reader.FieldCount; i++)
                {
                    var fieldName = reader.GetName(i);
                    if (fieldName.Equals("total_count", StringComparison.OrdinalIgnoreCase))
                    {
                        attributesDictionary["total_count"] = reader.IsDBNull(i) ? null : reader.GetFieldValue<long>(i);
                        continue;
                    }

                    if (fieldName.Equals("distance", StringComparison.OrdinalIgnoreCase))
                    {
                        attributesDictionary["distance"] = reader.IsDBNull(i) ? null : reader.GetFieldValue<double>(i);
                    }
                }
            }

            var attributes = attributesDictionary.ToImmutableDictionary();
            return Task.FromResult(GmlFeature.Create(id, geometryGml, attributes));
        }
        finally
        {
            _dictionaryPool.Return(attributesDictionary);
        }
    }

    private static Feature FilterFeatureFields(Feature feature, ImmutableArray<string> outFields)
    {
        if (outFields.IsDefaultOrEmpty)
        {
            return feature;
        }

        var filteredAttributes = new Dictionary<string, object?>();

        foreach (var field in outFields)
        {
            if (feature.Attributes.TryGetValue(field, out var value))
            {
                filteredAttributes[field] = value;
            }
        }

        return Feature.Create(feature.Id, feature.Geometry, filteredAttributes.ToImmutableDictionary());
    }

    private static string SerializeToJsonString(Dictionary<string, object?> dictionary)
    {
        return JsonSerializer.Serialize(dictionary, FeatureAttributesJsonContext.Default.DictionaryStringObject);
    }

    private static Dictionary<string, object?>? DeserializeFromJsonString(string json)
    {
        return JsonSerializer.Deserialize(json, FeatureAttributesJsonContext.Default.DictionaryStringObject);
    }

    private static object? ConvertJsonElementToObject(object? value)
    {
        if (value is JsonElement element)
        {
            return JsonElementConverter.ConvertToScalar(element);
        }

        return value;
    }
}
