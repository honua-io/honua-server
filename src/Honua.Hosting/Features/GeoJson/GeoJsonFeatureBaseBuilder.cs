// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;

namespace Honua.Infrastructure.GeoJson;

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

        var declaredAttributeFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visibleAttributeFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Date/datetime fields must be emitted as RFC 3339 strings to honor the
        // GeoJSON/OGC contract (and the collection's queryables schema). Stored
        // values arrive in different CLR shapes depending on the write path
        // (epoch-millisecond long from Esri applyEdits vs ISO string from seeds),
        // so map field name -> whether it is a date-only field and coerce on write.
        var dateOnlyFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dateTimeFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in resource.SchemaFields)
        {
            if (!IsGeometryField(field))
            {
                declaredAttributeFields.Add(field.Name);
                if (!field.Hidden)
                {
                    visibleAttributeFields.Add(field.Name);
                }

                if (field.Type == MetadataV2FieldType.Date)
                {
                    dateOnlyFields.Add(field.Name);
                }
                else if (field.Type == MetadataV2FieldType.DateTime)
                {
                    dateTimeFields.Add(field.Name);
                }
            }
        }

        foreach (var field in resource.SchemaFields.Where(static field => !field.Hidden))
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
                properties[fieldName] = CoerceProperty(fieldName, value, dateOnlyFields, dateTimeFields);
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

                properties[fieldName] = CoerceProperty(fieldName, value, dateOnlyFields, dateTimeFields);
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

    // Normalize date/datetime field values to RFC 3339 for GeoJSON/OGC output.
    // Stored values arrive as epoch-millisecond longs (Esri applyEdits), numeric
    // strings, ISO strings (seeds), or CLR date types depending on the write
    // path; emit one consistent, schema-conformant representation. Non-date
    // fields and unparseable values pass through unchanged.
    private static object? CoerceProperty(
        string fieldName,
        object? value,
        HashSet<string> dateOnlyFields,
        HashSet<string> dateTimeFields)
    {
        if (value is null)
        {
            return null;
        }

        if (dateOnlyFields.Contains(fieldName))
        {
            return TryCoerceDate(value, out var utc)
                ? utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : value;
        }

        if (dateTimeFields.Contains(fieldName))
        {
            return TryCoerceDate(value, out var utc)
                ? utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
                : value;
        }

        return value;
    }

    private static bool TryCoerceDate(object value, out DateTimeOffset utc)
    {
        switch (value)
        {
            case DateTimeOffset dto:
                utc = dto.ToUniversalTime();
                return true;
            case DateTime dt:
                utc = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
                return true;
            case DateOnly d:
                utc = new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                return true;
            case long epochMs:
                utc = DateTimeOffset.FromUnixTimeMilliseconds(epochMs);
                return true;
            case int epochMsInt:
                utc = DateTimeOffset.FromUnixTimeMilliseconds(epochMsInt);
                return true;
            case double epochMsDouble:
                utc = DateTimeOffset.FromUnixTimeMilliseconds((long)epochMsDouble);
                return true;
            case string text:
                return TryParseDateText(text, out utc);
            case JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var jsonEpoch):
                utc = DateTimeOffset.FromUnixTimeMilliseconds(jsonEpoch);
                return true;
            case JsonElement element when element.ValueKind == JsonValueKind.String:
                return TryParseDateText(element.GetString() ?? string.Empty, out utc);
            default:
                utc = default;
                return false;
        }
    }

    private static bool TryParseDateText(string text, out DateTimeOffset utc)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            utc = default;
            return false;
        }

        // Esri may persist a date as epoch-milliseconds rendered as text.
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epochMs))
        {
            utc = DateTimeOffset.FromUnixTimeMilliseconds(epochMs);
            return true;
        }

        if (DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed))
        {
            utc = parsed;
            return true;
        }

        utc = default;
        return false;
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
