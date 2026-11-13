// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Ai.Protocols.Mcp.Discovery;

internal static class DiscoveryToolSchemas
{
    public const int DefaultResolveLimit = 10;

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
            },
            "toolCursor": {
              "type": "string",
              "description": "Opaque cursor returned as nextToolCursor. Omit for the first bounded page."
            },
            "resourceCursor": {
              "type": "string",
              "description": "Opaque cursor returned as nextResourceCursor. Omit for the first bounded page."
            },
            "fullExport": {
              "type": "boolean",
              "default": false,
              "description": "Explicit admin-only operation that exports the complete capability inventory."
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
                  "serviceLayerIndex": { "type": ["integer", "null"] },
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
            "totalToolCount": { "type": "integer" },
            "nextToolCursor": { "type": ["string", "null"] },
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
            "totalResourceCount": { "type": "integer" },
            "nextResourceCursor": { "type": ["string", "null"] },
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
            "groundingResources": { "type": "array", "items": { "type": "string" } },
            "workflowViews": {
              "type": "array",
              "description": "Named server-authored workflow discovery views. Pass one of these names as tools/list params.view (or negotiate it once at initialize via params._meta[\"honua.io/workflow-view\"]) to receive only that bounded set of canonical descriptors. The reserved \"full\" view is an explicit admin-only catalog export. Selecting a view narrows discovery only; every call is still independently authenticated and authorized.",
              "items": {
                "type": "object",
                "properties": {
                  "name": { "type": "string" },
                  "title": { "type": "string" },
                  "description": { "type": "string" },
                  "revision": { "type": "string" },
                  "revisionDigest": { "type": "string" },
                  "membershipDigest": { "type": "string" },
                  "descriptorDigest": { "type": "string" },
                  "toolCount": { "type": "integer" },
                  "descriptorBytes": { "type": "integer" },
                  "estimatedTokens": { "type": "integer" },
                  "stages": { "type": "array", "items": { "type": "string" } }
                }
              }
            }
          }
        }
        """;

    public static readonly JsonElement ResolveEntityArgumentSchema = Parse(ResolveEntityArgumentSchemaJson);

    public static readonly JsonElement ListCapabilitiesArgumentSchema = Parse(ListCapabilitiesArgumentSchemaJson);

    public static readonly JsonElement ResolveEntityOutputSchema = Parse(ResolveEntityOutputSchemaJson);

    public static readonly JsonElement ListCapabilitiesOutputSchema = Parse(ListCapabilitiesOutputSchemaJson);

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
