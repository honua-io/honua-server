// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
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

    // -----------------------------------------------------------------------
    // Metadata v2 overloads
    // -----------------------------------------------------------------------

    internal static GeoJsonFeatureBase Create(
        Feature feature,
        MetadataV2Resource resource,
        GeoJsonFeatureBuildOptions options = default)
        => CreateCore(
            feature.Id,
            feature.Attributes,
            resource,
            feature.Geometry != null,
            options);

    internal static GeoJsonFeatureBase Create(
        EncodedGeoJsonFeature feature,
        MetadataV2Resource resource,
        GeoJsonFeatureBuildOptions options = default)
        => CreateCore(
            feature.Id,
            feature.Attributes,
            resource,
            !string.IsNullOrWhiteSpace(feature.GeometryGeoJson),
            options);

    private static GeoJsonFeatureBase CreateCore(
        long featureId,
        IReadOnlyDictionary<string, object?> attributes,
        MetadataV2Resource resource,
        bool hasGeometry,
        GeoJsonFeatureBuildOptions options)
    {
        var objectIdFieldName = ResolveObjectIdFieldName(resource);
        var properties = BuildProperties(featureId, attributes, resource, objectIdFieldName, options);
        var id = options.IdFactory?.Invoke(featureId)
            ?? (options.ResolveIdFromProperties
                ? ResolveId(properties, objectIdFieldName, featureId)
                : featureId);

        return GeoJsonFeatureBase.Create(id, properties, hasGeometry);
    }

    private static Dictionary<string, object?> BuildProperties(
        long featureId,
        IReadOnlyDictionary<string, object?> attributes,
        MetadataV2Resource resource,
        string objectIdFieldName,
        GeoJsonFeatureBuildOptions options)
    {
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        var projectedProperties = options.ProjectedProperties;
        var shouldProjectAll = projectedProperties is null;
        var shouldIncludeObjectId = options.IncludeObjectIdProperty;

        // V2 has no IsHidden/IsVisible concept: every non-geometry field is both
        // an attribute field and visible. The two sets are therefore identical.
        var declaredAttributeFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in resource.SchemaFields)
        {
            if (!IsGeometryField(field))
            {
                declaredAttributeFields.Add(field.Name);
            }
        }

        foreach (var field in resource.SchemaFields)
        {
            if (IsGeometryField(field))
            {
                continue;
            }

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
                if (fieldName.Equals(objectIdFieldName, StringComparison.OrdinalIgnoreCase))
                {
                    if (shouldIncludeObjectId && !properties.ContainsKey(fieldName))
                    {
                        properties[fieldName] = value ?? featureId;
                    }

                    continue;
                }

                if (ContainsKeyIgnoreCase(properties, fieldName))
                {
                    continue;
                }

                // V2: declared attribute fields are always visible, so the v1 "declared
                // but not visible → skip" branch collapses to a no-op and is omitted.

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
            TryGetValueIgnoreCase(properties, objectIdFieldName, out var objectIdValue))
        {
            properties["OBJECTID"] = objectIdValue;
        }

        return properties;
    }

    private static string ResolveObjectIdFieldName(MetadataV2Resource resource)
        => resource.FindPrimaryIdField()?.Name ?? "objectid";

    private static bool IsGeometryField(MetadataV2Field field)
        => field.Type is MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography;

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
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        var objectIdFieldName = layer.ObjectIdFieldName;
        var projectedProperties = options.ProjectedProperties;
        var shouldProjectAll = projectedProperties is null;
        var shouldIncludeObjectId = options.IncludeObjectIdProperty;
        var declaredAttributeFields = layer.AttributeFields
            .Select(static field => field.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visibleAttributeFields = layer.VisibleAttributeFields
            .Select(static field => field.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var field in layer.VisibleAttributeFields)
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
                if (fieldName.Equals(objectIdFieldName, StringComparison.OrdinalIgnoreCase))
                {
                    if (shouldIncludeObjectId && !properties.ContainsKey(fieldName))
                    {
                        properties[fieldName] = value ?? featureId;
                    }

                    continue;
                }

                if (ContainsKeyIgnoreCase(properties, fieldName))
                {
                    continue;
                }

                if (declaredAttributeFields.Contains(fieldName) && !visibleAttributeFields.Contains(fieldName))
                {
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
            TryGetValueIgnoreCase(properties, objectIdFieldName, out var objectIdValue))
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
        if (TryGetValueIgnoreCase(properties, objectIdFieldName, out var objectId))
        {
            return NormalizeIdValue(objectId) ?? featureId;
        }

        if (TryGetValueIgnoreCase(properties, "id", out var idValue))
        {
            return NormalizeIdValue(idValue) ?? featureId;
        }

        return featureId;
    }

    private static bool ContainsKeyIgnoreCase(
        IReadOnlyDictionary<string, object?> properties,
        string key)
        => TryGetValueIgnoreCase(properties, key, out _);

    private static bool TryGetValueIgnoreCase(
        IReadOnlyDictionary<string, object?> properties,
        string key,
        out object? value)
    {
        foreach (var (candidateKey, candidateValue) in properties)
        {
            if (candidateKey.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                value = candidateValue;
                return true;
            }
        }

        value = null;
        return false;
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
