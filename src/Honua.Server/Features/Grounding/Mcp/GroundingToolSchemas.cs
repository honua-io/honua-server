// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Grounding.Domain;

namespace Honua.Server.Features.Grounding.Mcp;

/// <summary>
/// JSON-schema documents advertised in <c>tools/list</c> for the grounding
/// tools. Schemas are assembled at type-load time from the canonical workflow
/// family and assumption-policy enums so the published contract cannot drift
/// from server-side parsing.
/// </summary>
internal static class GroundingToolSchemas
{
    /// <summary>
    /// Canonical workflow-family names advertised to MCP clients.
    /// </summary>
    public static readonly IReadOnlyList<string> WorkflowFamilyNames = Enum.GetNames<WorkflowFamily>();

    /// <summary>
    /// Canonical assumption-policy names advertised to MCP clients.
    /// </summary>
    public static readonly IReadOnlyList<string> AssumptionPolicyNames = Enum.GetNames<AssumptionPolicy>();

    /// <summary>
    /// Schema for <see cref="McpGroundCandidatesArgument"/>.
    /// </summary>
    public static readonly JsonElement GroundCandidatesArgumentSchema = Parse(BuildSchema(includeClarificationResponse: false));

    /// <summary>
    /// Schema for <see cref="McpClarifyIntentArgument"/>.
    /// </summary>
    public static readonly JsonElement ClarifyIntentArgumentSchema = Parse(BuildSchema(includeClarificationResponse: true));

    private static string BuildSchema(bool includeClarificationResponse)
    {
        var workflowFamilyEnum = JsonStringArray(WorkflowFamilyNames);
        var assumptionPolicyEnum = JsonStringArray(AssumptionPolicyNames);

        // honua_ground_candidates always needs a goal text; honua_clarify_intent
        // re-runs grounding with the answers merged in, so it also needs the
        // goal (plus intentId + response.answers). Keeping goal required keeps
        // the advertised schema aligned with GroundingToolMapper.ToDomain.
        var required = includeClarificationResponse
            ? """["goal", "intentId", "response"]"""
            : """["goal"]""";

        var builder = new StringBuilder();
        builder.Append(
            $$"""
            {
              "type": "object",
              "required": {{required}},
              "properties": {
                "goal": {
                  "type": "string",
                  "minLength": 1,
                  "description": "Freeform natural-language description of the operator's goal."
                },
                "intentId": {
                  "type": "string",
                  "description": "Intent identifier. Required for clarification turns; optional on the initial grounding call."
                },
                "workflowFamilyHint": {
                  "type": "string",
                  "enum": {{workflowFamilyEnum}},
                  "description": "Optional workflow-family override. When supplied the classifier reports 1.0 confidence with evidence=hint."
                },
                "assumptionPolicy": {
                  "type": "string",
                  "enum": {{assumptionPolicyEnum}},
                  "description": "Policy controlling when inferred assumptions trigger a clarification."
                },
                "explicitInputs": {
                  "type": "array",
                  "items": { "type": "string" },
                  "description": "Dataset, layer, or artifact identifiers the caller has already pinned."
                },
                "constraints": {
                  "type": "object",
                  "additionalProperties": false,
                  "properties": {
                    "areaOfInterest": {
                      "type": "string",
                      "description": "AOI as WKT, bounding-box string, or named area."
                    },
                    "spatialReferenceId": {
                      "type": "integer",
                      "description": "EPSG SRID for the AOI (defaults to 4326 when omitted)."
                    },
                    "timeWindowStart": {
                      "type": "string",
                      "format": "date-time"
                    },
                    "timeWindowEnd": {
                      "type": "string",
                      "format": "date-time"
                    },
                    "units": {
                      "type": "string",
                      "description": "Preferred unit system (e.g. 'meters', 'feet')."
                    }
                  }
                },
                "context": {
                  "type": "object",
                  "additionalProperties": false,
                  "properties": {
                    "workspaceId": { "type": "string" },
                    "priorIntentId": { "type": "string" },
                    "promotionScope": { "type": "string" }
                  }
                }
            """);

        if (includeClarificationResponse)
        {
            builder.Append(
                """
                ,
                    "response": {
                      "type": "object",
                      "required": ["answers"],
                      "properties": {
                        "answers": {
                          "type": "object",
                          "description": "Answers keyed by question identifier. Each answer is a list of values to support multi-select questions.",
                          "additionalProperties": {
                            "type": "array",
                            "items": { "type": "string" }
                          }
                        }
                      }
                    }
                """);
        }

        builder.Append(
            """

              }
            }
            """);

        return builder.ToString();
    }

    private static string JsonStringArray(IReadOnlyList<string> values)
    {
        var builder = new StringBuilder(2 + values.Count * 16);
        builder.Append('[');
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append('"').Append(values[i]).Append('"');
        }

        builder.Append(']');
        return builder.ToString();
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
