// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Tools;

internal static class McpPublishedLayerDiscovery
{
    private static readonly string[] StringFields = ["layerName", "schema", "table", "serviceName"];
    private static readonly JsonElement OutputSchema = Parse("""
        {"type":"object","additionalProperties":false,"required":["layers","page","pageSize","total"],"properties":{
          "layers":{"type":"array","items":{"type":"object","additionalProperties":false,
            "required":["layerId","layerName","schema","table","serviceName","enabled"],"properties":{
              "layerId":{"type":"integer"},"layerName":{"type":"string"},"schema":{"type":"string"},
              "table":{"type":"string"},"serviceName":{"type":"string"},"enabled":{"type":"boolean"}}}},
          "page":{"type":"integer","minimum":1},"pageSize":{"type":"integer","minimum":1},
          "total":{"type":"integer","minimum":0}}}
        """);

    internal static McpToolDescriptor Describe(string name, JsonElement inputSchema) => new()
    {
        Name = name,
        Title = "List connection layers",
        Description = "List published storage layers in connectionId from honua_admin_connections_list. Returns storage layerId, serviceName, schema, table and enabled status. Use layerId with honua_admin_layer_set_enabled; it is not the publication index from honua_list_layers. Follow page/pageSize/total to discover every result. Admin read permission required. No credentials or connection strings.",
        InputSchema = inputSchema,
        OutputSchema = OutputSchema,
        Annotations = McpToolAnnotationSets.ReadOnly("List connection layers")
    };

    internal static JsonElement Project(JsonElement response)
    {
        var data = response.GetProperty("data");
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("layers");
            foreach (var row in data.GetProperty("items").EnumerateArray()) WriteLayer(writer, row);
            writer.WriteEndArray();
            writer.WriteNumber("page", data.GetProperty("page").GetInt32());
            writer.WriteNumber("pageSize", data.GetProperty("pageSize").GetInt32());
            writer.WriteNumber("total", data.GetProperty("total").GetInt32());
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static void WriteLayer(Utf8JsonWriter writer, JsonElement row)
    {
        writer.WriteStartObject();
        writer.WriteNumber("layerId", row.GetProperty("layerId").GetInt32());
        foreach (var field in StringFields)
        {
            var value = row.GetProperty(field).GetString() ?? throw new InvalidOperationException();
            writer.WriteString(field, value);
        }
        writer.WriteBoolean("enabled", row.GetProperty("enabled").GetBoolean());
        writer.WriteEndObject();
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
