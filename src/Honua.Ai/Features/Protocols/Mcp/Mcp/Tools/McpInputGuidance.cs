// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Views;

namespace Honua.Ai.Protocols.Mcp.Tools;

internal static class McpInputGuidance
{
    private sealed record Guidance(IReadOnlyList<McpValidationViolation> Violations, string Sources);

    internal static McpToolsCallResult Enrich(IMcpTool tool, JsonElement? arguments, McpToolsCallResult result)
    {
        if (!result.IsError || result.StructuredContent is not { ValueKind: JsonValueKind.Object } payload
            || !payload.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String || code.GetString() != McpErrorMapper.Codes.InvalidArgument)
            return result;
        var text = result.Content.Where(block => block.Type == "text").ToArray();
        if (text.Length != 1) return result;
        try
        {
            var schema = McpWorkflowViewDescriptorClassifier.Describe(tool).InputSchema;
            var guidance = new Guidance(McpInputShapeGuidance.Find(schema, arguments), InputSources(schema));
            var enriched = WritePayload(payload, guidance);
            var enrichedText = enriched.GetRawText();
            result.StructuredContent = enriched;
            text[0].Text = enrichedText;
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException or ArgumentException)
        {
            // Optional guidance must preserve the original refusal when a descriptor is malformed.
        }
        return result;
    }

    private static string InputSources(JsonElement schema) =>
        schema.TryGetProperty("properties", out var properties)
            ? "Input sources: " + string.Join("; ", properties.EnumerateObject()
                .Select(property => $"{property.Name}: {McpInputShapeGuidance.ReadDescription(property.Value)}"))
            : "Caller-supplied tool arguments; follow the tool inputSchema.";

    private static JsonElement WritePayload(JsonElement payload, Guidance guidance)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in payload.EnumerateObject()) WriteProperty(writer, property, guidance);
            WriteViolations(writer, payload, guidance.Violations);
            WriteRemediation(writer, payload, guidance.Sources);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static void WriteProperty(Utf8JsonWriter writer, JsonProperty property, Guidance guidance)
    {
        if (property.Name is "violations" or "remediation") return;
        if (property.Name == "error" && property.Value.ValueKind == JsonValueKind.Object)
        {
            writer.WritePropertyName("error");
            WriteError(writer, property.Value, guidance.Violations);
        }
        else property.WriteTo(writer);
    }

    private static void WriteError(Utf8JsonWriter writer, JsonElement error, IReadOnlyList<McpValidationViolation> guidance)
    {
        writer.WriteStartObject();
        foreach (var property in error.EnumerateObject())
            if (property.Name != "violations") property.WriteTo(writer);
        WriteViolations(writer, error, guidance);
        writer.WriteEndObject();
    }

    private static void WriteViolations(Utf8JsonWriter writer, JsonElement payload, IReadOnlyList<McpValidationViolation> guidance)
    {
        var hasExisting = payload.TryGetProperty("violations", out var existing) && existing.ValueKind == JsonValueKind.Array;
        if (!hasExisting && guidance.Count == 0) return;
        writer.WriteStartArray("violations");
        if (hasExisting) foreach (var violation in existing.EnumerateArray()) violation.WriteTo(writer);
        foreach (var violation in guidance) WriteViolation(writer, violation);
        writer.WriteEndArray();
    }

    private static void WriteViolation(Utf8JsonWriter writer, McpValidationViolation violation)
    {
        writer.WriteStartObject();
        writer.WriteString("code", violation.Code);
        writer.WriteString("fieldPath", violation.FieldPath);
        writer.WriteString("message", violation.Message);
        writer.WriteEndObject();
    }

    private static void WriteRemediation(Utf8JsonWriter writer, JsonElement payload, string sources)
    {
        var existing = payload.TryGetProperty("remediation", out var remediation) && remediation.ValueKind == JsonValueKind.String
            ? remediation.GetString() : null;
        writer.WriteString("remediation", string.IsNullOrWhiteSpace(existing) ? sources : existing + "\n" + sources);
    }
}
