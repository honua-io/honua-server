// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.GeoJson;
using Honua.Protocols.Ogc.Common;

namespace Honua.Protocols.Ogc.Api.Features;

internal sealed partial class OgcFeaturesQueryHandler
{
    private readonly record struct DirectFeaturePayloadContext(
        MetadataV2Object Resource,
        AxisOrder AxisOrder,
        OgcFeaturesGeometryServices GeometryServices,
        ImmutableHashSet<string>? ProjectedProperties,
        ImmutableArray<Link> Links,
        long? NumberMatched);

    private readonly record struct DirectPropertyField(
        string Name,
        JsonEncodedText EncodedName,
        bool IsObjectIdField);

    private sealed record DirectFeatureWritePlan(
        GeoJsonFeatureBaseBuilder.PreparedSchema Schema,
        DirectPropertyField[] Fields,
        MetadataV2Object Resource,
        AxisOrder AxisOrder,
        OgcFeaturesGeometryServices GeometryServices);

    private static bool CanUseDirectFeatureWrite(
        bool useNativeGeoJson,
        string outputFormat,
        OgcFeatureLinkTemplate? linkTemplate) =>
        useNativeGeoJson &&
        linkTemplate is null &&
        !string.Equals(outputFormat, MediaTypes.Html, StringComparison.OrdinalIgnoreCase);

    private static OgcCollectionPayload CreateDirectFeatureCollectionPayload(
        ImmutableArray<Feature> features,
        DirectFeaturePayloadContext context)
    {
        var plan = BuildDirectFeatureWritePlan(context);
        var estimatedCapacity = Math.Max(4096, (features.Length * 320) + 512);
        var buffer = new ArrayBufferWriter<byte>(estimatedCapacity);
        using var writer = new Utf8JsonWriter(buffer);

        WriteCollectionHeader(writer);
        foreach (var feature in features)
        {
            WriteDirectFeature(writer, feature, plan);
        }

        return CompleteCollectionPayload(
            writer,
            buffer,
            new CollectionFooter(features.Length, context.Links, context.NumberMatched));
    }

    private static DirectFeatureWritePlan BuildDirectFeatureWritePlan(DirectFeaturePayloadContext context)
    {
        var schema = new GeoJsonFeatureBaseBuilder.PreparedSchema(context.Resource, includeAdditionalAttributes: false);
        var objectIdFieldName = schema.ObjectIdFieldName;
        var includeObjectId = !objectIdFieldName.Equals(FieldNames.ObjectId, StringComparison.OrdinalIgnoreCase) &&
            (context.ProjectedProperties is null || context.ProjectedProperties.Contains(objectIdFieldName));
        var hiddenIdName = schema.HiddenPrimaryIdFieldName;

        var fields = new List<DirectPropertyField>(schema.VisibleFields.Length + 1);
        var hasObjectIdField = false;
        foreach (var field in schema.VisibleFields)
        {
            var fieldName = field.Name;
            var isObjectIdField = fieldName.Equals(objectIdFieldName, StringComparison.OrdinalIgnoreCase);
            if ((isObjectIdField && !includeObjectId) ||
                (hiddenIdName is not null && fieldName.Equals(hiddenIdName, StringComparison.OrdinalIgnoreCase)) ||
                (context.ProjectedProperties is not null && !context.ProjectedProperties.Contains(fieldName)))
            {
                continue;
            }

            hasObjectIdField |= isObjectIdField;
            fields.Add(new DirectPropertyField(fieldName, JsonEncodedText.Encode(fieldName), isObjectIdField));
        }

        if (includeObjectId && !hasObjectIdField &&
            (hiddenIdName is null || !objectIdFieldName.Equals(hiddenIdName, StringComparison.OrdinalIgnoreCase)))
        {
            fields.Add(new DirectPropertyField(objectIdFieldName, JsonEncodedText.Encode(objectIdFieldName), true));
        }

        return new DirectFeatureWritePlan(
            schema,
            [.. fields],
            context.Resource,
            context.AxisOrder,
            context.GeometryServices);
    }

    private static void WriteDirectFeature(
        Utf8JsonWriter writer,
        Feature feature,
        DirectFeatureWritePlan plan)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "Feature");
        writer.WritePropertyName("id");
        WriteDirectValue(writer, OgcFeatureIdentifierResolver.GetPublicId(feature, plan.Resource));
        writer.WritePropertyName("geometry");
        WriteDirectGeometry(writer, feature.Geometry, plan);
        writer.WritePropertyName("properties");
        WriteDirectProperties(writer, feature, plan);
        writer.WriteEndObject();
    }

    private static void WriteDirectGeometry(
        Utf8JsonWriter writer,
        byte[]? wkb,
        DirectFeatureWritePlan plan)
    {
        if (plan.GeometryServices.TryWriteWkbPoint(writer, wkb, plan.AxisOrder))
        {
            return;
        }

        var geometry = plan.GeometryServices.ConvertWkbToSimpleGeometry(wkb, plan.AxisOrder);
        if (geometry is null)
        {
            writer.WriteNullValue();
            return;
        }

        JsonSerializer.Serialize(writer, geometry, OgcJsonContext.Default.SimpleGeoJsonGeometry);
    }

    private static void WriteDirectProperties(
        Utf8JsonWriter writer,
        Feature feature,
        DirectFeatureWritePlan plan)
    {
        writer.WriteStartObject();
        var attributes = feature.Attributes;
        foreach (var field in plan.Fields)
        {
            if (attributes.TryGetValue(field.Name, out var rawValue))
            {
                writer.WritePropertyName(field.EncodedName);
                WriteDirectValue(writer, GeoJsonFeatureBaseBuilder.CoerceProperty(field.Name, rawValue, plan.Schema));
            }
            else if (field.IsObjectIdField)
            {
                writer.WritePropertyName(field.EncodedName);
                writer.WriteNumberValue(feature.Id);
            }
        }

        writer.WriteEndObject();
    }

    private static void WriteDirectValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;
            case string s:
                writer.WriteStringValue(s);
                return;
            case long l:
                writer.WriteNumberValue(l);
                return;
            case int i:
                writer.WriteNumberValue(i);
                return;
            case double d when double.IsFinite(d):
                writer.WriteNumberValue(d);
                return;
            case bool b:
                writer.WriteBooleanValue(b);
                return;
            case JsonElement element:
                element.WriteTo(writer);
                return;
            case decimal dec:
                writer.WriteNumberValue(dec);
                return;
            default:
                WriteDirectFallbackValue(writer, value);
                return;
        }
    }

    private static void WriteDirectFallbackValue(Utf8JsonWriter writer, object value)
    {
        switch (value)
        {
            case float f when float.IsFinite(f):
                writer.WriteNumberValue(f);
                return;
            case short s16:
                writer.WriteNumberValue(s16);
                return;
            case byte u8:
                writer.WriteNumberValue(u8);
                return;
            case DateTimeOffset dto:
                writer.WriteStringValue(dto);
                return;
            case DateTime dt:
                writer.WriteStringValue(dt);
                return;
            case DateOnly d:
                writer.WriteStringValue(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                return;
            case Guid g:
                writer.WriteStringValue(g);
                return;
            default:
                JsonSerializer.Serialize(writer, value, OgcJsonContext.Default.Object);
                return;
        }
    }
}
