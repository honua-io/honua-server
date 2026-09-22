// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// Esri JSON <c>points</c> (a list of vertices) with positional Z/M slots. A missing ordinate is NaN in
/// memory and <c>null</c> on the wire, so a ZM vertex without a Z keeps its slot and its M is not read
/// back as the elevation (#4027).
/// </summary>
internal sealed class EsriVertexListJsonConverter : JsonConverter<double[][]>
{
    public override double[][]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => EsriVertexJson.ReadVertexList(ref reader, options);

    public override void Write(Utf8JsonWriter writer, double[][] value, JsonSerializerOptions options)
        => EsriVertexJson.WriteVertexList(writer, value);
}

/// <summary>
/// Esri JSON <c>paths</c> and <c>rings</c> (lists of vertex lists) with positional Z/M slots; see
/// <see cref="EsriVertexListJsonConverter"/>.
/// </summary>
internal sealed class EsriVertexListsJsonConverter : JsonConverter<double[][][]>
{
    public override double[][][]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        EsriVertexJson.ExpectStartArray(ref reader);
        var lists = new List<double[][]>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            lists.Add(EsriVertexJson.ReadVertexList(ref reader, options)
                ?? throw new JsonException("A path or ring cannot be null."));
        }

        return [.. lists];
    }

    public override void Write(Utf8JsonWriter writer, double[][][] value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var list in value)
        {
            EsriVertexJson.WriteVertexList(writer, list);
        }

        writer.WriteEndArray();
    }
}

internal static class EsriVertexJson
{
    public static void ExpectStartArray(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected a JSON array of coordinates.");
        }
    }

    public static double[][]? ReadVertexList(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        ExpectStartArray(ref reader);
        var vertices = new List<double[]>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            vertices.Add(ReadVertex(ref reader, options));
        }

        return [.. vertices];
    }

    private static double[] ReadVertex(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        ExpectStartArray(ref reader);
        var ordinates = new List<double>(4);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            ordinates.Add(reader.TokenType switch
            {
                JsonTokenType.Number => reader.GetDouble(),
                // Only a Z or M slot may be missing; x and y are required.
                JsonTokenType.Null when ordinates.Count >= 2 => double.NaN,
                JsonTokenType.String when ordinates.Count >= 2 && reader.ValueTextEquals("NaN") => double.NaN,
                JsonTokenType.String when (options.NumberHandling & JsonNumberHandling.AllowReadingFromString) != 0
                    && double.TryParse(reader.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => throw new JsonException("Coordinate ordinates must be numbers; only a Z or M slot may be null.")
            });
        }

        return [.. ordinates];
    }

    public static void WriteVertexList(Utf8JsonWriter writer, double[][] vertices)
    {
        writer.WriteStartArray();
        foreach (var vertex in vertices)
        {
            writer.WriteStartArray();
            foreach (var ordinate in vertex)
            {
                if (double.IsNaN(ordinate))
                {
                    writer.WriteNullValue();
                }
                else
                {
                    writer.WriteNumberValue(ordinate);
                }
            }

            writer.WriteEndArray();
        }

        writer.WriteEndArray();
    }
}
