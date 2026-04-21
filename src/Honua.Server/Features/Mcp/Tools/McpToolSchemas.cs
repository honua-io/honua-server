// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Server.Features.Mcp.Tools;

/// <summary>
/// JSON-schema documents published in <c>tools/list</c>. Plan schemas are
/// assembled at type-load time from the canonical
/// <see cref="AnalysisPlanStepKind"/> and <see cref="ArtifactKind"/> enums so
/// the published contract cannot drift from the server-side parser in
/// <see cref="McpToolHelpers"/>. Schemas remain immutable <see cref="JsonElement"/>
/// values to keep the MCP surface AOT-safe.
/// </summary>
internal static class McpToolSchemas
{
    /// <summary>
    /// Canonical step-kind names advertised to MCP clients. The live parser
    /// accepts these values case-insensitively, but the published schema pins
    /// the canonical PascalCase spelling to match the domain enum.
    /// </summary>
    public static readonly IReadOnlyList<string> PlanStepKindNames = Enum.GetNames<AnalysisPlanStepKind>();

    /// <summary>
    /// Canonical artifact-kind names advertised to MCP clients.
    /// </summary>
    public static readonly IReadOnlyList<string> ArtifactKindNames = Enum.GetNames<ArtifactKind>();

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
    /// and dry_run_plan. The validator intentionally reports missing
    /// <c>planId</c> and empty <c>steps</c> as structured violations rather
    /// than rejecting them at ingest, so the published schema does not mark
    /// those fields as required — otherwise schema-driven clients would block
    /// inputs the validator is meant to inspect.
    /// </summary>
    public static readonly JsonElement PlanArgumentSchema = Parse(BuildPlanArgumentSchema(requireExecutableShape: false, includeIdempotencyKey: false));

    /// <summary>
    /// Schema for <see cref="Models.McpExecutePlanArgument"/>. Execute rejects
    /// missing <c>planId</c> and empty <c>steps</c> at submission time, so the
    /// published schema marks both as required and pins <c>steps</c> to
    /// <c>minItems: 1</c> — this keeps the schema consistent with the
    /// submission-path guard in <see cref="Geoprocessing.GeoprocessingJobService"/>.
    /// </summary>
    public static readonly JsonElement ExecutePlanArgumentSchema = Parse(BuildPlanArgumentSchema(requireExecutableShape: true, includeIdempotencyKey: true));

    /// <summary>
    /// Schema for <see cref="Models.McpCancelJobArgument"/>.
    /// </summary>
    public static readonly JsonElement CancelJobArgumentSchema = Parse(CancelJobArgumentSchemaJson);

    /// <summary>
    /// Schema used by stub tools that accept no arguments.
    /// </summary>
    public static readonly JsonElement EmptyObjectSchema = Parse(EmptyObjectSchemaJson);

    private static string BuildPlanArgumentSchema(bool requireExecutableShape, bool includeIdempotencyKey)
    {
        var stepKindEnum = JsonStringArray(PlanStepKindNames);
        var stepKindExamples = string.Join(", ", PlanStepKindNames);
        var artifactKindEnum = JsonStringArray(ArtifactKindNames);
        var artifactKindExamples = string.Join(", ", ArtifactKindNames);

        // Validate/dry-run tools must accept partial plans so the server can
        // report EMPTY_PLAN_ID/EMPTY_STEPS as structured violations; execute
        // refuses those shapes outright and the schema matches that guard.
        var planRequired = requireExecutableShape
            ? """["planId", "steps"]"""
            : "[]";
        var planIdSchema = requireExecutableShape
            ? """{ "type": "string", "minLength": 1 }"""
            : """{ "type": "string" }""";
        var stepsMinItems = requireExecutableShape
            ? "\"minItems\": 1,\n                      "
            : string.Empty;

        var builder = new StringBuilder();
        builder.Append(
            $$"""
            {
              "type": "object",
              "required": ["plan"],
              "properties": {
                "plan": {
                  "type": "object",
                  "description": "Canonical analysis plan expressed as MCP plan input.",
                  "required": {{planRequired}},
                  "properties": {
                    "planId": {{planIdSchema}},
                    "intentId": { "type": "string" },
                    "steps": {
                      "type": "array",
                      {{stepsMinItems}}"items": {
                        "type": "object",
                        "required": ["stepId", "kind"],
                        "properties": {
                          "stepId": { "type": "string" },
                          "kind": {
                            "type": "string",
                            "enum": {{stepKindEnum}},
                            "description": "Canonical AnalysisPlanStepKind value ({{stepKindExamples}}). Server parsing is case-insensitive but clients should send the canonical PascalCase spelling."
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
                      "items": {
                        "type": "string",
                        "enum": {{artifactKindEnum}},
                        "description": "Canonical ArtifactKind value ({{artifactKindExamples}}). Server parsing is case-insensitive but clients should send the canonical PascalCase spelling."
                      }
                    },
                    "warnings": {
                      "type": "array",
                      "items": { "type": "string" }
                    }
                  }
                }
            """);

        if (includeIdempotencyKey)
        {
            builder.Append(
                """
                ,
                    "idempotencyKey": {
                      "type": "string",
                      "description": "Optional deduplication key. Replays with the same key return the same job record."
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
