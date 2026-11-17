// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Honua.Infrastructure.Security;

// Masks the content of CQL2-JSON string literals before SQL-injection inspection, matching
// the masking already applied to quoted literals in CQL2-text filters. Literal values are
// bound as parameters by the filter compiler, so "--" or "/* */" inside a JSON string
// literal is data. Operators, property names, unknown members and malformed or trailing
// content are written out decoded and stay visible to the injection pattern.
internal static class Cql2JsonLiteralMask
{
    private const string JsonFilterLanguage = "cql2-json";

    private static readonly JsonWriterOptions _writerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly HashSet<string> _scalarLiteralMembers = new(StringComparer.Ordinal)
    {
        "value",
        "date",
        "timestamp"
    };

    public static bool AppliesTo(HttpRequest request, string paramType, string name)
        => paramType.Equals("query", StringComparison.Ordinal)
           && name.Equals("filter", StringComparison.OrdinalIgnoreCase)
           && IsExplicitJsonLanguage(request)
           && IsSupportedFilterRoute(request.Path.Value);

    public static string Mask(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, _writerOptions))
            {
                WriteExpression(writer, document.RootElement);
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (JsonException)
        {
            return value;
        }
    }

    private static bool IsExplicitJsonLanguage(HttpRequest request)
    {
        var languages = request.Query["filter-lang"];
        return languages.Count == 1
               && string.Equals(languages[0], JsonFilterLanguage, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupportedFilterRoute(string? path)
    {
        var segments = (path ?? string.Empty).Trim('/').Split('/');
        return segments.Length switch
        {
            2 => IsSegment(segments[0], "stac") && IsSegment(segments[1], "search"),
            5 => IsSegment(segments[0], "ogc")
                 && IsSegment(segments[1], "features")
                 && IsSegment(segments[2], "collections")
                 && segments[3].Length > 0
                 && IsSegment(segments[4], "items"),
            _ => false
        };
    }

    private static bool IsSegment(string segment, string expected)
        => segment.Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static void WriteExpression(Utf8JsonWriter writer, JsonElement element)
    {
        if (!IsOperatorExpression(element))
        {
            WriteDecoded(writer, element);
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("op", element.GetProperty("op").GetString());
        writer.WriteStartArray("args");
        foreach (var argument in element.GetProperty("args").EnumerateArray())
        {
            WriteArgument(writer, argument);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static bool IsOperatorExpression(JsonElement element)
        => element.ValueKind == JsonValueKind.Object
           && element.EnumerateObject().Count() == 2
           && element.TryGetProperty("op", out var op) && op.ValueKind == JsonValueKind.String
           && element.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array;

    private static void WriteArgument(Utf8JsonWriter writer, JsonElement argument)
    {
        switch (argument.ValueKind)
        {
            case JsonValueKind.String:
                writer.WriteStringValue(string.Empty);
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in argument.EnumerateArray())
                {
                    WriteArgument(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.Object when IsOperatorExpression(argument):
                WriteExpression(writer, argument);
                break;
            case JsonValueKind.Object when ScalarLiteralMember(argument) is { } member:
                writer.WriteStartObject();
                writer.WriteString(member, string.Empty);
                writer.WriteEndObject();
                break;
            default:
                WriteDecoded(writer, argument);
                break;
        }
    }

    private static string? ScalarLiteralMember(JsonElement element)
    {
        var properties = element.EnumerateObject().ToList();
        return properties is [{ Value.ValueKind: JsonValueKind.String } only]
               && _scalarLiteralMembers.Contains(only.Name)
            ? only.Name
            : null;
    }

    // Copies an element with every string and member name decoded, so escaped sequences
    // such as "--" are inspected as the characters they represent.
    private static void WriteDecoded(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteDecoded(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteDecoded(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
