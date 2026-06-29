// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Ai.Protocols.Mcp.Discovery;

/// <summary>
/// JSON-schema documents advertised in <c>tools/list</c> for the discovery tools
/// (<c>honua_resolve_entity</c>, <c>honua_list_capabilities</c>). Schemas are
/// immutable <see cref="JsonElement"/> values parsed at type-load time to keep the
/// MCP surface AOT-safe (no runtime schema reflection). Mirrors
/// <see cref="Honua.Ai.Protocols.Mcp.MapTools.MapToolSchemas"/>.
/// </summary>
internal static class DiscoveryToolSchemas
{
    /// <summary>Default number of ranked matches returned by <c>honua_resolve_entity</c>.</summary>
    public const int DefaultResolveLimit = 10;

    /// <summary>Hard ceiling on ranked matches returned by <c>honua_resolve_entity</c>.</summary>
    public const int MaxResolveLimit = 50;

    private const string ResolveEntityArgumentSchemaJson = """
        {
          "type": "object",
          "required": ["text"],
          "properties": {
            "text": {
              "type": "string",
              "minLength": 1,
              "description": "Natural-language name or phrase to resolve to catalog entities (e.g. \"parcels\", \"flood zones\", \"roads\"). The server lexically grounds it against the live published catalog and returns ranked service/layer references."
            },
            "entityType": {
              "type": "string",
              "enum": ["any", "service", "layer"],
              "default": "any",
              "description": "Restrict matches to services, to layers, or to both (default)."
            },
            "limit": {
              "type": "integer",
              "minimum": 1,
              "maximum": 50,
              "default": 10,
              "description": "Maximum number of ranked matches to return (capped at 50)."
            }
          }
        }
        """;

    private const string ListCapabilitiesArgumentSchemaJson = """
        {
          "type": "object",
          "properties": {
            "includeResources": {
              "type": "boolean",
              "default": true,
              "description": "Include the resource catalog (job, catalog, and promotion URIs) in the manifest."
            },
            "includeGroundingResources": {
              "type": "boolean",
              "default": true,
              "description": "Include the grounding/catalog resource URIs a client LLM should read first to ground a workflow."
            }
          }
        }
        """;

    private const string ResolveEntityOutputSchemaJson = """
        {
          "type": "object",
          "required": ["query", "matchCount", "matches"],
          "properties": {
            "query": { "type": "string" },
            "entityType": { "type": "string" },
            "matchCount": { "type": "integer" },
            "matches": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "kind": { "type": "string", "enum": ["service", "layer"] },
                  "serviceId": { "type": "string" },
                  "serviceName": { "type": "string" },
                  "layerId": { "type": ["integer", "null"] },
                  "name": { "type": "string" },
                  "type": { "type": ["string", "null"] },
                  "geometryType": { "type": ["string", "null"] },
                  "description": { "type": ["string", "null"] },
                  "score": { "type": "number" }
                }
              }
            }
          }
        }
        """;

    private const string ListCapabilitiesOutputSchemaJson = """
        {
          "type": "object",
          "required": ["serverName", "toolCount", "tools"],
          "properties": {
            "serverName": { "type": "string" },
            "serverVersion": { "type": "string" },
            "protocolVersions": { "type": "array", "items": { "type": "string" } },
            "toolCount": { "type": "integer" },
            "tools": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "name": { "type": "string" },
                  "title": { "type": ["string", "null"] },
                  "description": { "type": "string" },
                  "readOnly": { "type": "boolean" },
                  "workflowFamily": { "type": "string" }
                }
              }
            },
            "resourceCount": { "type": "integer" },
            "resources": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "uri": { "type": "string" },
                  "name": { "type": "string" },
                  "description": { "type": "string" },
                  "family": { "type": "string" }
                }
              }
            },
            "groundingResources": { "type": "array", "items": { "type": "string" } }
          }
        }
        """;

    /// <summary>Input schema for <see cref="McpResolveEntityArgument"/>.</summary>
    public static readonly JsonElement ResolveEntityArgumentSchema = Parse(ResolveEntityArgumentSchemaJson);

    /// <summary>Input schema for <see cref="McpListCapabilitiesArgument"/>.</summary>
    public static readonly JsonElement ListCapabilitiesArgumentSchema = Parse(ListCapabilitiesArgumentSchemaJson);

    /// <summary>Output schema for <see cref="McpResolveEntityOutput"/>.</summary>
    public static readonly JsonElement ResolveEntityOutputSchema = Parse(ResolveEntityOutputSchemaJson);

    /// <summary>Output schema for <see cref="McpListCapabilitiesOutput"/>.</summary>
    public static readonly JsonElement ListCapabilitiesOutputSchema = Parse(ListCapabilitiesOutputSchemaJson);

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
