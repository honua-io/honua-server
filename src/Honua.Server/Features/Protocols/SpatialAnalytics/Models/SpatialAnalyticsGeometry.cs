// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.Protocols.SpatialAnalytics.Models;

/// <summary>
/// Simple GeoJSON geometry representation for the spatial analytics slice.
/// Mirrors the OGC Features <c>SimpleGeoJsonGeometry</c> shape but lives inside
/// the SpatialAnalytics feature so the slice does not take a hard dependency
/// on the OgcFeatures models (vertical slice isolation).
/// </summary>
internal sealed record SpatialAnalyticsGeometry
{
    /// <summary>
    /// Geometry type (Point, LineString, Polygon, MultiPolygon, etc.).
    /// </summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>
    /// Coordinates as raw JSON so the analytics pipeline can pass arbitrary
    /// coordinate arrays through to clients without parsing them through a
    /// reflection-heavy converter (AOT-friendly).
    /// </summary>
    [JsonPropertyName("coordinates")]
    [JsonConverter(typeof(SpatialAnalyticsRawJsonConverter))]
    public string? CoordinatesJson { get; init; }
}

/// <summary>
/// Writes a pre-serialized JSON string verbatim and reads any JSON value back
/// out as its raw text. Used by <see cref="SpatialAnalyticsGeometry"/> so the
/// PostGIS-emitted GeoJSON coordinate array can flow through System.Text.Json
/// untouched.
/// </summary>
internal sealed class SpatialAnalyticsRawJsonConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.GetRawText();
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteRawValue(value);
    }
}
