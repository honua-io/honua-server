// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;

namespace Honua.Server.Features.Infrastructure.GeoJson;

internal readonly record struct GeoJsonFeatureBuildOptions(
    IReadOnlySet<string>? ProjectedProperties = null,
    Func<long, object?>? IdFactory = null,
    bool IncludeObjectIdProperty = false,
    bool IncludeObjectIdAlias = false,
    bool IncludeAdditionalAttributes = false,
    bool ResolveIdFromProperties = false);

internal static class GeoJsonFeatureBaseBuilder
{
    internal static GeoJsonFeatureBase Create(
        Feature feature,
        LayerDefinition layer,
        GeoJsonFeatureBuildOptions options = default)
        => CreateCore(
            feature.Id,
            feature.Attributes,
            layer,
            feature.Geometry != null,
            options);

    internal static GeoJsonFeatureBase Create(
        EncodedGeoJsonFeature feature,
        LayerDefinition layer,
        GeoJsonFeatureBuildOptions options = default)
        => CreateCore(
            feature.Id,
            feature.Attributes,
            layer,
            !string.IsNullOrWhiteSpace(feature.GeometryGeoJson),
            options);

    private static GeoJsonFeatureBase CreateCore(
        long featureId,
        IReadOnlyDictionary<string, object?> attributes,
        LayerDefinition layer,
        bool hasGeometry,
        GeoJsonFeatureBuildOptions options)
    {
        var properties = BuildProperties(featureId, attributes, layer, options);
        var id = options.IdFactory?.Invoke(featureId)
            ?? (options.ResolveIdFromProperties
                ? ResolveId(properties, layer.ObjectIdFieldName, featureId)
                : featureId);

        return GeoJsonFeatureBase.Create(id, properties, hasGeometry);
    }

    private static Dictionary<string, object?> BuildProperties(
        long featureId,
        IReadOnlyDictionary<string, object?> attributes,
        LayerDefinition layer,
        GeoJsonFeatureBuildOptions options)
    {
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var objectIdFieldName = layer.ObjectIdFieldName;
        var projectedProperties = options.ProjectedProperties;
        var shouldProjectAll = projectedProperties is null;
        var shouldIncludeObjectId = options.IncludeObjectIdProperty;

        foreach (var field in layer.AttributeFields)
        {
            var fieldName = field.Name;
            var isObjectIdField = fieldName.Equals(objectIdFieldName, StringComparison.OrdinalIgnoreCase);

            if (isObjectIdField && !shouldIncludeObjectId)
            {
                continue;
            }

            if (!shouldProjectAll && !projectedProperties!.Contains(fieldName))
            {
                continue;
            }

            if (attributes.TryGetValue(fieldName, out var value))
            {
                properties[fieldName] = value;
            }
            else if (isObjectIdField && shouldIncludeObjectId)
            {
                properties[fieldName] = featureId;
            }
        }

        if (options.IncludeAdditionalAttributes)
        {
            foreach (var (fieldName, value) in attributes)
            {
                if (properties.ContainsKey(fieldName))
                {
                    continue;
                }

                if (fieldName.Equals(objectIdFieldName, StringComparison.OrdinalIgnoreCase))
                {
                    if (shouldIncludeObjectId)
                    {
                        properties[fieldName] = value ?? featureId;
                    }

                    continue;
                }

                if (!shouldProjectAll && !projectedProperties!.Contains(fieldName))
                {
                    continue;
                }

                properties[fieldName] = value;
            }
        }

        if (shouldIncludeObjectId && !properties.ContainsKey(objectIdFieldName))
        {
            properties[objectIdFieldName] = featureId;
        }

        if (options.IncludeObjectIdAlias &&
            objectIdFieldName.Equals(FieldNames.ObjectId, StringComparison.OrdinalIgnoreCase) &&
            !properties.ContainsKey("OBJECTID") &&
            properties.TryGetValue(objectIdFieldName, out var objectIdValue))
        {
            properties["OBJECTID"] = objectIdValue;
        }

        return properties;
    }

    private static object ResolveId(
        Dictionary<string, object?> properties,
        string objectIdFieldName,
        long featureId)
    {
        if (properties.TryGetValue(objectIdFieldName, out var objectId))
        {
            return NormalizeIdValue(objectId) ?? featureId;
        }

        if (properties.TryGetValue("id", out var idValue))
        {
            return NormalizeIdValue(idValue) ?? featureId;
        }

        return featureId;
    }

    private static object? NormalizeIdValue(object? value)
        => value switch
        {
            JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.Number =>
                jsonElement.TryGetInt64(out var longValue) ? longValue : jsonElement.GetDouble(),
            JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.String =>
                jsonElement.GetString(),
            JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.True => true,
            JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.False => false,
            JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.Null => null,
            _ => value
        };
}
