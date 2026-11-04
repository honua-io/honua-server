// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Honua.Core.Features.Shared.Services;

// JSON.stringify of the value JSON.parse yields (ECMA-262 25.5.2), for limits that JavaScript clients enforce on that
// text. JavaScript lists integer-like keys first; this keeps document order, which never changes the text's length.
public static class EcmaScriptJsonSerializer
{
    public static string Stringify(JsonNode? node)
    {
        var text = new StringBuilder();
        Append(text, node);
        return text.ToString();
    }

    private static void Append(StringBuilder text, JsonNode? node)
    {
        switch (node)
        {
            case null:
                text.Append("null");
                break;
            case JsonObject members:
                AppendObject(text, members);
                break;
            case JsonArray items:
                AppendArray(text, items);
                break;
            default:
                AppendValue(text, node.AsValue());
                break;
        }
    }

    private static void AppendObject(StringBuilder text, JsonObject members)
    {
        text.Append('{');
        var separator = string.Empty;
        foreach (var (name, value) in members)
        {
            text.Append(separator);
            AppendString(text, name);
            text.Append(':');
            Append(text, value);
            separator = ",";
        }

        text.Append('}');
    }

    private static void AppendArray(StringBuilder text, JsonArray items)
    {
        text.Append('[');
        for (var index = 0; index < items.Count; index++)
        {
            if (index > 0)
            {
                text.Append(',');
            }

            Append(text, items[index]);
        }

        text.Append(']');
    }

    private static void AppendValue(StringBuilder text, JsonValue value)
    {
        switch (value.GetValueKind())
        {
            case JsonValueKind.String:
                AppendString(text, value.GetValue<string>());
                break;
            case JsonValueKind.Number:
                text.Append(value.TryGetValue<double>(out var number) && double.IsFinite(number)
                    ? EcmaScriptNumberFormatter.Format(number)
                    : "null");
                break;
            case JsonValueKind.True:
                text.Append("true");
                break;
            case JsonValueKind.False:
                text.Append("false");
                break;
            default:
                text.Append("null");
                break;
        }
    }

    private static void AppendString(StringBuilder text, string value)
    {
        text.Append('"');
        for (var index = 0; index < value.Length; index++)
        {
            if (StartsSurrogatePair(value, index))
            {
                text.Append(value, index++, 2);
                continue;
            }

            var character = value[index];
            if (EscapeOf(character) is { } escape)
            {
                text.Append(escape);
            }
            else
            {
                text.Append(character);
            }
        }

        text.Append('"');
    }

    private static bool StartsSurrogatePair(string value, int index) =>
        char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]);

    private static string? EscapeOf(char character) => character switch
    {
        '"' => "\\\"",
        '\\' => "\\\\",
        '\b' => "\\b",
        '\f' => "\\f",
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        < ' ' or (>= '\uD800' and <= '\uDFFF') => "\\u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture),
        _ => null,
    };
}
