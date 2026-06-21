// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// JSON-schema documents describing the structured result of each MCP tool.
/// Mirrors <see cref="McpToolSchemas"/> (input schemas): schemas are immutable
/// <see cref="JsonElement"/> values parsed at type-load time so the MCP surface
/// stays AOT-safe, and the artifact-kind enum is sourced from the canonical
/// <see cref="ArtifactKind"/> so the published contract cannot drift from the
/// domain types the tools serialize.
/// </summary>
/// <remarks>
/// The MCP revision this server advertises (2025-03-26) predates the standard
/// <c>outputSchema</c> tool field (added in 2025-06-18). Each schema here is
/// published on the descriptor under the Honua extension key
/// <c>structuredContentSchema</c> and describes the same
/// <c>result.structuredContent</c> payload <c>outputSchema</c> would. The
/// revision bump that lets these move to the standard <c>outputSchema</c> key is
/// tracked in honua-server#1954.
/// </remarks>
internal static class McpToolOutputSchemas
{
    private const string ValidationViolationDef = """
        {
          "type": "object",
          "required": ["code", "message"],
          "properties": {
            "code": { "type": "string" },
            "message": { "type": "string" },
            "fieldPath": { "type": ["string", "null"] }
          }
        }
        """;

    /// <summary>Schema for <see cref="Models.McpValidatePlanOutput"/>.</summary>
    public static readonly JsonElement ValidatePlanOutputSchema = Parse(
        $$"""
        {
          "type": "object",
          "required": ["isExecutable", "requiresApproval", "violations", "warnings"],
          "properties": {
            "isExecutable": { "type": "boolean" },
            "requiresApproval": { "type": "boolean" },
            "violations": { "type": "array", "items": {{ValidationViolationDef}} },
            "warnings": { "type": "array", "items": { "type": "string" } }
          }
        }
        """);

    /// <summary>Schema for <see cref="Models.McpDryRunOutput"/>.</summary>
    public static readonly JsonElement DryRunOutputSchema = Parse(
        $$"""
        {
          "type": "object",
          "required": ["estimatedDurationSeconds", "estimatedArtifacts", "sideEffects"],
          "properties": {
            "estimatedDurationSeconds": { "type": "number" },
            "estimatedArtifacts": {
              "type": "array",
              "items": { "type": "string", "enum": {{ArtifactKindEnum}} }
            },
            "sideEffects": { "type": "array", "items": { "type": "string" } }
          }
        }
        """);

    /// <summary>Schema for <see cref="Models.McpExecuteOutput"/>.</summary>
    public static readonly JsonElement ExecuteOutputSchema = Parse(
        """
        {
          "type": "object",
          "required": ["jobId", "status", "createdAt", "resourceUri"],
          "properties": {
            "jobId": { "type": "string" },
            "status": { "type": "string" },
            "createdAt": { "type": "string", "format": "date-time" },
            "resourceUri": {
              "type": "string",
              "description": "honua://jobs/{jobId} resource URI for polling lifecycle state via resources/read."
            }
          }
        }
        """);

    /// <summary>Schema for <see cref="Models.McpCancelJobOutput"/>.</summary>
    public static readonly JsonElement CancelJobOutputSchema = Parse(
        """
        {
          "type": "object",
          "required": ["jobId", "status", "cancellationRequested"],
          "properties": {
            "jobId": { "type": "string" },
            "status": { "type": "string" },
            "cancellationRequested": { "type": "boolean" }
          }
        }
        """);

    /// <summary>Schema for <see cref="Models.McpProposeOperationOutput"/>.</summary>
    public static readonly JsonElement ProposeOperationOutputSchema = Parse(
        """
        {
          "type": "object",
          "required": ["outcome", "requiresApproval"],
          "properties": {
            "outcome": { "type": "string" },
            "requiresApproval": { "type": "boolean" },
            "proposalId": { "type": ["string", "null"] },
            "resourceUri": {
              "type": ["string", "null"],
              "description": "honua://proposals/{proposalId} resource URI to poll when human approval is required."
            },
            "executionOperationId": { "type": ["string", "null"] },
            "message": { "type": ["string", "null"] }
          }
        }
        """);

    /// <summary>
    /// Schema for <see cref="Models.McpPlanAnalysisOutput"/>. The compiled plan,
    /// spec draft, clarification, and estimate are deep nested shapes; the schema
    /// pins the top-level envelope and leaves those sub-objects open so the
    /// published contract stays stable as the planning engine evolves.
    /// </summary>
    public static readonly JsonElement PlanAnalysisOutputSchema = Parse(
        """
        {
          "type": "object",
          "required": ["status"],
          "properties": {
            "status": { "type": "string" },
            "plan": { "type": ["object", "null"] },
            "specDraft": { "type": ["object", "null"] },
            "warnings": { "type": "array", "items": { "type": "object" } },
            "cache": { "type": ["object", "null"] },
            "capabilityState": { "type": ["object", "null"] },
            "clarification": { "type": ["object", "null"] },
            "estimate": { "type": ["object", "null"] },
            "fixtureCase": { "type": ["string", "null"] }
          }
        }
        """);

    /// <summary>
    /// Schema for <c>McpGroundingOutput</c> (shared by honua_ground_candidates
    /// and honua_clarify_intent). The classification, draft intent, candidate
    /// ranking, and optional clarification envelope are deep nested shapes; the
    /// schema pins the top-level envelope and leaves those sub-objects open.
    /// </summary>
    public static readonly JsonElement GroundingOutputSchema = Parse(
        """
        {
          "type": "object",
          "required": ["workflowFamily", "draftIntent", "candidates", "engine"],
          "properties": {
            "workflowFamily": { "type": "object" },
            "draftIntent": { "type": "object" },
            "candidates": { "type": "object" },
            "clarification": { "type": ["object", "null"] },
            "engine": { "type": "string" }
          }
        }
        """);

    /// <summary>
    /// Schema for <c>PackageReviewResponse</c> (shared by honua_validate_package
    /// and honua_preview_package). The findings, requirements, estimate, and
    /// optional preview plan are deep nested shapes; the schema pins the
    /// top-level envelope and leaves those sub-objects open.
    /// </summary>
    public static readonly JsonElement PackageReviewOutputSchema = Parse(
        """
        {
          "type": "object",
          "properties": {
            "contractVersion": { "type": ["string", "null"] },
            "packageFamily": { "type": ["string", "null"] },
            "packageId": { "type": ["string", "null"] },
            "decision": { "type": ["string", "null"] },
            "findings": { "type": "array", "items": { "type": "object" } },
            "previewPlan": { "type": ["object", "null"] },
            "estimate": { "type": ["object", "null"] }
          }
        }
        """);

    /// <summary>Schema for <c>McpGeocodeOutput</c>.</summary>
    public static readonly JsonElement GeocodeOutputSchema = Parse(
        """
        {
          "type": "object",
          "required": ["provider", "candidates"],
          "properties": {
            "provider": { "type": "string" },
            "candidates": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "address": { "type": ["string", "null"] },
                  "x": { "type": "number" },
                  "y": { "type": "number" },
                  "score": { "type": "number" },
                  "srid": { "type": "integer" },
                  "matchLevel": { "type": ["string", "null"] },
                  "addressType": { "type": ["string", "null"] }
                }
              }
            }
          }
        }
        """);

    /// <summary>Schema for <c>McpRouteOutput</c>.</summary>
    public static readonly JsonElement RouteOutputSchema = Parse(
        """
        {
          "type": "object",
          "required": ["provider", "solved", "totalLengthMeters", "totalTimeMinutes", "directions"],
          "properties": {
            "provider": { "type": "string" },
            "solved": { "type": "boolean" },
            "routeGeometryGeoJson": { "type": ["string", "null"] },
            "totalLengthMeters": { "type": "number" },
            "totalTimeMinutes": { "type": "number" },
            "directions": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "text": { "type": "string" },
                  "lengthMeters": { "type": "number" },
                  "timeMinutes": { "type": "number" },
                  "maneuverType": { "type": "string" }
                }
              }
            }
          }
        }
        """);

    /// <summary>Schema for <c>McpListLayersOutput</c>.</summary>
    public static readonly JsonElement ListLayersOutputSchema = Parse(
        """
        {
          "type": "object",
          "required": ["layerCount", "layers"],
          "properties": {
            "layerCount": { "type": "integer" },
            "layers": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "serviceId": { "type": "string" },
                  "serviceName": { "type": "string" },
                  "layerId": { "type": "integer" },
                  "name": { "type": "string" },
                  "type": { "type": "string" },
                  "geometryType": { "type": "string" },
                  "srid": { "type": ["integer", "null"] },
                  "extent": { "type": ["object", "null"] },
                  "description": { "type": ["string", "null"] }
                }
              }
            }
          }
        }
        """);

    /// <summary>Schema for <c>McpQueryFeaturesOutput</c>.</summary>
    public static readonly JsonElement QueryFeaturesOutputSchema = Parse(
        """
        {
          "type": "object",
          "required": ["serviceId", "layerId", "returnedCount", "limit", "exceededTransferLimit", "geojson"],
          "properties": {
            "serviceId": { "type": "string" },
            "layerId": { "type": "integer" },
            "returnedCount": { "type": "integer" },
            "limit": { "type": "integer" },
            "exceededTransferLimit": { "type": "boolean" },
            "geojson": {
              "type": "object",
              "description": "RFC 7946 GeoJSON FeatureCollection.",
              "properties": {
                "type": { "type": "string", "const": "FeatureCollection" },
                "features": { "type": "array", "items": { "type": "object" } }
              }
            }
          }
        }
        """);

    /// <summary>Schema for <see cref="Models.McpNotImplementedOutput"/>.</summary>
    public static readonly JsonElement NotImplementedOutputSchema = Parse(
        """
        {
          "type": "object",
          "required": ["status", "tool", "blockedBy", "contract", "nextSteps"],
          "properties": {
            "status": { "type": "string", "const": "not_implemented" },
            "tool": { "type": "string" },
            "blockedBy": { "type": "string" },
            "contract": { "type": "string" },
            "nextSteps": { "type": "array", "items": { "type": "string" } }
          }
        }
        """);

    private static string ArtifactKindEnum => JsonStringArray(Enum.GetNames<ArtifactKind>());

    private static string JsonStringArray(string[] values)
    {
        var builder = new StringBuilder(2 + values.Length * 16);
        builder.Append('[');
        for (var i = 0; i < values.Length; i++)
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
