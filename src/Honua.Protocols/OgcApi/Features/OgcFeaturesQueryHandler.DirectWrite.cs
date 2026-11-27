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

    private sealed class DirectFeatureWritePlan
    {
        public required GeoJsonFeatureBaseBuilder.PropertyWritePlan Properties { get; init; }
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
        var schema = context.Schema ??
            new GeoJsonFeatureBaseBuilder.PreparedSchema(context.Resource, includeAdditionalAttributes: false);
        return new DirectFeatureWritePlan
        {
            Properties = OgcGeoJsonFeatureBuilder.PreparePropertyWritePlan(schema, context.ProjectedProperties),
            Resource = context.Resource,
            AxisOrder = context.AxisOrder,
            GeometryServices = context.GeometryServices
        };
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
        foreach (var field in plan.Properties.Fields)
        {
            if (plan.Properties.Read(feature, field) is { } property)
            {
                writer.WritePropertyName(field.JsonName);
                WriteDirectValue(writer, property.Value);
            }
        }

        if (plan.Properties.IdentifierFallback is { } fallback)
        {
            writer.WriteNumber(fallback.JsonName, feature.Id);
        }

        writer.WriteEndObject();
    }

    internal static void WriteDirectValue(Utf8JsonWriter writer, object? value)
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
            case double d:
                WriteFiniteOrNull(writer, d);
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
            case float f:
                WriteFiniteOrNull(writer, f);
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

    private static void WriteFiniteOrNull(Utf8JsonWriter writer, double value)
    {
        if (double.IsFinite(value))
        {
            writer.WriteNumberValue(value);
        }
        else
        {
            writer.WriteNullValue();
        }
    }

    private static void WriteFiniteOrNull(Utf8JsonWriter writer, float value)
    {
        if (float.IsFinite(value))
        {
            writer.WriteNumberValue(value);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
