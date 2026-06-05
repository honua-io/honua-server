// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Ai.MapGeneration;

/// <summary>
/// Builds the constrained JSON schema for the map-generation structured output, mirroring
/// <c>FormGenerationSchema</c>. Constrains each source binding's <c>protocol</c> to the supported
/// source-protocol vocabulary so the model cannot invent protocols, and pins the body <c>format</c> to
/// honua_map_package.v1.
/// </summary>
internal static class MapGenerationSchema
{
    public static JsonElement Build()
    {
        // Protocol enum, escaped reflection-free (the app disables reflection-based serialization).
        var protocolEnum = string.Join(",",
            MapGenerationPrompt.SourceProtocols.Select(t => "\"" + JsonEncodedText.Encode(t) + "\""));

        var json =
            $$"""
            {
              "type": "object",
              "properties": {
                "status": { "type": "string", "enum": ["generated", "needs-clarification", "unsupported", "refused"] },
                "rationale": { "type": "string" },
                "map": {
                  "type": ["object", "null"],
                  "properties": {
                    "mapPackageId": { "type": "string" },
                    "format": { "type": "string", "enum": ["honua_map_package.v1"] },
                    "status": { "type": "string", "enum": ["Draft"] },
                    "createdAt": { "type": "string" },
                    "templateId": { "type": ["string", "null"] },
                    "themeId": { "type": ["string", "null"] },
                    "sourceBindings": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "properties": {
                          "sourceId": { "type": "string" },
                          "protocol": { "type": "string", "enum": [{{protocolEnum}}] },
                          "locator": {
                            "type": "object",
                            "properties": {
                              "url": { "type": "string" },
                              "serviceId": { "type": ["string", "null"] },
                              "layerId": { "type": ["string", "null"] }
                            },
                            "required": ["url"],
                            "additionalProperties": false
                          },
                          "filter": { "type": ["string", "null"] }
                        },
                        "required": ["sourceId", "protocol", "locator"],
                        "additionalProperties": false
                      }
                    },
                    "styleRefs": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "properties": {
                          "styleId": { "type": "string" },
                          "label": { "type": ["string", "null"] },
                          "presetId": { "type": ["string", "null"] }
                        },
                        "required": ["styleId"],
                        "additionalProperties": false
                      }
                    },
                    "initialView": {
                      "type": "object",
                      "properties": {
                        "bbox": { "type": "array", "items": { "type": "number" }, "minItems": 4, "maxItems": 4 },
                        "crs": { "type": "string" }
                      },
                      "required": ["bbox", "crs"],
                      "additionalProperties": false
                    },
                    "legend": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "properties": {
                          "label": { "type": "string" },
                          "color": { "type": "string" },
                          "minValue": { "type": ["number", "null"] },
                          "maxValue": { "type": ["number", "null"] }
                        },
                        "required": ["label", "color"],
                        "additionalProperties": false
                      }
                    },
                    "popupBindings": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "properties": {
                          "sourceId": { "type": "string" },
                          "fieldName": { "type": "string" },
                          "template": { "type": ["string", "null"] }
                        },
                        "required": ["sourceId", "fieldName"],
                        "additionalProperties": false
                      }
                    },
                    "labelBindings": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "properties": {
                          "sourceId": { "type": "string" },
                          "fieldName": { "type": "string" },
                          "placement": { "type": ["string", "null"] }
                        },
                        "required": ["sourceId", "fieldName"],
                        "additionalProperties": false
                      }
                    }
                  },
                  "required": ["mapPackageId", "format", "status", "createdAt", "sourceBindings", "initialView"],
                  "additionalProperties": false
                },
                "clarifications": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "id": { "type": "string" },
                      "kind": { "type": "string" },
                      "prompt": { "type": "string" },
                      "reason": { "type": ["string", "null"] },
                      "choices": {
                        "type": "array",
                        "items": {
                          "type": "object",
                          "properties": {
                            "id": { "type": "string" },
                            "label": { "type": "string" },
                            "effect": { "type": ["string", "null"] }
                          },
                          "required": ["id", "label"],
                          "additionalProperties": false
                        }
                      }
                    },
                    "required": ["id", "kind", "prompt", "choices"],
                    "additionalProperties": false
                  }
                },
                "unmappedRequests": { "type": "array", "items": { "type": "string" } },
                "capabilityState": {
                  "type": ["object", "null"],
                  "properties": {
                    "name": { "type": "string" },
                    "state": { "type": "string" },
                    "reason": { "type": ["string", "null"] }
                  },
                  "required": ["name", "state"],
                  "additionalProperties": false
                }
              },
              "required": ["status"],
              "additionalProperties": false
            }
            """;

        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
