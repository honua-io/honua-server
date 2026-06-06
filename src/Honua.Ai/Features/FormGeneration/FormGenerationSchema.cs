// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Ai.FormGeneration;

/// <summary>
/// Builds the constrained JSON schema for the form-generation structured output, mirroring
/// <c>WorkflowGenerationSchema</c>. Constrains each field's <c>type</c> to the supported
/// field-type vocabulary so the model cannot invent field types.
/// </summary>
internal static class FormGenerationSchema
{
    public static JsonElement Build()
    {
        // Field-type enum, escaped reflection-free (the app disables reflection-based serialization).
        var fieldTypeEnum = string.Join(",",
            FormGenerationPrompt.FieldTypes.Select(t => "\"" + JsonEncodedText.Encode(t) + "\""));

        var json =
            $$"""
            {
              "type": "object",
              "properties": {
                "status": { "type": "string", "enum": ["generated", "needs-clarification", "unsupported", "refused"] },
                "rationale": { "type": "string" },
                "form": {
                  "type": ["object", "null"],
                  "properties": {
                    "title": { "type": "string" },
                    "description": { "type": ["string", "null"] },
                    "target": {
                      "type": "object",
                      "properties": {
                        "serviceId": { "type": "string" },
                        "layerId": { "type": "integer" }
                      },
                      "required": ["serviceId", "layerId"],
                      "additionalProperties": false
                    },
                    "sections": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "properties": {
                          "sectionId": { "type": "string" },
                          "label": { "type": "string" },
                          "description": { "type": ["string", "null"] },
                          "fieldIds": { "type": "array", "items": { "type": "string" } },
                          "repeatable": { "type": "boolean" }
                        },
                        "required": ["sectionId", "label"],
                        "additionalProperties": false
                      }
                    },
                    "fields": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "properties": {
                          "fieldId": { "type": "string" },
                          "label": { "type": "string" },
                          "hint": { "type": ["string", "null"] },
                          "type": { "type": "string", "enum": [{{fieldTypeEnum}}] },
                          "targetField": { "type": ["string", "null"] },
                          "sectionId": { "type": ["string", "null"] },
                          "required": { "type": "boolean" },
                          "readOnly": { "type": "boolean" },
                          "private": { "type": "boolean" },
                          "domain": {
                            "type": ["object", "null"],
                            "properties": {
                              "type": { "type": "string", "enum": ["codedValue", "range"] },
                              "choices": {
                                "type": "array",
                                "items": {
                                  "type": "object",
                                  "properties": {
                                    "code": { "type": ["string", "number"] },
                                    "label": { "type": "string" }
                                  },
                                  "required": ["code", "label"],
                                  "additionalProperties": false
                                }
                              },
                              "min": { "type": ["number", "null"] },
                              "max": { "type": ["number", "null"] }
                            },
                            "required": ["type"],
                            "additionalProperties": false
                          },
                          "validation": {
                            "type": "array",
                            "items": {
                              "type": "object",
                              "properties": {
                                "type": { "type": "string", "enum": ["minLength", "maxLength", "min", "max", "regex"] },
                                "message": { "type": ["string", "null"] }
                              },
                              "required": ["type"],
                              "additionalProperties": false
                            }
                          },
                          "visibility": {
                            "type": ["object", "null"],
                            "properties": {
                              "dependsOnFieldId": { "type": "string" },
                              "operator": { "type": "string", "enum": ["equals", "notEquals", "gt", "gte", "lt", "lte", "isEmpty", "isNotEmpty", "in"] },
                              "value": { "type": ["string", "number", "boolean", "null"] }
                            },
                            "required": ["dependsOnFieldId", "operator"],
                            "additionalProperties": false
                          }
                        },
                        "required": ["fieldId", "label", "type"],
                        "additionalProperties": false
                      }
                    }
                  },
                  "required": ["title", "target", "fields"],
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
