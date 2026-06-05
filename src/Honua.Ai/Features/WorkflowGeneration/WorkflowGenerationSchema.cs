// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Ai.WorkflowGeneration;

/// <summary>
/// Builds the constrained JSON schema for the workflow-generation structured output. The
/// schema mirrors the <c>WorkflowGenerationModelProposal</c> shape and constrains node
/// <c>nodeTypeId</c> to an <c>enum</c> of the grounded registry node-type ids so the model
/// cannot invent node types.
/// </summary>
internal static class WorkflowGenerationSchema
{
    /// <summary>
    /// Builds the proposal JSON schema for the supplied node-type whitelist.
    /// </summary>
    public static JsonElement Build(IReadOnlyList<WorkflowNodeDefinition> nodes)
    {
        var nodeTypeEnum = nodes.Count == 0
            ? "\"__no_node_types_available__\""
            : string.Join(",", nodes.Select(n => JsonSerializer.Serialize(n.NodeTypeId)));

        var schema =
            $$"""
            {
              "type": "object",
              "properties": {
                "status": {
                  "type": "string",
                  "enum": ["generated", "needs-clarification", "unsupported", "refused"]
                },
                "rationale": { "type": "string" },
                "graph": {
                  "type": ["object", "null"],
                  "properties": {
                    "schemaVersion": { "type": "string" },
                    "nodes": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "properties": {
                          "nodeId": { "type": "string" },
                          "nodeTypeId": { "type": "string", "enum": [{{nodeTypeEnum}}] },
                          "parameters": { "type": "object", "additionalProperties": { "type": "string" } },
                          "metadata": { "type": "object", "additionalProperties": { "type": "string" } }
                        },
                        "required": ["nodeId", "nodeTypeId"],
                        "additionalProperties": false
                      }
                    },
                    "edges": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "properties": {
                          "sourceNodeId": { "type": "string" },
                          "targetNodeId": { "type": "string" },
                          "kind": { "type": "string", "enum": ["Data", "Control", "Failure"] },
                          "sourcePort": { "type": ["string", "null"] },
                          "targetPort": { "type": ["string", "null"] }
                        },
                        "required": ["sourceNodeId", "targetNodeId"],
                        "additionalProperties": false
                      }
                    },
                    "schedule": {
                      "type": ["object", "null"],
                      "properties": {
                        "cronExpression": { "type": "string" },
                        "timeZone": { "type": ["string", "null"] },
                        "enabled": { "type": "boolean" }
                      },
                      "required": ["cronExpression"],
                      "additionalProperties": false
                    },
                    "workerProfile": { "type": ["string", "null"] }
                  },
                  "required": ["nodes"],
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

        return JsonDocument.Parse(schema).RootElement.Clone();
    }
}
