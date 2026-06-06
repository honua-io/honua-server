// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Ai.QueryGeneration;

/// <summary>
/// Builds the constrained JSON schema for the query-generation structured output, mirroring
/// <c>AnalysisGenerationSchema</c>/<c>FormGenerationSchema</c> and reusing the <c>FilterPlan</c> schema
/// shape from <c>OpenAiNlQueryPlanProvider</c>. Constrains the filter-plan combinators and each clause's
/// comparison/spatial/temporal operator to the exact vocabularies the canonical <c>FilterPlanCompiler</c>
/// accepts so the model cannot invent an operator, and constrains the proposal envelope to the
/// status/query/rationale/clarifications/unmappedRequests/capabilityState contract.
/// </summary>
internal static class QueryGenerationSchema
{
    // Built once: the schema is static (no per-request grounding catalog, unlike analysis generation).
    private static readonly JsonElement Schema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "status": { "type": "string", "enum": ["generated", "needs-clarification", "unsupported", "refused"] },
            "rationale": { "type": "string" },
            "query": {
              "type": ["object", "null"],
              "properties": {
                "naturalLanguageQuery": { "type": ["string", "null"] },
                "layerId": { "type": "integer" },
                "serviceName": { "type": ["string", "null"] },
                "filterPlan": {
                  "type": ["object", "null"],
                  "properties": {
                    "combinator": { "type": "string", "enum": ["and", "or"] },
                    "clauses": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "properties": {
                          "type": { "type": "string", "enum": ["comparison", "spatial", "temporal", "nested"] },
                          "comparison": {
                            "type": ["object", "null"],
                            "properties": {
                              "property": { "type": "string" },
                              "operator": { "type": "string", "enum": ["eq", "neq", "lt", "lte", "gt", "gte", "like", "in", "isNull"] },
                              "value": {
                                "anyOf": [
                                  { "type": "string" },
                                  { "type": "number" },
                                  { "type": "boolean" },
                                  { "type": "null" },
                                  { "type": "array", "items": { "type": "string" } }
                                ]
                              }
                            },
                            "required": ["property", "operator"],
                            "additionalProperties": false
                          },
                          "spatial": {
                            "type": ["object", "null"],
                            "properties": {
                              "operator": { "type": "string", "enum": ["intersects", "within", "contains", "dwithin"] },
                              "geometry": { "type": "object" },
                              "distance": { "type": ["number", "null"] },
                              "distanceUnit": { "type": ["string", "null"] }
                            },
                            "required": ["operator", "geometry"],
                            "additionalProperties": false
                          },
                          "temporal": {
                            "type": ["object", "null"],
                            "properties": {
                              "property": { "type": "string" },
                              "operator": { "type": "string", "enum": ["before", "after", "during"] },
                              "start": { "type": ["string", "null"] },
                              "end": { "type": ["string", "null"] }
                            },
                            "required": ["property", "operator"],
                            "additionalProperties": false
                          },
                          "nested": {
                            "type": ["object", "null"],
                            "properties": {
                              "combinator": { "type": "string", "enum": ["and", "or"] },
                              "clauses": { "type": "array", "items": { "type": "object" } }
                            },
                            "required": ["combinator", "clauses"],
                            "additionalProperties": false
                          }
                        },
                        "required": ["type"],
                        "additionalProperties": false
                      }
                    }
                  },
                  "required": ["combinator", "clauses"],
                  "additionalProperties": false
                },
                "outFields": { "type": "array", "items": { "type": "string" } },
                "outputSrid": { "type": ["integer", "null"] },
                "previewLimit": { "type": ["integer", "null"] },
                "units": { "type": ["string", "null"] }
              },
              "required": ["layerId"],
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
        """).RootElement.Clone();

    public static JsonElement Build() => Schema;
}
