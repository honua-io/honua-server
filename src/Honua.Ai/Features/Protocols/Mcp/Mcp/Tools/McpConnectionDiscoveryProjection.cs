// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Core.Features.Operations.Domain;
using Honua.Geoprocessing;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// MCP wire shapes for the canonical connection discovery reads. Project only these fields,
/// even if the REST response later acquires additional credential or connection metadata.
/// </summary>
internal static class McpConnectionDiscoveryProjection
{
    internal const string ConnectionsOperationId = "admin.connections.list";
    internal const string TablesOperationId = "admin.connections.tables.discover";

    private static readonly JsonElement ConnectionsOutputSchema = Parse("""
        {
          "type": "object",
          "required": [
            "data"
          ],
          "properties": {
            "data": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": [
                  "id",
                  "name",
                  "provider",
                  "status"
                ],
                "properties": {
                  "id": {
                    "type": "string"
                  },
                  "name": {
                    "type": "string"
                  },
                  "provider": {
                    "type": "string"
                  },
                  "status": {
                    "type": "string"
                  }
                }
              }
            }
          }
        }
        """);
    private static readonly JsonElement TablesOutputSchema = Parse("""
        {
          "type": "object",
          "required": [
            "tables"
          ],
          "properties": {
            "tables": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": [
                  "schema",
                  "table",
                  "geometryColumn",
                  "geometryType",
                  "estimatedRows"
                ],
                "properties": {
                  "schema": {
                    "type": "string"
                  },
                  "table": {
                    "type": "string"
                  },
                  "geometryColumn": {
                    "type": [
                      "string",
                      "null"
                    ]
                  },
                  "geometryType": {
                    "type": [
                      "string",
                      "null"
                    ]
                  },
                  "estimatedRows": {
                    "type": [
                      "integer",
                      "null"
                    ]
                  }
                }
              }
            }
          }
        }
        """);

    internal static bool Contains(string operationId) =>
        operationId is ConnectionsOperationId or TablesOperationId;

    internal static string InputName(string operationId, string parameterName) =>
        operationId == TablesOperationId && parameterName == "id" ? "connectionId" : parameterName;

    internal static McpToolDescriptor Describe(string operationId, string name, JsonElement inputSchema)
    {
        var connections = operationId == ConnectionsOperationId;
        var title = connections ? "List connections" : "List connection tables";
        return new McpToolDescriptor
        {
            Name = name,
            Title = title,
            Description = connections
                ? "List database connections: id, name, provider, and health status. Admin read permission required. No credentials or connection strings. Use an id with honua_admin_connection_tables_list."
                : "List spatial tables in connectionId: schema, table, geometryColumn, geometryType (geometry family), and estimatedRows. Admin read permission required. Use these fields with honua_admin_layer_publish.",
            InputSchema = inputSchema,
            OutputSchema = connections ? ConnectionsOutputSchema : TablesOutputSchema,
            Annotations = McpToolAnnotationSets.ReadOnly(title),
        };
    }

    internal static McpToolsCallResult CreateResult(string operationId, OperationHandle handle)
    {
        if (handle.Status == OperationHandleStatus.Denied)
            return McpToolHelpers.ErrorResult(new GeoprocessingAuthorizationException(
                requiresAuthentication: false, message: "Connection discovery is not authorized."));

        // Never forward an error response, exception message, or other operation detail.
        // Only a completed REST read can enter the explicit success-field projection.
        if (handle.Status != OperationHandleStatus.Completed ||
            handle.Result?.Details.TryGetValue("response", out var response) != true || response is null)
            return FailedResult();

        try
        {
            return McpToolHelpers.SuccessJsonElement(ProjectResponse(operationId, response));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return FailedResult();
        }
    }

    private static McpToolsCallResult FailedResult() =>
        McpToolHelpers.ErrorResult(new InvalidOperationException("Connection discovery failed."));

    internal static JsonElement ProjectResponse(string operationId, string response)
    {
        using var document = JsonDocument.Parse(response);
        var connections = operationId == ConnectionsOperationId;
        var arrayName = connections ? "data" : "tables";
        var rows = document.RootElement.GetProperty(arrayName);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray(arrayName);
            foreach (var row in rows.EnumerateArray())
            {
                writer.WriteStartObject();
                if (connections)
                {
                    WriteString(writer, row, "connectionId", "id");
                    WriteString(writer, row, "name", "name");
                    WriteString(writer, row, "provider", "provider");
                    WriteString(writer, row, "healthStatus", "status");
                }
                else
                {
                    WriteString(writer, row, "schema", "schema");
                    WriteString(writer, row, "table", "table");
                    WriteString(writer, row, "geometryColumn", "geometryColumn", nullable: true);
                    WriteString(writer, row, "geometryType", "geometryType", nullable: true);
                    writer.WritePropertyName("estimatedRows");
                    if (row.TryGetProperty("estimatedRows", out var estimate) && estimate.ValueKind != JsonValueKind.Null)
                        writer.WriteNumberValue(estimate.GetInt64());
                    else
                        writer.WriteNullValue();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        using var projected = JsonDocument.Parse(buffer.WrittenMemory);
        return projected.RootElement.Clone();
    }

    private static void WriteString(Utf8JsonWriter writer, JsonElement row, string source, string target, bool nullable = false)
    {
        if (nullable && (!row.TryGetProperty(source, out var value) || value.ValueKind == JsonValueKind.Null))
            writer.WriteNull(target);
        else
            writer.WriteString(target, row.GetProperty(source).GetString());
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
