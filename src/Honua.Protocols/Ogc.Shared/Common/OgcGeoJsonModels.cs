// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Shared.Models;

namespace Honua.Protocols.Ogc.Common;

internal static class OgcTimestampFormatter
{
    private const string TimeStampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    internal static string Format(DateTimeOffset value)
        => value.ToUniversalTime().ToString(TimeStampFormat, CultureInfo.InvariantCulture);
}

internal sealed class OgcTimestampJsonConverter : JsonConverter<DateTimeOffset?>
{
    public override DateTimeOffset? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? null : reader.GetDateTimeOffset();

    public override void Write(
        Utf8JsonWriter writer,
        DateTimeOffset? value,
        JsonSerializerOptions options)
    {
        if (value.HasValue)
        {
            writer.WriteStringValue(OgcTimestampFormatter.Format(value.Value));
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

public sealed class RawJsonStringConverter : JsonConverter<string?>
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
        }
        else
        {
            // Write the string as raw JSON without quotes
            writer.WriteRawValue(value);
        }
    }
}

public sealed record SimpleGeoJsonGeometry
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("coordinates")]
    [JsonConverter(typeof(RawJsonStringConverter))]
    public string? CoordinatesJson { get; init; }

    [JsonPropertyName("geometries")]
    [JsonConverter(typeof(RawJsonStringConverter))]
    public string? GeometriesJson { get; init; }
}

public sealed class FeatureIdConverter : JsonConverter<object?>
{
    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Number when reader.TryGetInt64(out var longValue) => longValue,
            JsonTokenType.Number => reader.GetDouble(),
            JsonTokenType.String => ReadStringId(ref reader),
            JsonTokenType.Null => null,
            _ => null
        };
    }

    public override void Write(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    private static string? ReadStringId(ref Utf8JsonReader reader)
    {
        var str = reader.GetString();
        if (str == null)
        {
            return null;
        }

        return str;
    }
}

public sealed record GeoJsonFeature
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "Feature";

    [JsonPropertyName("id")]
    [JsonConverter(typeof(FeatureIdConverter))]
    public object? Id { get; init; }

    [JsonPropertyName("geometry")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public SimpleGeoJsonGeometry? Geometry { get; init; }

    [JsonPropertyName("properties")]
    public Dictionary<string, object?> Properties { get; init; } = new();

    [JsonPropertyName("links")]
    public ImmutableArray<Link>? Links { get; init; }
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "FeatureCollection is the standard GeoJSON type name")]
public sealed record FeatureCollection : ICollectionResponse<GeoJsonFeature>
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "FeatureCollection";

    [JsonPropertyName("features")]
    public required GeoJsonFeature[] Features { get; init; }

    [JsonPropertyName("numberMatched")]
    public long? NumberMatched { get; init; }

    [JsonPropertyName("numberReturned")]
    public int NumberReturned { get; init; }

    [JsonPropertyName("links")]
    public ImmutableArray<Link>? Links { get; init; }

    [JsonPropertyName("timeStamp")]
    [JsonConverter(typeof(OgcTimestampJsonConverter))]
    public DateTimeOffset? TimeStamp { get; init; }

    // ICollectionResponse implementation
    ImmutableArray<GeoJsonFeature> ICollectionResponse<GeoJsonFeature>.Items => Features.ToImmutableArray();
    ImmutableArray<ILink>? ICollectionResponse<GeoJsonFeature>.Links => Links?.Cast<ILink>().ToImmutableArray();
    IPaginationMetadata? ICollectionResponse<GeoJsonFeature>.Pagination =>
        NumberMatched.HasValue || NumberReturned > 0
            ? PaginationMetadata.Create(NumberMatched, NumberReturned, false)
            : null;
}
