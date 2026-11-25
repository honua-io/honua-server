// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Honua.Infrastructure.Helpers;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

internal sealed class GeoServicesFeatureJsonConverter : JsonConverter<GeoServicesFeature>
{
    private static readonly JsonEncodedText AttributesPropertyName = JsonEncodedText.Encode("attributes");
    private static readonly JsonEncodedText GeometryPropertyName = JsonEncodedText.Encode("geometry");
    private static readonly JsonEncodedText CentroidPropertyName = JsonEncodedText.Encode("centroid");

    private static readonly JsonTypeInfo _attributesTypeInfo =
        FeatureServerJsonContext.Default.GetTypeInfo(typeof(Dictionary<string, object?>))
        ?? throw new InvalidOperationException("FeatureServerJsonContext is missing Dictionary<string, object?> metadata.");

    private static readonly JsonTypeInfo _geometryTypeInfo =
        FeatureServerJsonContext.Default.GetTypeInfo(typeof(GeoServicesGeometry))
        ?? throw new InvalidOperationException("FeatureServerJsonContext is missing GeoServicesGeometry metadata.");

    private static readonly JsonTypeInfo<object> _objectTypeInfo =
        FeatureServerJsonContext.Default.Object;

    public override GeoServicesFeature Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        Dictionary<string, object?> attributes = new(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("attributes", out var attributesElement))
        {
            attributes = JsonSerializer.Deserialize(attributesElement, _attributesTypeInfo) as Dictionary<string, object?>
                ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        }

        GeoServicesGeometry? geometry = null;
        GeoServicesGeometry? centroid = null;
        var includeGeometry = false;
        if (root.TryGetProperty("geometry", out var geometryElement))
        {
            includeGeometry = true;
            if (geometryElement.ValueKind != JsonValueKind.Null)
            {
                geometry = JsonSerializer.Deserialize(geometryElement, _geometryTypeInfo) as GeoServicesGeometry;
            }
        }

        if (root.TryGetProperty("centroid", out var centroidElement) &&
            centroidElement.ValueKind != JsonValueKind.Null)
        {
            centroid = JsonSerializer.Deserialize(centroidElement, _geometryTypeInfo) as GeoServicesGeometry;
        }

        return new GeoServicesFeature
        {
            Attributes = attributes,
            Geometry = geometry,
            Centroid = centroid,
            IncludeGeometry = includeGeometry
        };
    }

    public override void Write(Utf8JsonWriter writer, GeoServicesFeature value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        writer.WriteStartObject(AttributesPropertyName);
        if (value.Attributes is not null)
        {
            foreach (var kvp in value.Attributes)
            {
                WriteNormalizedAttribute(writer, kvp.Key, FeatureAttributeValueNormalizer.Normalize(kvp.Value));
            }
        }
        writer.WriteEndObject();

        if (value.IncludeGeometry)
        {
            writer.WritePropertyName(GeometryPropertyName);
            if (value.Geometry == null)
            {
                writer.WriteNullValue();
            }
            else
            {
                JsonSerializer.Serialize(writer, value.Geometry, _geometryTypeInfo);
            }
        }

        if (value.Centroid is not null)
        {
            writer.WritePropertyName(CentroidPropertyName);
            JsonSerializer.Serialize(writer, value.Centroid, _geometryTypeInfo);
        }

        writer.WriteEndObject();
    }

    private static void WriteNormalizedAttribute(Utf8JsonWriter writer, string name, object? value)
    {
        if (TryWriteNumericAttribute(writer, name, value))
        {
            return;
        }

        switch (value)
        {
            case null:
                writer.WriteNull(name);
                break;
            case string s:
                writer.WriteString(name, s);
                break;
            case bool b:
                writer.WriteBoolean(name, b);
                break;
            case Guid g:
                writer.WriteString(name, g);
                break;
            case DateTime dt:
                writer.WriteString(name, dt);
                break;
            case DateTimeOffset dto:
                writer.WriteString(name, dto);
                break;
            default:
                writer.WritePropertyName(name);
                JsonSerializer.Serialize(writer, value, _objectTypeInfo);
                break;
        }
    }

    private static bool TryWriteNumericAttribute(Utf8JsonWriter writer, string name, object? value)
    {
        switch (value)
        {
            case int i: writer.WriteNumber(name, i); return true;
            case long l: writer.WriteNumber(name, l); return true;
            case double d: writer.WriteNumber(name, d); return true;
            case float f: writer.WriteNumber(name, f); return true;
            case decimal dec: writer.WriteNumber(name, dec); return true;
            case short sh: writer.WriteNumber(name, (int)sh); return true;
            case ushort ush: writer.WriteNumber(name, (uint)ush); return true;
            case uint ui: writer.WriteNumber(name, ui); return true;
            case ulong ul: writer.WriteNumber(name, ul); return true;
            case byte b: writer.WriteNumber(name, (int)b); return true;
            case sbyte sb: writer.WriteNumber(name, (int)sb); return true;
            default: return false;
        }
    }
}
