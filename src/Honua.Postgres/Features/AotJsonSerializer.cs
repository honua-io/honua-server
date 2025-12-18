// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Honua.Postgres.Features;

/// <summary>
/// AOT-compatible JSON serializer for feature attributes.
/// Avoids reflection-based System.Text.Json for native AOT compatibility.
/// </summary>
internal static class AotJsonSerializer
{
    /// <summary>
    /// Manually serializes an ImmutableDictionary to JSON string for AOT compatibility.
    /// </summary>
    public static string SerializeAttributes(ImmutableDictionary<string, object?> attributes)
    {
        if (attributes.IsEmpty)
        {
            return "{}";
        }

        var json = new StringBuilder();
        json.Append('{');

        var first = true;
        foreach (var kvp in attributes)
        {
            if (!first)
            {
                json.Append(',');
            }

            json.Append('"');
            json.Append(EscapeJsonString(kvp.Key));
            json.Append("\":");
            json.Append(SerializeValue(kvp.Value));

            first = false;
        }

        json.Append('}');
        return json.ToString();
    }

    /// <summary>
    /// Manually deserializes JSON string to ImmutableDictionary for AOT compatibility.
    /// </summary>
    public static ImmutableDictionary<string, object?> DeserializeAttributes(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}")
        {
            return ImmutableDictionary<string, object?>.Empty;
        }

        var builder = ImmutableDictionary.CreateBuilder<string, object?>();
        var jsonSpan = json.AsSpan().Trim();

        if (jsonSpan.Length < 2 || jsonSpan[0] != '{' || jsonSpan[^1] != '}')
        {
            throw new ArgumentException("Invalid JSON object format");
        }

        // Remove outer braces
        jsonSpan = jsonSpan[1..^1].Trim();

        if (jsonSpan.IsEmpty)
        {
            return ImmutableDictionary<string, object?>.Empty;
        }

        ParseJsonObject(jsonSpan, builder);
        return builder.ToImmutable();
    }

    private static string SerializeValue(object? value)
    {
        return value switch
        {
            null => "null",
            string str => $"\"{EscapeJsonString(str)}\"",
            bool b => b ? "true" : "false",
            int i => i.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            double d => d.ToString("G17", CultureInfo.InvariantCulture),
            float f => f.ToString("G9", CultureInfo.InvariantCulture),
            decimal dec => dec.ToString(CultureInfo.InvariantCulture),
            _ => $"\"{EscapeJsonString(value.ToString() ?? "")}\""
        };
    }

    private static string EscapeJsonString(string str)
    {
        if (string.IsNullOrEmpty(str))
        {
            return str;
        }

        var sb = new StringBuilder(str.Length);
        foreach (var c in str)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    private static void ParseJsonObject(ReadOnlySpan<char> jsonSpan, ImmutableDictionary<string, object?>.Builder builder)
    {
        var pos = 0;

        while (pos < jsonSpan.Length)
        {
            // Skip whitespace
            while (pos < jsonSpan.Length && char.IsWhiteSpace(jsonSpan[pos]))
            {
                pos++;
            }

            if (pos >= jsonSpan.Length)
            {
                break;
            }

            // Parse key
            if (jsonSpan[pos] != '"')
            {
                throw new ArgumentException("Expected property name in quotes");
            }

            var keyStart = pos + 1;
            pos = keyStart;

            // Find end of key
            while (pos < jsonSpan.Length && jsonSpan[pos] != '"')
            {
                if (jsonSpan[pos] == '\\')
                {
                    pos++; // Skip escaped character
                }
                pos++;
            }

            if (pos >= jsonSpan.Length)
            {
                throw new ArgumentException("Unterminated string");
            }

            var key = UnescapeJsonString(jsonSpan[keyStart..pos].ToString());
            pos++; // Skip closing quote

            // Skip whitespace and find colon
            while (pos < jsonSpan.Length && char.IsWhiteSpace(jsonSpan[pos]))
            {
                pos++;
            }

            if (pos >= jsonSpan.Length || jsonSpan[pos] != ':')
            {
                throw new ArgumentException("Expected ':' after property name");
            }

            pos++; // Skip colon

            // Skip whitespace
            while (pos < jsonSpan.Length && char.IsWhiteSpace(jsonSpan[pos]))
            {
                pos++;
            }

            // Parse value
            var (value, newPos) = ParseJsonValue(jsonSpan, pos);
            builder[key] = value;
            pos = newPos;

            // Skip whitespace
            while (pos < jsonSpan.Length && char.IsWhiteSpace(jsonSpan[pos]))
            {
                pos++;
            }

            // Check for comma
            if (pos < jsonSpan.Length && jsonSpan[pos] == ',')
            {
                pos++;
            }
        }
    }

    private static (object?, int) ParseJsonValue(ReadOnlySpan<char> jsonSpan, int startPos)
    {
        var pos = startPos;

        // Skip whitespace
        while (pos < jsonSpan.Length && char.IsWhiteSpace(jsonSpan[pos]))
        {
            pos++;
        }

        if (pos >= jsonSpan.Length)
        {
            throw new ArgumentException("Unexpected end of JSON");
        }

        var c = jsonSpan[pos];

        if (c == '"')
        {
            // String value
            var stringStart = pos + 1;
            pos = stringStart;

            while (pos < jsonSpan.Length && jsonSpan[pos] != '"')
            {
                if (jsonSpan[pos] == '\\')
                {
                    pos++; // Skip escaped character
                }
                pos++;
            }

            if (pos >= jsonSpan.Length)
            {
                throw new ArgumentException("Unterminated string");
            }

            var stringValue = UnescapeJsonString(jsonSpan[stringStart..pos].ToString());
            return (stringValue, pos + 1);
        }

        if (c == 'n' && jsonSpan[pos..].StartsWith("null"))
        {
            return (null, pos + 4);
        }

        if (c == 't' && jsonSpan[pos..].StartsWith("true"))
        {
            return (true, pos + 4);
        }

        if (c == 'f' && jsonSpan[pos..].StartsWith("false"))
        {
            return (false, pos + 5);
        }

        if (char.IsDigit(c) || c == '-')
        {
            // Number value
            var numberStart = pos;
            if (c == '-')
            {
                pos++;
            }

            while (pos < jsonSpan.Length && (char.IsDigit(jsonSpan[pos]) || jsonSpan[pos] == '.'))
            {
                pos++;
            }

            var numberStr = jsonSpan[numberStart..pos].ToString();

            if (numberStr.Contains('.'))
            {
                return (double.Parse(numberStr, CultureInfo.InvariantCulture), pos);
            }
            else
            {
                return (long.Parse(numberStr, CultureInfo.InvariantCulture), pos);
            }
        }

        throw new ArgumentException($"Unexpected character '{c}' in JSON");
    }

    private static string UnescapeJsonString(string str)
    {
        if (!str.Contains('\\'))
        {
            return str;
        }

        var sb = new StringBuilder(str.Length);
        for (var i = 0; i < str.Length; i++)
        {
            if (str[i] == '\\' && i + 1 < str.Length)
            {
                switch (str[i + 1])
                {
                    case '"':
                        sb.Append('"');
                        i++;
                        break;
                    case '\\':
                        sb.Append('\\');
                        i++;
                        break;
                    case 'b':
                        sb.Append('\b');
                        i++;
                        break;
                    case 'f':
                        sb.Append('\f');
                        i++;
                        break;
                    case 'n':
                        sb.Append('\n');
                        i++;
                        break;
                    case 'r':
                        sb.Append('\r');
                        i++;
                        break;
                    case 't':
                        sb.Append('\t');
                        i++;
                        break;
                    default:
                        sb.Append(str[i]);
                        break;
                }
            }
            else
            {
                sb.Append(str[i]);
            }
        }
        return sb.ToString();
    }
}
