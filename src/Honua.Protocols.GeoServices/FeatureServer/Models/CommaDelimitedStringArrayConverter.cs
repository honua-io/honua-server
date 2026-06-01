// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// Serializes a <see cref="string"/> array as a single comma-delimited string,
/// matching the GeoServices REST contract for fields the Esri spec defines as a
/// comma-separated list (notably <c>supportedQueryFormats</c>).
/// </summary>
/// <remarks>
/// The ArcGIS Maps SDK for JavaScript parses <c>supportedQueryFormats</c> by
/// calling <c>value.split(",")</c>; when Honua emitted it as a JSON array the
/// SDK threw <c>"split is not a function"</c> and <c>FeatureLayer.load()</c>
/// failed, so the JS SDK could not load any FeatureServer layer. (The ArcGIS API
/// for Python and ArcGIS Maps SDK for .NET tolerated the array, so this was a
/// JS-SDK-only break.) Writing the spec-compliant comma-delimited string fixes
/// it. Reading is lenient — it accepts either a comma-delimited string or a JSON
/// string array — so the internal <c>string[]</c> contract and existing
/// deserialization round-trips are preserved.
/// </remarks>
public sealed class CommaDelimitedStringArrayConverter : JsonConverter<string[]>
{
    public override string[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return [];
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var raw = reader.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return [];
            }

            return raw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var values = new List<string>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.String)
                {
                    var item = reader.GetString();
                    if (!string.IsNullOrWhiteSpace(item))
                    {
                        values.Add(item);
                    }
                }
            }

            return [.. values];
        }

        throw new JsonException(
            $"Unexpected token {reader.TokenType} when reading a comma-delimited string array.");
    }

    public override void Write(Utf8JsonWriter writer, string[] value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value is { Length: > 0 } ? string.Join(", ", value) : string.Empty);
    }
}
