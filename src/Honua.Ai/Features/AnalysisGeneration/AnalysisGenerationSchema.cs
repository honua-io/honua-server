// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Abstractions;

namespace Honua.Ai.AnalysisGeneration;

/// <summary>
/// Builds the constrained JSON schema for the analysis-generation structured output, mirroring
/// <c>WorkflowGenerationSchema</c>/<c>FormGenerationSchema</c>. Constrains each Geoprocess step's
/// <c>processId</c> to an <c>enum</c> of the grounded process-catalog method ids so the model cannot
/// invent a method, and constrains the step <c>kind</c> / artifact-kind enums to the canonical
/// PascalCase wire spellings the analysis-content contract accepts.
/// </summary>
internal static class AnalysisGenerationSchema
{
    // Canonical PascalCase enum wire spellings (AnalysisPlanStepKind / ArtifactKind member names,
    // serialized member-name by UseStringEnumConverter).
    private const string StepKindEnum = "\"QueryFeatures\", \"Geoprocess\", \"Aggregate\", \"RenderMap\", \"Export\"";
    private const string ArtifactKindEnum = "\"Scalar\", \"FeatureLayer\", \"Table\", \"Raster\", \"File\", \"Report\", \"Map\", \"AppBundle\"";

    public static JsonElement Build(IProcessCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        // Emit each processId as a properly-escaped JSON string literal WITHOUT reflection (the app
        // disables reflection-based serialization). JsonEncodedText.Encode does the escaping; wrap in quotes.
        var processIds = catalog.ListProcesses();
        var processIdEnum = processIds.Count == 0
            ? "\"__no_methods_available__\""
            : string.Join(",", processIds.Select(p => "\"" + JsonEncodedText.Encode(p.ProcessId) + "\""));

        var json =
            $$"""
            {
              "type": "object",
              "properties": {
                "status": { "type": "string", "enum": ["generated", "needs-clarification", "unsupported", "refused"] },
                "rationale": { "type": "string" },
                "analysis": {
                  "type": ["object", "null"],
                  "properties": {
                    "intent": {
                      "type": ["object", "null"],
                      "properties": {
                        "intentId": { "type": "string" },
                        "goal": { "type": "string" },
                        "mode": { "type": ["string", "null"] },
                        "requestedOutputs": { "type": "array", "items": { "type": "string", "enum": [{{ArtifactKindEnum}}] } },
                        "inputs": { "type": "array", "items": { "type": "string" } }
                      },
                      "required": ["intentId", "goal"],
                      "additionalProperties": false
                    },
                    "plan": {
                      "type": "object",
                      "properties": {
                        "planId": { "type": "string" },
                        "intentId": { "type": "string" },
                        "steps": {
                          "type": "array",
                          "items": {
                            "type": "object",
                            "properties": {
                              "stepId": { "type": "string" },
                              "kind": { "type": "string", "enum": [{{StepKindEnum}}] },
                              "processId": { "type": ["string", "null"], "enum": [{{processIdEnum}}, null] },
                              "inputs": { "type": "object", "additionalProperties": { "type": "string" } },
                              "dependsOn": { "type": "array", "items": { "type": "string" } }
                            },
                            "required": ["stepId", "kind"],
                            "additionalProperties": false
                          }
                        },
                        "outputs": { "type": "array", "items": { "type": "string", "enum": [{{ArtifactKindEnum}}] } },
                        "warnings": { "type": "array", "items": { "type": "string" } }
                      },
                      "required": ["planId", "intentId", "steps"],
                      "additionalProperties": false
                    },
                    "parameters": { "type": "object", "additionalProperties": { "type": "string" } },
                    "requestedArtifacts": { "type": "array", "items": { "type": "string", "enum": [{{ArtifactKindEnum}}] } },
                    "spatialReferenceId": { "type": ["integer", "null"] },
                    "units": { "type": ["string", "null"] }
                  },
                  "required": ["plan"],
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
