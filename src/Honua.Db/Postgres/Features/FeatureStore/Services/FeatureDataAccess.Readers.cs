// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;
using Npgsql;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal sealed partial class FeatureDataAccess
{
    // Row readers are synchronous: the row is already buffered once ReadAsync returns, so a
    // per-row Task would only add an allocation. Loops over a result set pass one
    // FeatureAttributeReadContext so repeated attribute names are not re-allocated per row.
    private Feature ReadFeature(NpgsqlDataReader reader, FeatureAttributeReadContext? attributesReader = null)
    {
        var id = reader.GetInt64(0);
        var geometry = reader.IsDBNull(1) ? null : reader.GetFieldValue<byte[]>(1);

        if (reader.FieldCount < 3)
        {
            return Feature.Create(
                id,
                geometry,
                ImmutableDictionary<string, object?>.Empty.Add("objectid", id));
        }

        var attributes = ReadAttributes(reader, id, attributesReader);
        return Feature.Create(id, geometry, attributes);
    }

    private GmlFeature ReadGmlFeature(NpgsqlDataReader reader, FeatureAttributeReadContext? attributesReader = null)
    {
        var id = reader.GetInt64(0);
        var geometryGml = reader.IsDBNull(1) ? null : reader.GetString(1);
        var attributes = ReadAttributes(reader, id, attributesReader);
        return GmlFeature.Create(id, geometryGml, attributes);
    }

    private EncodedGeoJsonFeature ReadGeoJsonFeature(NpgsqlDataReader reader, FeatureAttributeReadContext? attributesReader = null)
    {
        var id = reader.GetInt64(0);
        var geometryGeoJson = reader.IsDBNull(1) ? null : reader.GetString(1);
        var attributes = ReadAttributes(reader, id, attributesReader);
        return EncodedGeoJsonFeature.Create(id, geometryGeoJson, attributes);
    }

    private static RawGeoJsonFeature ReadRawGeoJsonFeature(NpgsqlDataReader reader)
    {
        var id = reader.GetInt64(0);
        var geometryGeoJson = reader.IsDBNull(1) ? null : reader.GetString(1);
        if (reader.FieldCount >= 4)
        {
            var publicIdJson = reader.IsDBNull(2) ? null : reader.GetString(2);
            var propertiesJson = reader.IsDBNull(3) ? null : reader.GetString(3);
            return RawGeoJsonFeature.Create(id, geometryGeoJson, publicIdJson, propertiesJson);
        }

        var legacyPropertiesJson = reader.IsDBNull(2) ? null : reader.GetString(2);
        return RawGeoJsonFeature.Create(id, geometryGeoJson, legacyPropertiesJson);
    }

    private static RawGeoServicesFeature ReadRawGeoServicesFeature(NpgsqlDataReader reader)
    {
        var id = reader.GetInt64(0);
        if (reader.FieldCount >= 5)
        {
            var publicIdJson = reader.IsDBNull(1) ? null : reader.GetString(1);
            var attributesJson = reader.IsDBNull(2) ? null : reader.GetString(2);
            double? x = reader.IsDBNull(3) ? null : reader.GetDouble(3);
            double? y = reader.IsDBNull(4) ? null : reader.GetDouble(4);
            return RawGeoServicesFeature.Create(id, publicIdJson, attributesJson, x, y);
        }

        var legacyAttributesJson = reader.IsDBNull(1) ? null : reader.GetString(1);
        double? legacyX = reader.IsDBNull(2) ? null : reader.GetDouble(2);
        double? legacyY = reader.IsDBNull(3) ? null : reader.GetDouble(3);
        return RawGeoServicesFeature.Create(id, legacyAttributesJson, legacyX, legacyY);
    }

    private KmlFeature ReadKmlFeature(NpgsqlDataReader reader, FeatureAttributeReadContext? attributesReader = null)
    {
        var id = reader.GetInt64(0);
        var geometryKml = reader.IsDBNull(1) ? null : reader.GetString(1);
        var attributes = ReadAttributes(reader, id, attributesReader);
        return KmlFeature.Create(id, geometryKml, attributes);
    }

    private static Feature FilterFeatureFields(Feature feature, ImmutableArray<string> outFields)
    {
        if (outFields.IsDefaultOrEmpty)
        {
            return feature;
        }

        // Build the immutable result directly; a temporary Dictionary copied into it would be a
        // second per-row allocation. TryGetValue checks and fetches in one lookup.
        var filteredAttributes = ImmutableDictionary.CreateBuilder<string, object?>();
        foreach (var field in outFields)
        {
            if (feature.Attributes.TryGetValue(field, out var value))
            {
                filteredAttributes[field] = value;
            }
        }

        return Feature.Create(feature.Id, feature.Geometry, filteredAttributes.ToImmutable());
    }

    private static string SerializeToJsonString(Dictionary<string, object?> dictionary)
    {
        return JsonSerializer.Serialize(dictionary, FeatureAttributesJsonContext.Default.DictionaryStringObject);
    }

    private ImmutableDictionary<string, object?> ReadAttributes(
        NpgsqlDataReader reader,
        long id,
        FeatureAttributeReadContext? attributesReader)
    {
        var attributesDictionary = _dictionaryPool.Get();

        try
        {
            (attributesReader ?? FeatureAttributeReadContext.Uncached).ReadInto(reader, 2, attributesDictionary);

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
