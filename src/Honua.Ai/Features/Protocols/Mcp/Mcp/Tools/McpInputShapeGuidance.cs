// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Tools;

internal sealed class McpInputShapeGuidance
{
    private sealed record InputPath(string Value, string? Source, string? Property = null);
    private const int MaxViolations = 20;
    private const int MaxArrayItems = 20;
    private readonly List<McpValidationViolation> _violations = [];
    private int _remainingNodes = 256;
    private bool Complete => _remainingNodes <= 0 || _violations.Count >= MaxViolations;

    internal static IReadOnlyList<McpValidationViolation> Find(JsonElement schema, JsonElement? arguments)
    {
        var guidance = new McpInputShapeGuidance();
        guidance.Inspect(schema, arguments, new InputPath("$", null));
        return guidance._violations;
    }

    private void Inspect(JsonElement schema, JsonElement? value, InputPath context)
    {
        if (Complete || schema.ValueKind != JsonValueKind.Object) return;
        _remainingNodes--;
        var description = ReadDescription(schema);
        if (description.Contains("honua_", StringComparison.Ordinal) || description.Contains("caller", StringComparison.OrdinalIgnoreCase))
            context = context with { Source = description };
        if (value is { } present && !MatchesType(schema, present))
        {
            AddViolation(schema, context, "Input has an invalid type");
            return;
        }
        if (value is null || value.Value.ValueKind == JsonValueKind.Object) InspectObject(schema, value, context);
        if (value is { ValueKind: JsonValueKind.Array } array && schema.TryGetProperty("items", out var items))
            InspectArray(items, array, new ArrayPath(context, ReadArrayLimit(schema)));
    }

    private sealed record ArrayPath(InputPath Context, int Limit);

    private static int ReadArrayLimit(JsonElement schema) =>
        schema.TryGetProperty("maxItems", out var maximum) && maximum.ValueKind == JsonValueKind.Number && maximum.TryGetInt32(out var count)
            ? Math.Clamp(count, 0, MaxArrayItems) : MaxArrayItems;

    private void InspectArray(JsonElement items, JsonElement array, ArrayPath path)
    {
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (Complete || index >= path.Limit) break;
            Inspect(items, item, path.Context with { Value = $"{path.Context.Value}[{index++}]" });
        }
    }

    private void InspectObject(JsonElement schema, JsonElement? value, InputPath context)
    {
        if (!schema.TryGetProperty("properties", out var properties)) return;
        if (schema.TryGetProperty("required", out var required))
            foreach (var field in required.EnumerateArray())
            {
                if (Complete) break;
                _remainingNodes--;
                InspectRequired(properties, value, context with { Value = $"{context.Value}.{field.GetString()}", Property = field.GetString() });
            }
        if (value is not { ValueKind: JsonValueKind.Object } supplied) return;
        foreach (var property in properties.EnumerateObject())
        {
            if (Complete) break;
            _remainingNodes--;
            if (supplied.TryGetProperty(property.Name, out var argument))
                Inspect(property.Value, argument, context with { Value = $"{context.Value}.{property.Name}" });
        }
    }

    private void InspectRequired(JsonElement properties, JsonElement? value, InputPath context)
    {
        var name = context.Property!;
        if (value is { ValueKind: JsonValueKind.Object } supplied && supplied.TryGetProperty(name, out _)) return;
        if (properties.TryGetProperty(name, out var schema)) AddViolation(schema, context, "Required input is missing");
    }

    private void AddViolation(JsonElement schema, InputPath context, string problem)
    {
        if (_violations.Count >= MaxViolations) return;
        var description = ReadDescription(schema);
        var source = context.Source is not null && context.Source != description ? $" Source: {context.Source}" : string.Empty;
        _violations.Add(new McpValidationViolation
        {
            Code = "INVALID_ARGUMENT",
            FieldPath = context.Value,
            Message = $"{problem}: {context.Value}. {description}{source}"
        });
    }

    private static bool MatchesType(JsonElement schema, JsonElement value)
    {
        if (!schema.TryGetProperty("type", out var type)) return true;
        return type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Any(candidate => MatchesTypeName(candidate.GetString(), value))
            : MatchesTypeName(type.GetString(), value);
    }

    private static bool MatchesTypeName(string? type, JsonElement value) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true
    };

    internal static string ReadDescription(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String
            ? description.GetString()! : string.Empty;
}
