// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Ai.DashboardGeneration;

/// <summary>
/// Builds the constrained JSON schema for the dashboard-generation structured output, mirroring
/// <c>ReportGenerationSchema</c>. Constrains each panel's <c>kind</c> to the supported panel-kind
/// vocabulary so the model cannot invent panel kinds. The <c>chartSpec</c> is a free-form Vega-Lite object
/// — its inner shape is intentionally unconstrained so the model can emit any valid Vega-Lite
/// mark/encoding.
/// </summary>
internal static class DashboardGenerationSchema
{
    public static JsonElement Build()
    {
        // Panel-kind enum, escaped reflection-free (the app disables reflection-based serialization).
        var panelKindEnum = string.Join(",",
            DashboardGenerationPrompt.PanelKinds.Select(k => "\"" + JsonEncodedText.Encode(k) + "\""));

        var json =
            $$"""
            {
              "type": "object",
              "properties": {
                "status": { "type": "string", "enum": ["generated", "needs-clarification", "unsupported", "refused"] },
                "rationale": { "type": "string" },
                "routeSlug": { "type": ["string", "null"] },
                "dashboard": {
                  "type": ["object", "null"],
                  "properties": {
                    "title": { "type": "string" },
                    "description": { "type": ["string", "null"] },
                    "narrative": { "type": ["string", "null"] },
                    "routeSlug": { "type": ["string", "null"] },
                    "breakpoint": { "type": ["string", "null"] },
                    "bindings": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "properties": {
                          "alias": { "type": "string" },
                          "contentRef": { "type": "string" },
                          "versionPin": { "type": ["string", "null"] }
                        },
                        "required": ["alias", "contentRef"],
                        "additionalProperties": false
                      }
                    },
                    "panels": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "properties": {
                          "id": { "type": "string" },
                          "kind": { "type": "string", "enum": [{{panelKindEnum}}] },
                          "title": { "type": ["string", "null"] },
                          "bindingAlias": { "type": ["string", "null"] },
                          "chartSpec": { "type": ["object", "null"] },
                          "field": { "type": ["string", "null"] }
                        },
                        "required": ["id", "kind"],
                        "additionalProperties": false
                      }
                    }
                  },
                  "required": ["title", "panels"],
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
