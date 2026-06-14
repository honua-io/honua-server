// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Ai.AppGeneration;

/// <summary>
/// Builds the constrained JSON schema for the app-generation structured output, mirroring
/// <c>MapGenerationSchema</c>. Constrains the app body to the console's <c>studio-app/v1</c> vocabulary:
/// the component-kind enum (map/dashboard/form/report/table), the requiredPermission enum
/// (viewer/editor/operator), and the sharePolicy.visibility enum (private/workspace/organization/public)
/// so the model cannot invent kinds/permissions/tiers, and pins the body <c>schemaVersion</c> to
/// studio-app/v1. Enum literals are escaped reflection-free via <see cref="JsonEncodedText"/> (the app
/// disables reflection-based serialization, so a bare <c>JsonSerializer.Serialize(string)</c> throws).
/// </summary>
internal static class AppGenerationSchema
{
    public static JsonElement Build()
    {
        // Enum literals, escaped reflection-free (the app disables reflection-based serialization).
        var componentKindEnum = string.Join(",",
            AppGenerationStructuralValidator.ComponentKinds.Select(k => "\"" + JsonEncodedText.Encode(k) + "\""));
        var permissionEnum = string.Join(",",
            AppGenerationStructuralValidator.Permissions.Select(p => "\"" + JsonEncodedText.Encode(p) + "\""));
        var visibilityEnum = string.Join(",",
            AppGenerationStructuralValidator.Visibilities.Select(v => "\"" + JsonEncodedText.Encode(v) + "\""));
        var schemaVersion = JsonEncodedText.Encode(AppGenerationStructuralValidator.AppPackageSchemaVersion);

        var json =
            $$"""
            {
              "type": "object",
              "properties": {
                "status": { "type": "string", "enum": ["generated", "needs-clarification", "unsupported", "refused"] },
                "rationale": { "type": "string" },
                "app": {
                  "type": ["object", "null"],
                  "properties": {
                    "schemaVersion": { "type": "string", "enum": ["{{schemaVersion}}"] },
                    "title": { "type": "string" },
                    "summary": { "type": "string" },
                    "pages": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "properties": {
                          "route": { "type": "string" },
                          "title": { "type": "string" },
                          "component": {
                            "type": "object",
                            "properties": {
                              "kind": { "type": "string", "enum": [{{componentKindEnum}}] },
                              "binding": { "type": ["string", "null"] }
                            },
                            "required": ["kind"],
                            "additionalProperties": false
                          }
                        },
                        "required": ["route", "title", "component"],
                        "additionalProperties": false
                      }
                    },
                    "navigation": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "properties": {
                          "route": { "type": "string" },
                          "label": { "type": "string" }
                        },
                        "required": ["route", "label"],
                        "additionalProperties": false
                      }
                    },
                    "actions": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "properties": {
                          "name": { "type": "string" },
                          "pageRoute": { "type": "string" },
                          "requiredPermission": { "type": "string", "enum": [{{permissionEnum}}] }
                        },
                        "required": ["name", "pageRoute", "requiredPermission"],
                        "additionalProperties": false
                      }
                    },
                    "sharePolicy": {
                      "type": "object",
                      "properties": {
                        "visibility": { "type": "string", "enum": [{{visibilityEnum}}] },
                        "embed": { "type": "boolean" },
                        "reviewed": { "type": "boolean" }
                      },
                      "required": ["visibility", "embed", "reviewed"],
                      "additionalProperties": false
                    }
                  },
                  "required": ["schemaVersion", "title", "summary", "pages", "navigation", "actions", "sharePolicy"],
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
