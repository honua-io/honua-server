// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Infrastructure.GeoJson;

internal static partial class GeoJsonFeatureBaseBuilder
{
    private readonly record struct PropertySource(
        long FeatureId,
        IReadOnlyDictionary<string, object?> Attributes,
        PreparedSchema Schema);

    internal readonly record struct PropertyWriteField(string Name, JsonEncodedText JsonName);

    internal sealed class PropertyWritePlan
    {
        private readonly PreparedSchema _schema;
        private readonly GeoJsonFeatureBuildOptions _options;

        internal PropertyWritePlan(PreparedSchema schema, GeoJsonFeatureBuildOptions options)
        {
            _schema = schema;
            _options = options;
            Fields = schema.VisibleFields
                .Where(field => IncludesProperty(field.Name, schema, options))
                .Select(field => new PropertyWriteField(field.Name, JsonEncodedText.Encode(field.Name)))
                .ToArray();
            IdentifierFallback = options.IncludeObjectIdProperty &&
                !Fields.Any(field => field.Name.Equals(schema.ObjectIdFieldName, StringComparison.OrdinalIgnoreCase))
                    ? new PropertyWriteField(schema.ObjectIdFieldName, JsonEncodedText.Encode(schema.ObjectIdFieldName))
                    : null;
        }

        internal PropertyWriteField[] Fields { get; }
        internal PropertyWriteField? IdentifierFallback { get; }

        internal KeyValuePair<string, object?>? Read(Feature feature, PropertyWriteField field)
            => ReadProperty(field.Name, new PropertySource(feature.Id, feature.Attributes, _schema), _options);
    }

    private static bool IncludesProperty(
        string fieldName,
        PreparedSchema schema,
        GeoJsonFeatureBuildOptions options)
        => (!fieldName.Equals(schema.ObjectIdFieldName, StringComparison.OrdinalIgnoreCase) || options.IncludeObjectIdProperty) &&
           (options.ProjectedProperties is null || options.ProjectedProperties.Contains(fieldName));

    private static KeyValuePair<string, object?>? ReadProperty(
        string fieldName,
        PropertySource source,
        GeoJsonFeatureBuildOptions options)
    {
        var isIdentifier = fieldName.Equals(source.Schema.ObjectIdFieldName, StringComparison.OrdinalIgnoreCase);
        if (source.Attributes.TryGetValue(fieldName, out var value))
        {
            return new(fieldName, isIdentifier && options.IncludeObjectIdProperty && options.ResolveIdFromProperties
                ? UseCanonicalIdForNull(value, source.FeatureId)
                : CoerceProperty(fieldName, value, source.Schema));
        }

        return isIdentifier && options.IncludeObjectIdProperty
            ? new KeyValuePair<string, object?>(fieldName, source.FeatureId)
            : null;
    }
}
