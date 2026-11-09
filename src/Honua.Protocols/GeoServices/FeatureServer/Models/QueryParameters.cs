// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

public sealed class QueryParameters
{
    public string? Where { get; init; }

    public string? OutFields { get; init; }

    public string? OrderByFields { get; init; }

    public bool ReturnGeometry { get; init; } = true;

    public bool ReturnIdsOnly { get; init; }

    public bool ReturnCountOnly { get; init; }

    public bool ReturnExtentOnly { get; init; }

    public string F { get; init; } = "json";

    [JsonIgnore]
    public bool FormatSpecified { get; init; }

    public int? ResultOffset { get; init; }

    public int? ResultRecordCount { get; init; }

    [JsonConverter(typeof(RawJsonStringConverter))]
    public string? Geometry { get; init; }

    [JsonPropertyName("inSR")]
    [JsonConverter(typeof(RawJsonStringConverter))]
    public string? InSr { get; init; }

    [JsonIgnore]
    public bool InSrSpecified { get; init; }

    [JsonPropertyName("outSR")]
    [JsonConverter(typeof(RawJsonStringConverter))]
    public string? OutSr { get; init; }

    [JsonIgnore]
    public bool OutSrSpecified { get; init; }

    public string? GeometryType { get; init; }

    public string? SpatialRel { get; init; }

    public double? Distance { get; init; }

    public string? Units { get; init; }

    public int? NearestCount { get; init; }

    public bool ReturnDistance { get; init; }

    public bool ReturnCentroid { get; init; }

    public bool ReturnDistinctValues { get; init; }

    public bool ReturnZ { get; init; }

    public bool ReturnM { get; init; }

    public bool ReturnTrueCurves { get; init; }

    public bool ReturnExceededLimitFeatures { get; init; }

    public long[]? ObjectIds { get; init; }

    public int? GeometryPrecision { get; init; }

    public double? MaxAllowableOffset { get; init; }

    public string? ResultType { get; init; }

    public string? OutStatistics { get; init; }

    public string? GroupByFieldsForStatistics { get; init; }

    public string? Having { get; init; }

    public string? SqlFormat { get; init; }

    public string? GdbVersion { get; init; }

    public string? QuantizationParameters { get; init; }

    public string? DatumTransformation { get; init; }

    public string? Time { get; init; }

    public string? TimeRelation { get; init; }
}

internal sealed class RawJsonStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.Number:
                return reader.TryGetInt64(out var longValue)
                    ? longValue.ToString(CultureInfo.InvariantCulture)
                    : reader.GetDouble().ToString(CultureInfo.InvariantCulture);
            case JsonTokenType.StartObject:
            case JsonTokenType.StartArray:
                {
                    using var document = JsonDocument.ParseValue(ref reader);
                    return document.RootElement.GetRawText();
                }
            case JsonTokenType.Null:
                return null;
            default:
                throw new JsonException("Unsupported JSON token for string conversion.");
        }
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        if (value.StartsWith('{') || value.StartsWith('['))
        {
            using var document = JsonDocument.Parse(value);
            document.RootElement.WriteTo(writer);
            return;
        }

        writer.WriteStringValue(value);
    }
}
