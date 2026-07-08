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
/// These schemas are published on the descriptor under the standard MCP
/// <c>outputSchema</c> tool field, available in the 2025-06-18 revision this
/// server negotiates by default (honua-server#1954). Each describes the same
/// <c>result.structuredContent</c> payload the tool emits.
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
            "supportedKinds": {
              "type": ["array", "null"],
              "items": { "type": "string" },
              "description": "Operation classes with a genuinely registered executor (routable through the gateway); reported on every response, including rejections, so proposing an unsupported kind is never a silent dead end (#2563)."
            },
            "message": { "type": ["string", "null"] }
          }
        }
        """);

    /// <summary>
    /// Schema for <see cref="Models.McpPublishServiceOutput"/>. Projects the
    /// canonical <c>OperationHandle</c>: <c>status</c> is the
    /// <c>OperationHandleStatus</c> (Completed | Queued | RequiresApproval |
    /// Running | Failed); a completed publish carries <c>serviceUri</c>,
    /// <c>layerId</c>, and <c>metadataRevision</c>; a requires-approval outcome
    /// carries <c>approvalLane</c>; a queued outcome carries <c>jobId</c>.
    /// </summary>
    public static readonly JsonElement PublishServiceOutputSchema = Parse(
        """
        {
          "type": "object",
          "required": ["status", "requiresApproval", "operationId", "handleId"],
          "properties": {
            "status": {
              "type": "string",
              "description": "Operation handle status: Completed, Queued, Running, RequiresApproval, or Failed."
            },
            "requiresApproval": { "type": "boolean" },
            "operationId": { "type": "string" },
            "handleId": { "type": "string" },
            "serviceUri": {
              "type": ["string", "null"],
              "description": "honua://published-services/{serviceName} URI of the published service when the publish completed."
            },
            "layerId": { "type": ["string", "null"] },
            "serviceName": { "type": ["string", "null"] },
            "metadataRevision": {
              "type": ["integer", "null"],
              "description": "Metadata v2 graph revision produced by the publish."
            },
            "jobId": {
              "type": ["string", "null"],
              "description": "Durable job id when the operation was queued."
            },
            "approvalLane": {
              "type": ["string", "null"],
              "description": "Approval lane to wait on when the publish requires human approval."
            },
            "summary": { "type": ["string", "null"] },
            "message": { "type": ["string", "null"] }
          }
        }
        """);

    /// <summary>
    /// Schema for <see cref="Models.McpPublishResultOutput"/>. Projects the same
    /// canonical <c>OperationHandle</c> as the publish-service output (the
    /// promotion routes through <c>service.publish</c>): a completed promotion
    /// carries <c>serviceId</c> + <c>layerId</c> the agent chains straight into
    /// <c>honua_query_features</c> / <c>honua_render_map</c>; a requires-approval
    /// outcome carries <c>approvalLane</c>; a queued outcome carries <c>jobId</c>.
    /// <c>sourceJobId</c> / <c>artifactId</c> echo the promoted result.
    /// </summary>
    public static readonly JsonElement PublishResultOutputSchema = Parse(
        """
        {
          "type": "object",
          "required": ["status", "requiresApproval", "operationId", "handleId"],
          "properties": {
            "status": {
              "type": "string",
              "description": "Operation handle status: Completed, Queued, Running, RequiresApproval, Denied, or Failed."
            },
            "requiresApproval": { "type": "boolean" },
            "operationId": { "type": "string" },
            "handleId": { "type": "string" },
            "sourceJobId": {
              "type": ["string", "null"],
              "description": "The completed analysis job id whose artifact was promoted."
            },
            "artifactId": {
              "type": ["string", "null"],
              "description": "The result artifact that was promoted."
            },
            "serviceUri": {
              "type": ["string", "null"],
              "description": "honua://published-services/{serviceId} URI of the published service when the promotion completed."
            },
            "serviceId": {
              "type": ["string", "null"],
              "description": "Published service id (name) — pass to honua_query_features / honua_render_map as serviceId."
            },
            "layerId": {
              "type": ["string", "null"],
              "description": "Published layer id — pass to honua_query_features / honua_render_map as layerId."
            },
            "metadataRevision": {
              "type": ["integer", "null"],
              "description": "Metadata v2 graph revision produced by the promotion."
            },
            "jobId": {
              "type": ["string", "null"],
              "description": "Durable job id when the promotion was queued."
            },
            "approvalLane": {
              "type": ["string", "null"],
              "description": "Approval lane to wait on when the promotion requires human approval."
            },
            "summary": { "type": ["string", "null"] },
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
          "required": ["status", "engine"],
          "properties": {
            "status": { "type": "string" },
            "engine": {
              "type": "string",
              "enum": ["live", "fixture"],
              "description": "Which planner produced this response. engine:\"live\" means the plan was compiled from your intent by a provider-backed model; engine:\"fixture\" means the plan is a canned deterministic template returned because no LLM provider is configured - treat it as a capability demo, not a plan compiled from your intent."
            },
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
    /// schema pins the top-level envelope and leaves those sub-objects open. On a
    /// clarification turn exactly one of <c>clarification</c> (proprietary
    /// envelope) or <c>elicitation</c> (MCP-native elicitation request, emitted
    /// when the session advertised the elicitation capability; honua-server#2484)
    /// is populated.
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
            "elicitation": { "type": ["object", "null"] },
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

    /// <summary>Schema for <c>McpGeocodeAddressesOutput</c>.</summary>
    public static readonly JsonElement GeocodeAddressesOutputSchema = Parse(
        """
        {
          "type": "object",
          "required": ["results", "succeeded", "failed", "srid"],
          "properties": {
            "results": {
              "type": "array",
              "description": "One entry per input address, in the same order as the request.",
              "items": {
                "type": "object",
                "required": ["input", "ok"],
                "properties": {
                  "input": { "type": "string" },
                  "ok": { "type": "boolean" },
                  "location": {
                    "type": ["object", "null"],
                    "properties": {
                      "x": { "type": "number", "description": "Longitude (X) ordinate." },
                      "y": { "type": "number", "description": "Latitude (Y) ordinate." },
                      "srid": { "type": "integer" }
                    }
                  },
                  "score": { "type": ["number", "null"] },
                  "matchedAddress": { "type": ["string", "null"] },
                  "matchLevel": { "type": ["string", "null"] },
                  "provider": { "type": ["string", "null"] },
                  "error": { "type": ["string", "null"] }
                }
              }
            },
            "succeeded": { "type": "integer" },
            "failed": { "type": "integer" },
            "srid": { "type": "integer" }
          }
        }
        """);

    /// <summary>
    /// Schema for <see cref="Models.McpIngestDatasetOutput"/>. A successful
    /// ingest carries the connectionId/schema/table triple (plus geometry column
    /// and primary key) that chains directly into <c>honua_publish_service</c>;
    /// per-row issues (e.g. addresses that failed to geocode) ride alongside
    /// without failing the ingest.
    /// </summary>
    public static readonly JsonElement IngestDatasetOutputSchema = Parse(
        """
        {
          "type": "object",
          "required": ["success", "datasetName", "rowCount"],
          "properties": {
            "success": { "type": "boolean" },
            "datasetName": { "type": "string" },
            "rowCount": { "type": "integer" },
            "connectionId": {
              "type": ["string", "null"],
              "description": "Registered secure-connection name/id for the catalog database that owns the imported table; pass to honua_publish_service. Null when no registered connection matches the catalog database."
            },
            "schema": { "type": ["string", "null"], "description": "Schema that owns the imported table." },
            "table": { "type": ["string", "null"], "description": "Physical table name to publish." },
            "srid": { "type": ["integer", "null"] },
            "geometryColumn": { "type": ["string", "null"] },
            "primaryKey": { "type": ["string", "null"] },
            "warnings": { "type": "array", "items": { "type": "string" } },
            "rowErrors": {
              "type": "array",
              "items": {
                "type": "object",
                "required": ["code", "message"],
                "properties": {
                  "row": { "type": ["integer", "null"], "description": "1-based data row (header excluded), when known." },
                  "code": { "type": "string" },
                  "message": { "type": "string" },
                  "field": { "type": ["string", "null"] }
                }
              }
            },
            "errorCode": { "type": ["string", "null"] },
            "errorMessage": { "type": ["string", "null"] }
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
          "required": ["serviceId", "layerId", "returnedCount", "limit", "resultOffset", "exceededTransferLimit"],
          "properties": {
            "serviceId": { "type": "string" },
            "layerId": { "type": "integer" },
            "returnedCount": { "type": "integer" },
            "limit": { "type": "integer" },
            "resultOffset": {
              "type": "integer",
              "description": "The resultOffset applied to this request (defaults to 0)."
            },
            "exceededTransferLimit": {
              "type": "boolean",
              "description": "True when more matching features exist beyond this page. When true, re-issue the same query with resultOffset=nextOffset to fetch the next page."
            },
            "nextOffset": {
              "type": "integer",
              "description": "Present only when exceededTransferLimit is true: the resultOffset to send on the next request (resultOffset + returnedCount) to page mechanically. Absent on the last page."
            },
            "count": {
              "type": "integer",
              "description": "Matching feature count. Present only when returnCountOnly=true; features are omitted in that mode."
            },
            "geojson": {
              "type": "object",
              "description": "RFC 7946 GeoJSON FeatureCollection. Omitted when returnCountOnly=true. When returnGeometry=false each feature's geometry is null.",
              "properties": {
                "type": { "type": "string", "const": "FeatureCollection" },
                "features": { "type": "array", "items": { "type": "object" } }
              }
            }
          }
        }
        """);

    /// <summary>
    /// Schema for the <c>honua_create_map_package</c> result (a
    /// <c>MapGenerationResult</c>). Pins the top-level envelope (status, package,
    /// rationale, clarifications, validation, capabilityState, provider, model)
    /// and leaves the deep package/validation sub-objects open so the published
    /// contract stays stable as the generation engine evolves.
    /// </summary>
    public static readonly JsonElement CreateMapPackageOutputSchema = Parse(
        """
        {
          "type": "object",
          "required": ["status"],
          "properties": {
            "status": {
              "type": "string",
              "description": "generated, needs_clarification, invalid, or capability_unavailable."
            },
            "package": { "type": ["object", "null"], "description": "The generated MapPackage when status is generated." },
            "rationale": { "type": ["string", "null"] },
            "clarifications": { "type": "array", "items": { "type": "object" } },
            "validation": { "type": ["object", "null"] },
            "unmappedRequests": { "type": "array", "items": { "type": "string" } },
            "capabilityState": { "type": ["object", "null"] },
            "provider": { "type": ["string", "null"] },
            "model": { "type": ["string", "null"] }
          }
        }
        """);

    /// <summary>
    /// Schema for the <c>honua_create_app_package</c> result (an
    /// <c>AppGenerationResult</c>). Mirrors <see cref="CreateMapPackageOutputSchema"/>;
    /// the <c>package</c> is the opaque studio-app/v1 body.
    /// </summary>
    public static readonly JsonElement CreateAppPackageOutputSchema = Parse(
        """
        {
          "type": "object",
          "required": ["status"],
          "properties": {
            "status": {
              "type": "string",
              "description": "generated, needs_clarification, invalid, or capability_unavailable."
            },
            "package": { "type": ["object", "null"], "description": "The generated studio-app/v1 AppPackage body when status is generated." },
            "rationale": { "type": ["string", "null"] },
            "clarifications": { "type": "array", "items": { "type": "object" } },
            "validation": { "type": ["object", "null"] },
            "unmappedRequests": { "type": "array", "items": { "type": "string" } },
            "capabilityState": { "type": ["object", "null"] },
            "provider": { "type": ["string", "null"] },
            "model": { "type": ["string", "null"] }
          }
        }
        """);

    /// <summary>
    /// Schema for <c>McpGetStyleOutput</c>. Resolve mode carries the StyleRef
    /// projection (styleId/title/encodings); list mode carries the styles
    /// discovery catalog. Only one field set is populated per call.
    /// </summary>
    public static readonly JsonElement GetStyleOutputSchema = Parse(
        """
        {
          "type": "object",
          "properties": {
            "styleId": { "type": ["string", "null"] },
            "title": { "type": ["string", "null"] },
            "description": { "type": ["string", "null"] },
            "styleVersion": { "type": ["integer", "null"] },
            "encodings": {
              "type": ["array", "null"],
              "description": "Advertised encodings for the resolved style (resolve mode).",
              "items": {
                "type": "object",
                "properties": {
                  "encoding": { "type": "string", "description": "mapbox-style, esri-drawing-info, sld-1.0.0, or sld-1.1.0." },
                  "mediaType": { "type": "string" },
                  "inlineBody": { "type": ["string", "null"], "description": "Inlined stylesheet body for the selected encoding, when includeStylesheet=true." },
                  "storageRef": { "type": ["string", "null"], "description": "honua://styles/{styleId} reference when the encoding is advertised by reference." }
                }
              }
            },
            "styles": {
              "type": ["array", "null"],
              "description": "Discovery catalog of available styles (list mode).",
              "items": {
                "type": "object",
                "properties": {
                  "styleId": { "type": "string" },
                  "title": { "type": ["string", "null"] },
                  "uri": { "type": "string" }
                }
              }
            }
          }
        }
        """);

    /// <summary>Schema for <c>McpApplyStylePresetOutput</c>.</summary>
    public static readonly JsonElement ApplyStylePresetOutputSchema = Parse(
        """
        {
          "type": "object",
          "required": ["serviceId", "layerId", "styleId", "applied"],
          "properties": {
            "serviceId": { "type": "string" },
            "layerId": { "type": "integer" },
            "styleId": { "type": "string", "description": "The preset now bound as the layer's primary/default style." },
            "title": { "type": ["string", "null"] },
            "styleVersion": { "type": "integer" },
            "applied": { "type": "boolean" }
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

    /// <summary>
    /// Schema for <see cref="Models.McpOperationToolOutput"/> — the structured
    /// result of a published operations-toolset operation projected as a
    /// first-class MCP tool (#2483). Projects the canonical <c>OperationHandle</c>
    /// plus the determinism/cache provenance the published-tool contract adds.
    /// </summary>
    public static readonly JsonElement OperationToolOutputSchema = Parse(
        """
        {
          "type": "object",
          "required": ["status", "requiresApproval", "deterministic", "cacheHit", "operationId", "handleId"],
          "properties": {
            "status": {
              "type": "string",
              "description": "Operation handle status: Completed, Queued, Running, RequiresApproval, DryRunRequired, Denied, or Failed."
            },
            "requiresApproval": { "type": "boolean" },
            "deterministic": {
              "type": "boolean",
              "description": "Whether the backing operation descriptor is deterministic (AI-free)."
            },
            "cacheHit": {
              "type": "boolean",
              "description": "True when served from the param-keyed deterministic cache instead of re-executed."
            },
            "cacheKey": {
              "type": ["string", "null"],
              "description": "Param-keyed cache key (operation id + catalog version + normalized parameters) for a deterministic, read-only invocation; null when not cacheable."
            },
            "operationId": { "type": "string" },
            "handleId": { "type": "string" },
            "jobId": {
              "type": ["string", "null"],
              "description": "Durable job id when the operation was queued."
            },
            "approvalLane": {
              "type": ["string", "null"],
              "description": "Approval lane to wait on when the operation requires human approval."
            },
            "metadataRevision": {
              "type": ["integer", "null"],
              "description": "Metadata v2 graph revision produced by the operation, when it mutated the graph."
            },
            "summary": { "type": ["string", "null"] },
            "message": { "type": ["string", "null"] },
            "details": {
              "type": "object",
              "additionalProperties": { "type": "string" },
              "description": "Operation-specific result detail values keyed by name."
            }
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
