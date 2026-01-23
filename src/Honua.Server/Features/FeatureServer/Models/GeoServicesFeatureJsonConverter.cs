// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.FeatureServer.Models;

internal sealed class GeoServicesFeatureJsonConverter : JsonConverter<GeoServicesFeature>
{
    public override GeoServicesFeature Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        Dictionary<string, object?> attributes = new(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("attributes", out var attributesElement))
        {
            attributes = attributesElement.Deserialize<Dictionary<string, object?>>(options)
                ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        }

        GeoServicesGeometry? geometry = null;
        var includeGeometry = false;
        if (root.TryGetProperty("geometry", out var geometryElement))
        {
            includeGeometry = true;
            if (geometryElement.ValueKind != JsonValueKind.Null)
            {
                geometry = geometryElement.Deserialize<GeoServicesGeometry>(options);
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
        JsonSerializer.Serialize(writer, value.Attributes, options);

        if (value.IncludeGeometry)
        {
            writer.WritePropertyName("geometry");
            if (value.Geometry == null)
            {
                writer.WriteNullValue();
            }
            else
            {
                JsonSerializer.Serialize(writer, value.Geometry, options);
            }
        }

        writer.WriteEndObject();
    }
}
