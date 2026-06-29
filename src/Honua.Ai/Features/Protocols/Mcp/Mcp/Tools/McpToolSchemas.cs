// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Ai.Protocols.Mcp.Tools;

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

    private const string ProposeOperationArgumentSchemaJson = """
        {
          "type": "object",
          "required": ["kind"],
          "properties": {
            "kind": {
              "type": "string",
              "enum": ["AdminConfigChange", "Deploy", "MetadataRelease", "Seed"],
              "description": "In-scope mutating control-plane operation class to propose."
            },
            "reason": {
              "type": "string",
              "description": "Operator-facing reason or change note for the proposal."
            },
            "executionPayload": {
              "type": "string",
              "description": "Opaque, class-specific JSON execution payload replayed when the proposal is approved."
            },
            "idempotencyKey": {
              "type": "string",
              "description": "Stable idempotency key for the underlying operation."
            }
          }
        }
        """;

    private const string PublishServiceArgumentSchemaJson = """
        {
          "type": "object",
          "required": ["connectionId", "schema", "table", "layerName"],
          "properties": {
            "connectionId": {
              "type": "string",
              "description": "Secure-connection identifier whose table is published. Required: the canonical service.publish operation resolves the target database connection from it."
            },
            "schema": {
              "type": "string",
              "minLength": 1,
              "description": "Source schema containing the table to publish."
            },
            "table": {
              "type": "string",
              "minLength": 1,
              "description": "Source table to publish as a layer."
            },
            "layerName": {
              "type": "string",
              "minLength": 1,
              "description": "Display name for the published layer."
            },
            "serviceName": {
              "type": "string",
              "description": "Target service name. Defaults to the canonical default service when omitted."
            },
            "description": {
              "type": "string",
              "description": "Optional layer description."
            },
            "geometryColumn": {
              "type": "string",
              "description": "Geometry column to publish when the table has more than one."
            },
            "geometryType": {
              "type": "string",
              "description": "Geometry type override (for example Point, Polygon) when it cannot be inferred."
            },
            "srid": {
              "type": "integer",
              "description": "Spatial reference identifier for the published layer."
            },
            "primaryKey": {
              "type": "string",
              "description": "Primary key column when the table has no detectable key."
            },
            "fields": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Subset of attribute columns to publish. All columns are published when omitted."
            }
          }
        }
        """;

    // The geospatial-mcp create_map_package / create_app_package schemas mark NO
    // field required and set additionalProperties:true, so a standard-conformant
    // client composes a map/app from any subset of the composition selectors. The
    // live schemas mirror that (no required fields) and add the Honua prompt /
    // provider / model extension inputs the canonical generation pipeline drives.
    private const string CreateMapPackageArgumentSchemaJson = """
        {
          "type": "object",
          "additionalProperties": true,
          "properties": {
            "prompt": {
              "type": "string",
              "description": "Natural-language description of the map to build. Required by the generation pipeline; a missing prompt returns a structured invalid_argument."
            },
            "templateId": {
              "type": "string",
              "description": "Registry-defined MapTemplate identifier seeding the composition (e.g. analysis_default)."
            },
            "styleId": {
              "type": "string",
              "description": "StyleRef selection (style_…) applied to the composition."
            },
            "themeId": {
              "type": "string",
              "description": "ThemeSpec selection (theme_…) applied to the composition."
            },
            "initialView": {
              "type": "object",
              "additionalProperties": true,
              "description": "Initial view geometry: bounding box and CRS.",
              "properties": {
                "bbox": { "type": "array", "minItems": 4, "maxItems": 4, "items": { "type": "number" } },
                "crs": { "type": ["string", "integer"] }
              }
            },
            "provider": {
              "type": "string",
              "description": "Optional workflow-generation provider override (e.g. anthropic, bedrock, local)."
            },
            "model": {
              "type": "string",
              "description": "Optional model override for the selected provider."
            }
          }
        }
        """;

    private const string CreateAppPackageArgumentSchemaJson = """
        {
          "type": "object",
          "additionalProperties": true,
          "properties": {
            "prompt": {
              "type": "string",
              "description": "Natural-language description of the application to build. Required by the generation pipeline; a missing prompt returns a structured invalid_argument."
            },
            "templateId": {
              "type": "string",
              "description": "App-template registry identifier (surfaced through AppPackage.templateId)."
            },
            "targetSdk": {
              "type": "string",
              "description": "Target SDK declaration. V1 default is honua-sdk-js."
            },
            "mapPackageId": {
              "type": "string",
              "description": "MapPackage (map_…) bound into the application."
            },
            "boundArtifactIds": {
              "type": "array",
              "items": { "type": "string" },
              "description": "ArtifactRef identifiers (artifact_…) required at runtime."
            },
            "provider": {
              "type": "string",
              "description": "Optional workflow-generation provider override (e.g. anthropic, bedrock, local)."
            },
            "model": {
              "type": "string",
              "description": "Optional model override for the selected provider."
            }
          }
        }
        """;

    private const string PlanAnalysisArgumentSchemaJson = """
        {
          "type": "object",
          "required": ["intent"],
          "properties": {
            "intent": {
              "type": "string",
              "minLength": 1,
              "description": "Natural-language intent to compile into an analysis plan or canonical spec draft."
            },
            "context": {
              "type": "object",
              "description": "Optional caller context. Echoed back unchanged; fixture replay honors fixtureScenarioId or fixtureCase for duplicate-prompt selection."
            }
          }
        }
        """;

    private const string PackageReviewArgumentSchemaJson = """
        {
          "type": "object",
          "properties": {
            "contractVersion": {
              "type": "string",
              "description": "Package-review contract version. Current value is honua.package_review.v1."
            },
            "packageFamily": {
              "type": "string",
              "description": "Supported values are query, analysis_plan, map_package, dashboard_report, form, app_package, workflow, gp, and etl. Missing or unknown values are returned as canonical missing_package_family or unsupported_package_family findings rather than rejected by the schema."
            },
            "packageId": { "type": "string" },
            "requestedAction": { "type": "string" },
            "format": { "type": "string" },
            "packagePayload": {
              "type": ["object", "array", "string", "number", "boolean", "null"],
              "description": "Opaque package payload inspected only by the matching package-family adapter."
            },
            "requirements": {
              "type": ["object", "null"],
              "description": "Shared data binding, permission, schema, CRS, capability, dependency, and approval requirements. An explicit null is normalized to an empty requirement set."
            },
            "estimate": {
              "type": "object",
              "description": "Optional upstream estimate echoed in the shared package-review response."
            },
            "includePreviewPlan": { "type": "boolean" },
            "includePassFindings": { "type": "boolean" },
            "resourceRefs": {
              "type": ["object", "null"],
              "additionalProperties": { "type": "string" },
              "description": "Safe caller-visible resource references. An explicit null is normalized to an empty map."
            }
          }
        }
        """;

    /// <summary>
    /// Schema for the <c>honua_plan_analysis</c> tool input.
    /// </summary>
    public static readonly JsonElement PlanAnalysisArgumentSchema = Parse(PlanAnalysisArgumentSchemaJson);

    /// <summary>
    /// Schema for the <c>honua_create_map_package</c> tool input. Marks no field
    /// required to match the geospatial-mcp <c>create_map_package</c> standard
    /// schema (the composition is built from any subset of selectors); the
    /// generation pipeline reports a missing prompt as a structured finding.
    /// </summary>
    public static readonly JsonElement CreateMapPackageArgumentSchema = Parse(CreateMapPackageArgumentSchemaJson);

    /// <summary>
    /// Schema for the <c>honua_create_app_package</c> tool input. Marks no field
    /// required to match the geospatial-mcp <c>create_app_package</c> standard
    /// schema.
    /// </summary>
    public static readonly JsonElement CreateAppPackageArgumentSchema = Parse(CreateAppPackageArgumentSchemaJson);

    /// <summary>
    /// Schema for the package-review tools. The schema intentionally does not
    /// mark <c>packageFamily</c> as required or enum-limit it: the service
    /// reviews missing and unknown families and reports them as structured
    /// <c>missing_package_family</c>/<c>unsupported_package_family</c> findings,
    /// so a stricter published schema would let schema-driven clients block the
    /// very inputs these tools are meant to inspect. For the same reason
    /// <c>requirements</c> and <c>resourceRefs</c> permit <c>null</c>: the
    /// request model normalizes an explicit null to an empty set, so the
    /// published schema must not reject a value the service accepts.
    /// </summary>
    public static readonly JsonElement PackageReviewArgumentSchema = Parse(PackageReviewArgumentSchemaJson);

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
    /// Schema for <see cref="Models.McpProposeOperationArgument"/>.
    /// </summary>
    public static readonly JsonElement ProposeOperationArgumentSchema = Parse(ProposeOperationArgumentSchemaJson);

    /// <summary>
    /// Schema for <see cref="Models.McpPublishServiceArgument"/>. <c>connectionId</c>,
    /// <c>schema</c>, <c>table</c>, and <c>layerName</c> are marked required to
    /// match the <c>service.publish</c> executor, which throws on a missing
    /// connection id or required publish parameter at submit time — so a
    /// schema-driven client should never send a payload the operation always
    /// refuses.
    /// </summary>
    public static readonly JsonElement PublishServiceArgumentSchema = Parse(PublishServiceArgumentSchemaJson);

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
