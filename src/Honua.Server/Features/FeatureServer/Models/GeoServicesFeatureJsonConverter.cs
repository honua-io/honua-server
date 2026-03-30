// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Honua.Server.Features.FeatureServer.Models;

internal sealed class GeoServicesFeatureJsonConverter : JsonConverter<GeoServicesFeature>
{
    private static readonly JsonTypeInfo _attributesTypeInfo =
        FeatureServerJsonContext.Default.GetTypeInfo(typeof(Dictionary<string, object?>))
        ?? throw new InvalidOperationException("FeatureServerJsonContext is missing Dictionary<string, object?> metadata.");

    private static readonly JsonTypeInfo _geometryTypeInfo =
        FeatureServerJsonContext.Default.GetTypeInfo(typeof(GeoServicesGeometry))
        ?? throw new InvalidOperationException("FeatureServerJsonContext is missing GeoServicesGeometry metadata.");

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
        var includeGeometry = false;
        if (root.TryGetProperty("geometry", out var geometryElement))
        {
            includeGeometry = true;
            if (geometryElement.ValueKind != JsonValueKind.Null)
            {
                geometry = JsonSerializer.Deserialize(geometryElement, _geometryTypeInfo) as GeoServicesGeometry;
            }
        }

        return new GeoServicesFeature
        {
            Attributes = attributes,
            Geometry = geometry,
            IncludeGeometry = includeGeometry
        };
    }

    public override void Write(Utf8JsonWriter writer, GeoServicesFeature value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        writer.WritePropertyName("attributes");
        JsonSerializer.Serialize(
            writer,
            GeoServicesValueNormalizer.NormalizeAttributes(value.Attributes),
            _attributesTypeInfo);

        if (value.IncludeGeometry)
        {
            writer.WritePropertyName("geometry");
            if (value.Geometry == null)
            {
                writer.WriteNullValue();
            }
            else
            {
                JsonSerializer.Serialize(writer, value.Geometry, _geometryTypeInfo);
            }
        }

        writer.WriteEndObject();
    }
}
