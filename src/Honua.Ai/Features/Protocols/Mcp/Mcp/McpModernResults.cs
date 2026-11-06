// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp;

internal static class McpModernResults
{
    internal static JsonElement Discover() => JsonSerializer.SerializeToElement(
        new McpDiscoverResult(), McpJsonContext.Default.McpDiscoverResult);

    internal static JsonElement Complete(JsonElement result, JsonElement request)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("resultType", "complete");
            foreach (var property in result.EnumerateObject())
            {
                if (property.Name is not ("resultType" or "_meta" or "ttlMs" or "cacheScope"))
                {
                    property.WriteTo(writer);
                }
            }

            WriteMetadata(writer, result);
            if (IsCacheable(request))
            {
                writer.WriteNumber("ttlMs", 0);
                writer.WriteString("cacheScope", "private");
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    private static void WriteMetadata(Utf8JsonWriter writer, JsonElement result)
    {
        writer.WriteStartObject("_meta");
        if (result.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in meta.EnumerateObject())
            {
                if (property.Name != "io.modelcontextprotocol/serverInfo")
                {
                    property.WriteTo(writer);
                }
            }
        }

        writer.WritePropertyName("io.modelcontextprotocol/serverInfo");
        JsonSerializer.Serialize(writer, new McpServerInfo(), McpJsonContext.Default.McpServerInfo);
        writer.WriteEndObject();
    }

    private static bool IsCacheable(JsonElement request)
    {
        if (request.TryGetProperty("params", out var parameters)
            && (parameters.TryGetProperty("inputResponses", out _) || parameters.TryGetProperty("requestState", out _)))
        {
            return false;
        }

        return McpModernProtocol.ReadString(request, "method") is "server/discover" or "tools/list"
            or "prompts/list" or "resources/list" or "resources/templates/list" or "resources/read";
    }
}
