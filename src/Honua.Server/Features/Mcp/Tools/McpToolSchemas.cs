// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Server.Features.Mcp.Tools;

/// <summary>
/// Static JSON-schema documents published in <c>tools/list</c>. Schemas are kept
/// as <c>const</c> strings and parsed once so the MCP surface remains AOT-safe
/// without reflecting over <see cref="Models.McpPlanArgument"/> and siblings.
/// </summary>
internal static class McpToolSchemas
{
    private const string PlanArgumentSchemaJson = """
        {
          "type": "object",
          "required": ["plan"],
          "properties": {
            "plan": {
              "type": "object",
              "description": "Canonical analysis plan expressed as MCP plan input.",
              "required": ["steps"],
              "properties": {
                "planId": { "type": "string" },
                "intentId": { "type": "string" },
                "steps": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "required": ["stepId", "kind"],
                    "properties": {
                      "stepId": { "type": "string" },
                      "kind": {
                        "type": "string",
                        "description": "Step kind name (e.g. CallProcess, Stage, Publish)."
                      },
                      "processId": { "type": "string" },
                      "inputs": {
                        "type": "object",
                        "additionalProperties": { "type": "string" }
                      },
                      "dependsOn": {
                        "type": "array",
                        "items": { "type": "string" }
                      }
                    }
                  }
                },
                "outputs": {
                  "type": "array",
                  "items": { "type": "string" }
                },
                "warnings": {
                  "type": "array",
                  "items": { "type": "string" }
                }
              }
            }
          }
        }
        """;

    private const string ExecutePlanArgumentSchemaJson = """
        {
          "type": "object",
          "required": ["plan"],
          "properties": {
            "plan": {
              "type": "object",
              "description": "Canonical analysis plan expressed as MCP plan input.",
              "required": ["steps"],
              "properties": {
                "planId": { "type": "string" },
                "intentId": { "type": "string" },
                "steps": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "required": ["stepId", "kind"],
                    "properties": {
                      "stepId": { "type": "string" },
                      "kind": { "type": "string" },
                      "processId": { "type": "string" },
                      "inputs": {
                        "type": "object",
                        "additionalProperties": { "type": "string" }
                      },
                      "dependsOn": {
                        "type": "array",
                        "items": { "type": "string" }
                      }
                    }
                  }
                },
                "outputs": {
                  "type": "array",
                  "items": { "type": "string" }
                },
                "warnings": {
                  "type": "array",
                  "items": { "type": "string" }
                }
              }
            },
            "idempotencyKey": {
              "type": "string",
              "description": "Optional deduplication key. Replays with the same key return the same job record."
            }
          }
        }
        """;

    private const string CancelJobArgumentSchemaJson = """
        {
          "type": "object",
          "required": ["jobId"],
          "properties": {
            "jobId": {
              "type": "string",
              "description": "Execution job identifier to request cancellation for."
            }
          }
        }
        """;

    private const string EmptyObjectSchemaJson = """
        {
          "type": "object",
          "additionalProperties": false
        }
        """;

    /// <summary>
    /// Schema for <see cref="Models.McpPlanArgument"/>, shared by validate_plan
    /// and dry_run_plan.
    /// </summary>
    public static readonly JsonElement PlanArgumentSchema = Parse(PlanArgumentSchemaJson);

    /// <summary>
    /// Schema for <see cref="Models.McpExecutePlanArgument"/>.
    /// </summary>
    public static readonly JsonElement ExecutePlanArgumentSchema = Parse(ExecutePlanArgumentSchemaJson);

    /// <summary>
    /// Schema for <see cref="Models.McpCancelJobArgument"/>.
    /// </summary>
    public static readonly JsonElement CancelJobArgumentSchema = Parse(CancelJobArgumentSchemaJson);

    /// <summary>
    /// Schema used by stub tools that accept no arguments.
    /// </summary>
    public static readonly JsonElement EmptyObjectSchema = Parse(EmptyObjectSchemaJson);

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
