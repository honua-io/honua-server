// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.Geocoding;

/// <summary>
/// One value in a geocode candidate's attribute bag, carrying whether it is textual or
/// numeric so it serializes as the JSON type <c>candidateFields</c> declares.
/// </summary>
/// <remarks>
/// A locator that declares <c>Score</c> as <c>esriFieldTypeDouble</c> and then answers
/// <c>"100"</c> disagrees with itself, and the geocoding tools build a typed output column
/// from that declaration. The live ArcGIS World Geocoding Service returns <c>Score</c> and
/// <c>Rank</c> as JSON numbers while every address component stays a string, so the
/// attribute bag cannot be uniformly typed. This is the same defect class as a query
/// response disagreeing with its layer resource (honua-server#5197).
/// </remarks>
[JsonConverter(typeof(GeocodeAttributeValueConverter))]
internal readonly record struct GeocodeAttributeValue
{
    private GeocodeAttributeValue(string? text, double? number)
    {
        Text = text;
        Number = number;
    }

    public string? Text { get; }

    public double? Number { get; }

    public static GeocodeAttributeValue FromText(string? value) => new(value, null);

    public static GeocodeAttributeValue FromNumber(double value) => new(null, value);

    public override string ToString() =>
        Number?.ToString("0.##", CultureInfo.InvariantCulture) ?? Text ?? string.Empty;
}

internal sealed class GeocodeAttributeValueConverter : JsonConverter<GeocodeAttributeValue>
{
    public override GeocodeAttributeValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.Number => GeocodeAttributeValue.FromNumber(reader.GetDouble()),
            JsonTokenType.Null => GeocodeAttributeValue.FromText(null),
            _ => GeocodeAttributeValue.FromText(reader.GetString()),
        };

    public override void Write(Utf8JsonWriter writer, GeocodeAttributeValue value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (value.Number is { } number)
        {
            writer.WriteNumberValue(number);
            return;
        }

        if (value.Text is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(value.Text);
    }
}
