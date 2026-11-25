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
    private enum DirectPropertyKind
    {
        Attribute,
        ObjectId
    }

    private sealed class DirectFeaturePayloadContext
    {
        public required MetadataV2Object Resource { get; init; }
        public required AxisOrder AxisOrder { get; init; }
        public required OgcFeaturesGeometryServices GeometryServices { get; init; }
        public ImmutableHashSet<string>? ProjectedProperties { get; init; }
        public ImmutableArray<Link> Links { get; init; }
        public long? NumberMatched { get; init; }
        public OgcFeatureLinkTemplate? LinkTemplate { get; init; }
        public GeoJsonFeatureBaseBuilder.PreparedSchema? Schema { get; init; }
    }

    private readonly record struct DirectPropertyField(
        string Name,
        JsonEncodedText EncodedName,
        DirectPropertyKind Kind);

    private sealed class DirectFeatureWritePlan
    {
        public required GeoJsonFeatureBaseBuilder.PreparedSchema Schema { get; init; }
        public required DirectPropertyField[] Fields { get; init; }
        public required MetadataV2Object Resource { get; init; }
        public required AxisOrder AxisOrder { get; init; }
        public required OgcFeaturesGeometryServices GeometryServices { get; init; }
    }

    private static bool CanUseDirectFeatureWrite(string outputFormat, OgcFeatureLinkTemplate? linkTemplate) =>
        linkTemplate is null &&
        (string.Equals(outputFormat, MediaTypes.GeoJson, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(outputFormat, MediaTypes.Json, StringComparison.OrdinalIgnoreCase));

    private static GeoJsonFeature[] MaterializeDirectFallbackFeatures(
        ImmutableArray<Feature> items,
        DirectFeaturePayloadContext context)
    {
        var result = new GeoJsonFeature[items.Length];
        for (var i = 0; i < items.Length; i++)
        {
            var feature = items[i];
            ImmutableArray<Link>? featureLinks = context.LinkTemplate?.Build(
                OgcFeatureIdentifierResolver.FormatPublicId(feature, context.Resource));
            result[i] = ToOgcFeature(
                feature,
                context.Resource,
                context.AxisOrder,
                context.GeometryServices,
                context.ProjectedProperties,
                featureLinks,
                context.Schema);
        }

        return result;
    }

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
        var fields = ResolveDirectPropertyFields(schema, context.ProjectedProperties);
        return new DirectFeatureWritePlan
        {
            Schema = schema,
            Fields = fields,
            Resource = context.Resource,
            AxisOrder = context.AxisOrder,
            GeometryServices = context.GeometryServices
        };
    }

    private static DirectPropertyField[] ResolveDirectPropertyFields(
        GeoJsonFeatureBaseBuilder.PreparedSchema schema,
        ImmutableHashSet<string>? projectedProperties)
    {
        var objectIdFieldName = schema.ObjectIdFieldName;
        var includeObjectId = !objectIdFieldName.Equals(FieldNames.ObjectId, StringComparison.OrdinalIgnoreCase) &&
            (projectedProperties is null || projectedProperties.Contains(objectIdFieldName));
        var hiddenIdName = schema.HiddenPrimaryIdFieldName;
        var fields = new List<DirectPropertyField>(schema.VisibleFields.Length + 1);
        var hasObjectIdField = false;

        foreach (var field in schema.VisibleFields)
        {
            var isObjectIdField = field.Name.Equals(objectIdFieldName, StringComparison.OrdinalIgnoreCase);
            if ((isObjectIdField && !includeObjectId) ||
                (hiddenIdName is not null && field.Name.Equals(hiddenIdName, StringComparison.OrdinalIgnoreCase)) ||
                (projectedProperties is not null && !projectedProperties.Contains(field.Name)))
            {
                continue;
            }

            hasObjectIdField |= isObjectIdField;
            var kind = isObjectIdField ? DirectPropertyKind.ObjectId : DirectPropertyKind.Attribute;
            fields.Add(new DirectPropertyField(field.Name, JsonEncodedText.Encode(field.Name), kind));
        }

        if (includeObjectId && !hasObjectIdField &&
            (hiddenIdName is null || !objectIdFieldName.Equals(hiddenIdName, StringComparison.OrdinalIgnoreCase)))
        {
            fields.Add(new DirectPropertyField(objectIdFieldName, JsonEncodedText.Encode(objectIdFieldName), DirectPropertyKind.ObjectId));
        }

        return [.. fields];
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
            else if (field.Kind == DirectPropertyKind.ObjectId)
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
