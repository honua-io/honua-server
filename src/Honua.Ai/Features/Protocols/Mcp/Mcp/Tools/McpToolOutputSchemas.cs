// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Ai.Protocols.Mcp.Tools;

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

    private const string ToolErrorBranch = """
        {
          "required": ["status", "code", "message", "error"]
        }
        """;

    private const string ToolErrorRequiredProperties = """
            "status": { "type": "string", "const": "error" },
            "code": { "type": "string" },
            "message": { "type": "string" },
            "studioAuthorizationCode": { "type": ["string", "null"] },
            "currentGeneration": { "type": ["integer", "null"], "minimum": 1 },
            "error": { "type": "object" }
        """;

    private static string ToolErrorSchema => $$"""
        {
          "type": "object",
          "required": ["status", "code", "message", "error"],
          "properties": {
            {{ToolErrorRequiredProperties}}
          }
        }
        """;

    private static string ToolErrorProperties => $$"""
            "status": { "type": "string", "const": "error" },
            "code": { "type": "string" },
            "message": { "type": "string" },
            "requiresReauthentication": { "type": ["boolean", "null"] },
            "approvalRequired": { "type": ["boolean", "null"] },
            "policyRef": { "type": ["string", "null"] },
            "studioAuthorizationCode": { "type": ["string", "null"] },
            "conflictingJobId": { "type": ["string", "null"] },
            "currentGeneration": { "type": ["integer", "null"], "minimum": 1 },
            "retryable": { "type": ["boolean", "null"] },
            "retryAfterSeconds": { "type": ["integer", "null"], "minimum": 0 },
            "violations": {
              "type": ["array", "null"],
              "items": {{ValidationViolationDef}}
            },
            "error": {
              "type": "object",
              "required": ["kind", "message"],
              "properties": {
                "kind": { "type": "string" },
                "message": { "type": "string" },
                "stepId": { "type": ["string", "null"] },
                "violations": {
                  "type": ["array", "null"],
                  "items": {{ValidationViolationDef}}
                }
              }
            }
        """;

    // MCP clients validate structuredContent against outputSchema for failed calls too, so every advertised schema must accept the shared error envelope.
    internal static JsonElement IncludeErrorEnvelope(JsonElement successSchema)
    {
        if (AcceptsErrorEnvelope(successSchema))
        {
            return successSchema;
        }

        var root = JsonNode.Parse(successSchema.GetRawText())?.AsObject()
            ?? throw new InvalidOperationException("MCP tool output schemas must be JSON objects.");
        var errorRoot = JsonNode.Parse(ToolErrorSchema)!.AsObject();
        var errorRequired = errorRoot["required"]!.DeepClone();
        if (root["properties"] is not JsonObject successProperties)
        {
            throw new InvalidOperationException("MCP tool output schemas must declare object properties.");
        }

        var errorProperties = errorRoot["properties"]!.AsObject();
        var successPropertyNames = successProperties.Select(property => property.Key).ToArray();
        var successOverlapProperties = new JsonObject();
        var errorOverlapProperties = new JsonObject();
        foreach (var errorProperty in errorProperties)
        {
            if (successProperties[errorProperty.Key] is JsonNode successProperty)
            {
                successOverlapProperties[errorProperty.Key] = successProperty.DeepClone();
                errorOverlapProperties[errorProperty.Key] = errorProperty.Value?.DeepClone();
                successProperties[errorProperty.Key] = new JsonObject();
                continue;
            }

            successProperties[errorProperty.Key] = errorProperty.Value?.DeepClone();
        }

        var closedSuccessSchema = root["additionalProperties"]?.GetValue<bool>() is false;
        root.Remove("additionalProperties");
        var successRequired = root["required"]?.DeepClone();
        root.Remove("required");
        var successBranch = new JsonObject
        {
            ["not"] = new JsonObject { ["required"] = errorRequired.DeepClone() }
        };
        if (successRequired is not null)
        {
            successBranch["required"] = successRequired;
        }

        if (closedSuccessSchema)
        {
            var allowedSuccessProperties = new JsonObject();
            foreach (var propertyName in successPropertyNames)
            {
                allowedSuccessProperties[propertyName] =
                    successOverlapProperties[propertyName]?.DeepClone() ?? new JsonObject();
            }

            successBranch["properties"] = allowedSuccessProperties;
            successBranch["additionalProperties"] = false;
        }
        else if (successOverlapProperties.Count > 0)
        {
            successBranch["properties"] = successOverlapProperties;
        }

        var errorBranch = new JsonObject { ["required"] = errorRequired };
        if (errorOverlapProperties.Count > 0)
        {
            errorBranch["properties"] = errorOverlapProperties;
        }

        root["oneOf"] = new JsonArray(successBranch, errorBranch);

        return Parse(root.ToJsonString());
    }

    private static JsonElement IncludeApprovalStatus(JsonElement completedSchema)
    {
        var root = JsonNode.Parse(IncludeErrorEnvelope(completedSchema).GetRawText())!.AsObject();
        var props = root["properties"]!.AsObject();
        props["status"] = JsonNode.Parse("""{"type":"string"}""");
        props["operationId"] ??= JsonNode.Parse("""{"type":"string"}""");
        props["proposalId"] ??= JsonNode.Parse("""{"type":"string"}""");
        props["jobId"] ??= JsonNode.Parse("""{"type":["string","null"]}""");
        var branches = root["oneOf"]!.AsArray();
        var success = branches[0]!.AsObject();
        var error = branches[1]!.AsObject();
        var notError = success["not"]!.DeepClone();
        success.Remove("not");
        var approval = JsonNode.Parse(
            """{"required":["status","operationId","proposalId"],"properties":{"status":{"enum":["RequiresApproval","Running","Rejected","Cancelled","Failed","Indeterminate"]}}}""")!;
        branches[0] = new JsonObject
        {
            ["not"] = notError,
            ["anyOf"] = new JsonArray(success.DeepClone(), approval),
        };
        if (error["properties"] is not JsonObject)
        {
            error["properties"] = JsonNode.Parse("""{"status":{"const":"error"}}""")!;
        }

        return Parse(root.ToJsonString());
    }

    private static bool AcceptsErrorEnvelope(JsonElement schema)
    {
        if (!schema.TryGetProperty("oneOf", out var branches)
            || branches.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var branch in branches.EnumerateArray())
        {
            if (!branch.TryGetProperty("required", out var required)
                || required.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var names = required.EnumerateArray()
                .Select(value => value.GetString())
                .ToHashSet(StringComparer.Ordinal);
            if (names.IsSupersetOf(["status", "code", "message", "error"]))
            {
                return true;
            }
        }

        return false;
    }

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

    public static readonly JsonElement DryRunOutputSchema = Parse(
        $$"""
        {
          "type": "object",
          "required": ["estimatedDurationSeconds", "durationEstimateAvailable", "estimatedArtifacts", "sideEffects"],
          "properties": {
            "estimatedDurationSeconds": { "type": "number" },
            "durationEstimateAvailable": {
              "type": "boolean",
              "description": "False means estimatedDurationSeconds is a placeholder, not a runtime prediction."
            },
            "estimatedArtifacts": {
              "type": "array",
              "items": { "type": "string", "enum": {{ArtifactKindEnum}} }
            },
            "sideEffects": { "type": "array", "items": { "type": "string" } }
          }
        }
        """);

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
              "description": "Job resource URI."
            }
          }
        }
        """);

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
              "description": "Proposal resource URI."
            },
            "executionOperationId": { "type": ["string", "null"] },
            "supportedKinds": {
              "type": ["array", "null"],
              "items": { "type": "string" },
              "description": "Routable operation classes with a registered executor."
            },
            "message": { "type": ["string", "null"] },
            "result": {
              "type": ["object", "null"],
              "description": "Planning result."
            },
            "targets": {
              "type": ["array", "null"],
              "items": {
                "type": "object",
                "required": ["targetId", "outcome"],
                "properties": {
                  "targetId": { "type": "string" },
                  "outcome": { "type": "string" },
                  "proposalId": { "type": ["string", "null"] },
                  "message": { "type": ["string", "null"] }
                }
              }
            },
            "code": {
              "type": ["string", "null"],
              "description": "Rejection code."
            },
            "declaredVersion": { "type": ["string", "null"] },
            "targetVersion": { "type": ["string", "null"] },
            "servingArtifactReference": { "type": ["string", "null"] },
            "isCoVersioned": { "type": ["boolean", "null"] }
          }
        }
        """);

    public static readonly JsonElement PublishServiceOutputSchema = Parse(
        """
        {
          "type": "object",
          "required": ["status", "requiresApproval", "operationId", "operationInstanceId", "handleId", "correlationId", "createdAt", "updatedAt"],
          "properties": {
            "status": {
              "type": "string",
              "description": "Operation handle status."
            },
            "requiresApproval": { "type": "boolean" },
            "operationId": { "type": "string" },
            "operationInstanceId": { "type": "string" },
            "handleId": { "type": "string" },
            "durable": {
              "type": "boolean",
              "description": "False when the direct publish handle was not durably persisted and cannot be polled."
            },
            "proposalId": { "type": ["string", "null"] },
            "correlationId": { "type": "string" },
            "auditId": { "type": ["string", "null"] },
            "createdAt": { "type": "string", "format": "date-time" },
            "updatedAt": { "type": "string", "format": "date-time" },
            "authorizationOutcome": { "type": ["string", "null"] },
            "policyOutcome": { "type": ["string", "null"] },
            "serviceUri": {
              "type": ["string", "null"],
              "description": "honua://published-services/{serviceName} URI on completion."
            },
            "storageLayerId": {
              "type": ["string", "null"],
              "description": "Storage layer id."
            },
            "serviceLayerIndex": {
              "type": ["string", "null"],
              "description": "Service-local layer index."
            },
            "serviceName": { "type": ["string", "null"] },
            "metadataRevision": {
              "type": ["integer", "null"],
              "description": "Resulting metadata revision."
            },
            "jobId": {
              "type": ["string", "null"],
              "description": "Queued job id."
            },
            "approvalLane": {
              "type": ["string", "null"],
              "description": "Approval lane when approval is required."
            },
            "summary": { "type": ["string", "null"] },
            "message": { "type": ["string", "null"] },
            "details": { "type": "object", "additionalProperties": { "type": "string" } },
            "resourceIds": { "type": "object", "additionalProperties": { "type": "string" } },
            "evidenceRefs": { "type": "array", "items": { "type": "string" } }
          }
        }
        """);

    public static readonly JsonElement PublishResultOutputSchema = Parse(
        """
        {
          "type": "object",
          "required": ["status", "requiresApproval", "operationId", "operationInstanceId", "handleId", "correlationId", "createdAt", "updatedAt"],
          "properties": {
            "status": {
              "type": "string",
              "description": "Operation handle status."
            },
            "requiresApproval": { "type": "boolean" },
            "operationId": { "type": "string" },
            "operationInstanceId": { "type": "string" },
            "handleId": { "type": "string" },
            "proposalId": { "type": ["string", "null"] },
            "correlationId": { "type": "string" },
            "auditId": { "type": ["string", "null"] },
            "createdAt": { "type": "string", "format": "date-time" },
            "updatedAt": { "type": "string", "format": "date-time" },
            "authorizationOutcome": { "type": ["string", "null"] },
            "policyOutcome": { "type": ["string", "null"] },
            "sourceJobId": {
              "type": ["string", "null"],
              "description": "Source job id."
            },
            "artifactId": {
              "type": ["string", "null"],
              "description": "Promoted artifact id."
            },
            "serviceUri": {
              "type": ["string", "null"],
              "description": "honua://published-services/{serviceId} URI on completion."
            },
            "serviceId": {
              "type": ["string", "null"],
              "description": "Published serviceId."
            },
            "storageLayerId": {
              "type": ["string", "null"],
              "description": "Storage layer id."
            },
            "serviceLayerIndex": {
              "type": ["string", "null"],
              "description": "Service-local layer index."
            },
            "metadataRevision": {
              "type": ["integer", "null"],
              "description": "Resulting metadata revision."
            },
            "jobId": {
              "type": ["string", "null"],
              "description": "Durable job id when the promotion was queued."
            },
            "approvalLane": {
              "type": ["string", "null"],
              "description": "Approval lane when approval is required."
            },
            "summary": { "type": ["string", "null"] },
            "message": { "type": ["string", "null"] },
            "details": { "type": "object", "additionalProperties": { "type": "string" } },
            "resourceIds": { "type": "object", "additionalProperties": { "type": "string" } },
            "evidenceRefs": { "type": "array", "items": { "type": "string" } }
          }
        }
        """);

    // Nested planning objects stay open so the published contract remains stable as the planning engine evolves.
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
              "description": "Planner engine: live (LLM) or fixture (canned capability demo when no LLM is configured)."
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

    public static readonly JsonElement PackageReviewOutputSchema = Parse(
        """
        {
          "type": "object",
          "properties": {
            "contractVersion": { "type": ["string", "null"] },
            "packageFamily": { "type": ["string", "null"] },
            "packageId": { "type": ["string", "null"] },
            "decision": { "type": ["string", "null"] },
            "status": {
              "type": "string",
              "enum": ["ready", "warning", "blocked"],
              "description": "Review outcome. Distinct from the structured error envelope status \"error\"."
            },
            "findings": { "type": "array", "items": { "type": "object" } },
            "previewPlan": { "type": ["object", "null"] },
            "estimate": { "type": ["object", "null"] }
          }
        }
        """);

    public static readonly JsonElement GeocodeOutputSchema = Parse(
        $$"""
        {
          "type": "object",
          "oneOf": [
            { "required": ["provider", "candidates"] },
            {{ToolErrorBranch}}
          ],
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
            },
            {{ToolErrorProperties}}
          }
        }
        """);

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

    public static readonly JsonElement IngestJobArtifactOutputSchema = Parse(
        """
        {
          "type": "object",
          "required": ["success", "intentId", "datasetName", "rowCount"],
          "properties": {
            "success": { "type": "boolean" },
            "intentId": { "type": "string" },
            "datasetName": { "type": "string" },
            "rowCount": { "type": "integer" },
            "connectionId": { "type": ["string", "null"] },
            "schema": { "type": ["string", "null"] },
            "table": { "type": ["string", "null"] },
            "srid": { "type": ["integer", "null"], "description": "Stored SRID." },
            "sourceSrid": { "type": ["integer", "null"], "description": "Source SRID." },
            "crsState": { "type": ["string", "null"], "description": "CRS provenance (known or unknown)." },
            "contentSha256": { "type": ["string", "null"] },
            "geometryColumn": { "type": ["string", "null"] },
            "primaryKey": { "type": ["string", "null"] },
            "warnings": { "type": "array", "items": { "type": "string" } },
            "errorMessage": { "type": ["string", "null"] }
          }
        }
        """);

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
              "description": "Catalog connection id for honua_publish_service, or null."
            },
            "schema": { "type": ["string", "null"], "description": "Target schema." },
            "table": { "type": ["string", "null"], "description": "Target table." },
            "srid": { "type": ["integer", "null"] },
            "sourceSrid": { "type": ["integer", "null"] },
            "storedSrid": { "type": ["integer", "null"] },
            "reprojected": { "type": "boolean" },
            "geometryColumn": { "type": ["string", "null"] },
            "primaryKey": { "type": ["string", "null"] },
            "warnings": { "type": "array", "items": { "type": "string" } },
            "rowErrors": {
              "type": "array",
              "items": {
                "type": "object",
                "required": ["code", "message"],
                "properties": {
                  "row": { "type": ["integer", "null"], "description": "1-based data row." },
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
                  "serviceLayerIndex": { "type": "integer" },
                  "studioSourceId": { "type": "string", "description": "Existing Studio catalog key for composition layer.sourceId; not a composition layer.id or a data permission." },
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

    public static readonly JsonElement QueryFeaturesOutputSchema = Parse(
        $$"""
        {
          "type": "object",
          "oneOf": [
            { "required": ["serviceId", "serviceLayerIndex", "returnedCount", "limit", "resultOffset", "exceededTransferLimit", "features"] },
            {{ToolErrorBranch}}
          ],
          "properties": {
            "serviceId": { "type": "string" },
            "serviceLayerIndex": { "type": "integer" },
            "returnedCount": { "type": "integer" },
            "limit": { "type": "integer" },
            "resultOffset": {
              "type": "integer",
              "description": "The resultOffset applied to this request (defaults to 0)."
            },
            "exceededTransferLimit": {
              "type": "boolean",
              "description": "True when more matching features exist beyond this page. When true, re-issue the same query with cursor=nextCursor or resultOffset=nextOffset to fetch the next page."
            },
            "nextOffset": {
              "type": "integer",
              "description": "Present only when exceededTransferLimit is true: the resultOffset to send on the next request (resultOffset + returnedCount) to page mechanically. Absent on the last page."
            },
            "nextCursor": {
              "type": "string",
              "description": "Present only when exceededTransferLimit is true: the cursor to send on the next request. Mirrors nextOffset as a string for SDK clients."
            },
            "count": {
              "type": "integer",
              "description": "Matching feature count when known. Normal queries use the canonical query result total; returnCountOnly=true uses CountAsync."
            },
            "features": {
              "type": "array",
              "description": "Feature list with attributes under features[].attributes.",
              "items": {
                "type": "object",
                "required": ["attributes"],
                "properties": {
                  "id": { "type": ["integer", "string", "null"] },
                  "attributes": { "type": "object" },
                  "geometry": { "type": ["object", "null"] }
                }
              }
            },
            "geojson": {
              "type": "object",
              "description": "GeoJSON FeatureCollection (omitted when returnCountOnly=true).",
              "properties": {
                "type": { "type": "string", "const": "FeatureCollection" },
                "features": { "type": "array", "items": { "type": "object" } }
              }
            },
            "warnings": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Non-fatal advisories such as likely bbox/CRS mismatch."
            },
            {{ToolErrorProperties}}
          }
        }
        """);

    public static readonly JsonElement RenderMapOutputSchema = Parse(
        $$"""
        {
          "type": "object",
          "oneOf": [
            { "required": ["format", "width", "height", "byteLength", "bbox", "bboxSrid", "layers", "image"] },
            {{ToolErrorBranch}}
          ],
          "properties": {
            "format": { "type": "string" },
            "width": { "type": "integer" },
            "height": { "type": "integer" },
            "byteLength": { "type": "integer" },
            "bbox": {
              "type": "array",
              "minItems": 4,
              "maxItems": 4,
              "items": { "type": "number" }
            },
            "bboxSrid": { "type": "integer" },
            "layers": {
              "type": "array",
              "items": {
                "type": "object",
                "required": ["serviceId", "serviceLayerIndex", "styleRendering"],
                "properties": {
                  "serviceId": { "type": "string" },
                  "serviceLayerIndex": { "type": "integer" },
                  "styleId": { "type": ["string", "null"], "description": "Applied primary style id, or null for default." },
                  "styleRendering": {
                    "type": "string",
                    "enum": ["applied", "default", "unsupported-style-construct"],
                    "description": "applied, default, or unsupported-style-construct."
                  },
                  "unsupportedStyleConstructs": { "type": "array", "items": { "type": "string" } }
                }
              }
            },
            "image": {
              "type": "object",
              "required": ["format", "width", "height", "byteLength", "inlined"],
              "properties": {
                "format": { "type": "string" },
                "width": { "type": "integer" },
                "height": { "type": "integer" },
                "byteLength": { "type": "integer" },
                "uri": { "type": ["string", "null"] },
                "base64": { "type": ["string", "null"] },
                "inlined": { "type": "boolean" }
              }
            },
            {{ToolErrorProperties}}
          }
        }
        """);

    // The package sub-object stays open because the canonical Map shape is owned by the domain model, not by this schema.
    public static readonly JsonElement CreateMapOutputSchema = Parse(
        """
        {
          "type": "object",
          "required": ["status", "mapId", "resourceUri"],
          "properties": {
            "status": {
              "type": "string",
              "enum": ["created"],
              "description": "Creation status."
            },
            "mapId": { "type": "string", "description": "Draft map id." },
            "resourceUri": { "type": "string", "description": "honua://maps/{mapId}." },
            "map": { "type": ["object", "null"], "description": "The created Map draft." },
            "warnings": {
              "type": "array",
              "description": "Non-blocking findings.",
              "items": {
                "type": "object",
                "required": ["code", "path", "message"],
                "properties": {
                  "code": { "type": "string" },
                  "path": { "type": "string" },
                  "message": { "type": "string" }
                }
              }
            }
          }
        }
        """);

    public static readonly JsonElement CreateAppOutputSchema = Parse(
        """
        {
          "type": "object",
          "required": ["status", "appId", "resourceUri"],
          "properties": {
            "status": {
              "type": "string",
              "enum": ["created"],
              "description": "Creation status."
            },
            "appId": { "type": "string", "description": "Draft app id." },
            "resourceUri": { "type": "string", "description": "honua://apps/{appId}." },
            "app": { "type": ["object", "null"], "description": "The created App draft." },
            "warnings": {
              "type": "array",
              "description": "Non-blocking findings.",
              "items": {
                "type": "object",
                "required": ["code", "path", "message"],
                "properties": {
                  "code": { "type": "string" },
                  "path": { "type": "string" },
                  "message": { "type": "string" }
                }
              }
            }
          }
        }
        """);

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
              "description": "Style encodings.",
              "items": {
                "type": "object",
                "properties": {
                  "encoding": { "type": "string", "description": "mapbox-style, esri-drawing-info, sld-1.0.0, or sld-1.1.0." },
                  "mediaType": { "type": "string" },
                  "inlineBody": { "type": ["string", "null"], "description": "Inline stylesheet body." },
                  "storageRef": { "type": ["string", "null"], "description": "Style resource URI." }
                }
              }
            },
            "styles": {
              "type": ["array", "null"],
              "description": "Available styles.",
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

    private static readonly JsonElement ApplyStylePresetResultSchema = Parse(
        """
        {
          "type": "object",
          "required": ["serviceId", "serviceLayerIndex", "styleId", "applied", "status", "operationId", "operationInstanceId", "handleId", "correlationId", "createdAt", "updatedAt"],
          "properties": {
            "status": { "type": "string", "description": "Operation handle status." },
            "operationId": { "type": "string", "const": "style.apply-preset" },
            "operationInstanceId": { "type": "string" },
            "handleId": { "type": "string" },
            "durable": {
              "type": "boolean",
              "description": "False when the direct handle was not durably persisted."
            },
            "correlationId": { "type": "string" },
            "auditId": { "type": ["string", "null"], "description": "Operation audit id." },
            "createdAt": { "type": "string", "format": "date-time" },
            "updatedAt": { "type": "string", "format": "date-time" },
            "authorizationOutcome": { "type": ["string", "null"] },
            "policyOutcome": { "type": ["string", "null"] },
            "serviceId": { "type": "string" },
            "serviceLayerIndex": { "type": "integer" },
            "styleId": { "type": "string", "description": "Selected preset id." },
            "title": { "type": ["string", "null"] },
            "styleVersion": { "type": "integer" },
            "applied": { "type": "boolean" },
            "dryRun": { "type": "boolean", "description": "True on dry-run validation." },
            "warning": { "type": ["string", "null"], "description": "Follow-up warning, if any." }
          }
        }
        """);

    public static readonly JsonElement OperationToolOutputSchema = Parse(
        """
        {
          "type": "object",
          "required": ["status", "requiresApproval", "deterministic", "cacheHit", "operationId", "operationInstanceId", "handleId", "correlationId", "createdAt", "updatedAt"],
          "properties": {
            "status": {
              "type": "string",
              "description": "Operation handle status."
            },
            "requiresApproval": { "type": "boolean" },
            "deterministic": {
              "type": "boolean",
              "description": "True when deterministic."
            },
            "cacheHit": {
              "type": "boolean",
              "description": "True on deterministic cache hit."
            },
            "cacheKey": {
              "type": ["string", "null"],
              "description": "Deterministic cache key, or null."
            },
            "operationId": { "type": "string" },
            "operationInstanceId": { "type": "string" },
            "handleId": { "type": "string" },
            "proposalId": { "type": ["string", "null"] },
            "correlationId": { "type": "string" },
            "auditId": { "type": ["string", "null"] },
            "createdAt": { "type": "string", "format": "date-time" },
            "updatedAt": { "type": "string", "format": "date-time" },
            "authorizationOutcome": { "type": ["string", "null"] },
            "policyOutcome": { "type": ["string", "null"] },
            "jobId": {
              "type": ["string", "null"],
              "description": "Queued job id."
            },
            "approvalLane": {
              "type": ["string", "null"],
              "description": "Approval lane when required."
            },
            "metadataRevision": {
              "type": ["integer", "null"],
              "description": "Resulting metadata revision."
            },
            "summary": { "type": ["string", "null"] },
            "message": { "type": ["string", "null"] },
            "details": {
              "type": "object",
              "additionalProperties": { "type": "string" },
              "description": "Operation result details."
            },
            "resourceIds": { "type": "object", "additionalProperties": { "type": "string" } },
            "evidenceRefs": { "type": "array", "items": { "type": "string" } }
          }
        }
        """);

    public static readonly JsonElement ApplyStylePresetOutputSchema = IncludeApprovalStatus(ApplyStylePresetResultSchema);
    public static readonly JsonElement StudioDraftResultSchema = Parse(
        """
        {
          "type": "object",
          "required": ["draftId", "itemId", "packageKey", "family", "envelope", "generation", "createdAt", "updatedAt"],
          "properties": {
            "draftId": { "type": "string" },
            "itemId": { "type": "string" },
            "packageKey": { "type": "string" },
            "workspaceId": { "type": ["string", "null"] },
            "ownerId": { "type": ["string", "null"] },
            "family": { "type": "string" },
            "envelope": { "type": "object" },
            "validation": { "type": "object" },
            "baseVersionId": { "type": ["string", "null"] },
            "generation": {
              "type": "integer",
              "description": "Next mutation generation."
            },
            "createdBy": { "type": ["string", "null"] },
            "updatedBy": { "type": ["string", "null"] },
            "createdAt": { "type": "string" },
            "updatedAt": { "type": "string" }
          }
        }
        """);

    public static readonly JsonElement StudioDraftOutputSchema = IncludeApprovalStatus(StudioDraftResultSchema);
    public static readonly JsonElement StudioDraftMutationOutputSchema = Parse(
        """
        {
          "type": "object",
          "required": ["operation"],
          "properties": {
            "operation": {
              "type": "object",
              "required": ["operationInstanceId", "operationId", "status", "correlationId", "createdAt", "updatedAt"],
              "properties": {
                "operationInstanceId": { "type": "string" },
                "operationId": { "type": "string" },
                "status": { "type": "string" },
                "correlationId": { "type": "string" },
                "auditId": { "type": ["string", "null"] },
                "proposalId": { "type": ["string", "null"] },
                "createdAt": { "type": "string" },
                "updatedAt": { "type": "string" }
              }
            },
            "draftId": { "type": ["string", "null"] },
            "itemId": { "type": ["string", "null"] },
            "packageKey": { "type": ["string", "null"] },
            "workspaceId": { "type": ["string", "null"] },
            "ownerId": { "type": ["string", "null"] },
            "family": { "type": ["string", "null"] },
            "envelope": { "type": ["object", "null"] },
            "validation": { "type": ["object", "null"] },
            "baseVersionId": { "type": ["string", "null"] },
            "generation": { "type": ["integer", "null"] },
            "createdBy": { "type": ["string", "null"] },
            "updatedBy": { "type": ["string", "null"] },
            "createdAt": { "type": ["string", "null"] },
            "updatedAt": { "type": ["string", "null"] },
            "studioAuthorizationCode": {
              "type": ["string", "null"],
              "description": "Stable Studio authorization denial code on an error branch."
            }
          }
        }
        """);

    public static readonly JsonElement StudioValidationOutputSchema = Parse(
        """
        {
          "type": "object",
          "required": ["status"],
          "properties": {
            "status": { "type": "string", "enum": ["not-validated", "valid", "warning", "invalid"] },
            "diagnostics": { "type": "array", "items": { "type": "object" } },
            "unsupportedCapabilities": { "type": "array", "items": { "type": "string" } },
            "generatedAt": { "type": ["string", "null"] }
          }
        }
        """);

    public static readonly JsonElement StudioPreviewPlanOutputSchema = Parse(
        """
        {
          "type": "object",
          "required": ["draftId", "family", "synchronous", "requiresJob", "steps", "validation"],
          "properties": {
            "draftId": { "type": "string" },
            "family": { "type": "string" },
            "synchronous": { "type": "boolean" },
            "requiresJob": { "type": "boolean" },
            "steps": { "type": "array", "items": { "type": "string" } },
            "validation": { "type": "object" }
          }
        }
        """);

    public static readonly JsonElement StudioProposePublicationOutputSchema = Parse(
        """
        {
          "type": "object",
          "required": ["operation", "operationInstanceId", "proposalId", "proposalUri", "auditId", "correlationId", "idempotencyIdentity", "status", "humanConfirmationRequired", "message"],
          "properties": {
            "operation": { "type": "object" },
            "operationInstanceId": { "type": "string", "minLength": 1 },
            "proposalId": { "type": "string", "minLength": 1 },
            "proposalUri": { "type": "string", "pattern": "^honua://proposals/.+" },
            "auditId": { "type": "string", "minLength": 1 },
            "correlationId": { "type": "string", "minLength": 1 },
            "idempotencyIdentity": { "type": "string", "minLength": 1 },
            "status": { "type": "string", "minLength": 1 },
            "humanConfirmationRequired": { "type": "boolean" },
            "message": { "type": "string" }
          }
        }
        """);

    public static readonly JsonElement StudioItemsOutputSchema = Parse("""
        {"type":"object","required":["items","total"],"properties":{
        "items":{"type":"array","items":{"type":"object","required":["itemId","packageKey","family","state"],"properties":{
        "itemId":{"type":"string","format":"uuid"},"packageKey":{"type":"string"},"family":{"type":"string"},
        "state":{"type":"string"},"currentVersionId":{"type":["string","null"]},"publishedVersionId":{"type":["string","null"]}}}},
        "total":{"type":"integer","minimum":0},"nextCursor":{"type":["string","null"]}}}
        """);

    public static readonly JsonElement StudioVersionsOutputSchema = Parse("""
        {"type":"object","required":["itemId","versions"],"properties":{
        "itemId":{"type":"string","format":"uuid"},"versions":{"type":"array","items":{"type":"object",
        "required":["versionId","versionNumber","contentHash","createdAt"],"properties":{
        "versionId":{"type":"string","format":"uuid"},"versionNumber":{"type":"integer","minimum":1},
        "contentHash":{"type":"string","minLength":1},"createdAt":{"type":"string","format":"date-time"},
        "changeNote":{"type":["string","null"]}}}},"nextBeforeVersionNumber":{"type":["integer","null"],"minimum":1}}}
        """);

    public static readonly JsonElement StudioGetVersionOutputSchema = Parse("""
        {"type":"object","required":["versionId","contentHash"],"additionalProperties":false,"properties":{
          "versionId":{"type":"string","format":"uuid"},
          "contentHash":{"type":"string","minLength":1}}}
        """);

    public static readonly JsonElement StudioVersionMutationOutputSchema = Parse("""
        {"type":"object","required":["operation"],"properties":{
          "operation":{"type":"object","required":["operationId","operationInstanceId","status"],"properties":{
            "operationId":{"type":"string"},"operationInstanceId":{"type":"string"},"status":{"type":"string"},
            "proposalId":{"type":["string","null"]}}},
          "version":{"type":["object","null"],"properties":{
            "itemId":{"type":"string"},"versionId":{"type":"string"},"contentHash":{"type":"string"},
            "envelope":{"type":"object"},"validation":{"type":"object"}}},
          "studioAuthorizationCode":{"type":["string","null"]}}}
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
