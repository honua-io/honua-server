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
        var attributes = ReadAttributes(reader, id);
        return Task.FromResult(Feature.Create(id, geometry, attributes));
    }

    private Task<GmlFeature> ReadGmlFeatureAsync(NpgsqlDataReader reader, CancellationToken cancellationToken = default)
    {
        var id = reader.GetInt64(0);
        var geometryGml = reader.IsDBNull(1) ? null : reader.GetString(1);
        var attributes = ReadAttributes(reader, id);
        return Task.FromResult(GmlFeature.Create(id, geometryGml, attributes));
    }

    private Task<EncodedGeoJsonFeature> ReadGeoJsonFeatureAsync(NpgsqlDataReader reader, CancellationToken cancellationToken = default)
    {
        var id = reader.GetInt64(0);
        var geometryGeoJson = reader.IsDBNull(1) ? null : reader.GetString(1);
        var attributes = ReadAttributes(reader, id);
        return Task.FromResult(EncodedGeoJsonFeature.Create(id, geometryGeoJson, attributes));
    }

    private Task<KmlFeature> ReadKmlFeatureAsync(NpgsqlDataReader reader, CancellationToken cancellationToken = default)
    {
        var id = reader.GetInt64(0);
        var geometryKml = reader.IsDBNull(1) ? null : reader.GetString(1);
        var attributes = ReadAttributes(reader, id);
        return Task.FromResult(KmlFeature.Create(id, geometryKml, attributes));
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

    private ImmutableDictionary<string, object?> ReadAttributes(NpgsqlDataReader reader, long id)
    {
        var attributesJson = reader.IsDBNull(2) ? null : reader.GetString(2);
        var attributesDictionary = _dictionaryPool.Get();

        try
        {
            var deserializedDict = string.IsNullOrWhiteSpace(attributesJson)
                ? new Dictionary<string, object?>()
                : DeserializeFromJsonString(attributesJson) ?? new Dictionary<string, object?>();

            foreach (var (key, value) in deserializedDict)
            {
                attributesDictionary[key] = ConvertJsonElementToObject(value);
            }

            attributesDictionary["objectid"] = id;

            if (reader.FieldCount > 3)
            {
                for (var i = 3; i < reader.FieldCount; i++)
                {
                    var fieldName = reader.GetName(i);
                    if (fieldName.Equals(FeatureQueryEncoding.InternalTotalCountColumn, StringComparison.OrdinalIgnoreCase))
                    {
                        attributesDictionary[FeatureQueryEncoding.InternalTotalCountColumn] = reader.IsDBNull(i)
                            ? null
                            : reader.GetFieldValue<long>(i);
                        continue;
                    }

                    if (fieldName.Equals(FeatureQueryEncoding.InternalDistanceColumn, StringComparison.OrdinalIgnoreCase))
                    {
                        attributesDictionary[FeatureQueryEncoding.PublicDistanceColumn] = reader.IsDBNull(i)
                            ? null
                            : reader.GetFieldValue<double>(i);
                    }
                }
            }

            return attributesDictionary.ToImmutableDictionary();
        }
        finally
        {
            _dictionaryPool.Return(attributesDictionary);
        }
    }
}
