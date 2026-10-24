// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// JSON schemas for the job diagnosis MCP tools (honua-server#6219). The output
/// schemas name the top-level sections and the fields an agent must read before
/// acting; nested objects mirror the admin diagnostics projection and are left
/// open so additive projection fields never break the tool contract.
/// </summary>
internal static class McpJobDiagnosticsSchemas
{
    public const int DefaultEvents = 50;

    public const int MaxEvents = 500;

    public static readonly JsonElement DiagnoseJobInputSchema = Parse(
        """
        {
          "type": "object",
          "required": ["jobId"],
          "properties": {
            "eventCursor": {
              "type": "string",
              "maxLength": 128,
              "description": "Omit for the latest events. Use start to page retained events from the beginning, then the returned nextCursor. Pages follow append order and may include events appended since the previous page."
            },
            "jobId": {
              "type": "string",
              "minLength": 1,
              "description": "Identifier of a geoprocessing job the caller can read (from a submission result or honua_list_jobs)."
            },
            "events": {
              "type": "integer",
              "minimum": 1,
              "maximum": 500,
              "default": 50,
              "description": "How many of the most recent recorded events to include in observed.events."
            }
          },
          "additionalProperties": false
        }
        """);

    public static readonly JsonElement DiagnoseJobOutputSchema = Parse(
        """
        {
          "type": "object",
          "required": ["schema", "jobId", "observed", "inferred", "evidence", "caveat"],
          "properties": {
            "schema": { "type": "string" },
            "jobId": { "type": "string" },
            "correlationId": { "type": ["string", "null"] },
            "view": { "type": "string", "enum": ["operator", "owner"] },
            "observed": {
              "type": "object",
              "description": "Facts the server recorded: status, timestamps, heartbeat, failure message and structured error, release identity, steps, events, artifacts.",
              "required": ["status", "identity", "release", "worker", "events", "artifacts"],
              "properties": {
                "status": { "type": "string" },
                "currentPhase": { "type": ["string", "null"] },
                "failure": { "type": ["object", "null"] }
              }
            },
            "inferred": {
              "type": "object",
              "description": "The server's explanations. Verify against observed before acting.",
              "required": ["phase", "timingSource", "liveness", "livenessReason", "retry"],
              "properties": {
                "phase": { "type": "string" },
                "timingSource": { "type": "string", "enum": ["measured", "inferred"] },
                "liveness": { "type": "string" },
                "livenessReason": { "type": "string" },
                "failureClassification": { "type": ["string", "null"] },
                "recovery": { "type": ["string", "null"] },
                "retry": { "type": "object" }
              }
            },
            "evidence": {
              "type": "object",
              "required": ["missing", "truncated", "notes"],
              "properties": {
                "missing": { "type": "array", "items": { "type": "string" } },
                "truncated": { "type": "array", "items": { "type": "string" } },
                "notes": { "type": "array", "items": { "type": "string" } }
              }
            },
            "caveat": { "type": "string" }
          }
        }
        """);

    public static readonly JsonElement JobEvidenceInputSchema = Parse(
        """
        {
          "type": "object",
          "required": ["jobId"],
          "properties": {
            "eventCursor": {
              "type": "string",
              "maxLength": 128,
              "description": "Omit for the latest events. Use start to page retained events from the beginning, then the returned nextCursor. Pages follow append order and may include events appended since the previous page."
            },
            "jobId": {
              "type": "string",
              "minLength": 1,
              "description": "Identifier of a geoprocessing job the caller can read."
            },
            "events": {
              "type": "integer",
              "minimum": 1,
              "maximum": 500,
              "default": 50,
              "description": "How many of the most recent recorded events to return."
            }
          },
          "additionalProperties": false
        }
        """);

    public static readonly JsonElement JobEvidenceOutputSchema = Parse(
        """
        {
          "type": "object",
          "required": ["schema", "jobId", "steps", "events", "artifacts", "evidence"],
          "properties": {
            "schema": { "type": "string" },
            "jobId": { "type": "string" },
            "correlationId": { "type": ["string", "null"] },
            "steps": { "type": "array", "items": { "type": "object" } },
            "events": {
              "type": "object",
              "required": ["state", "items", "totalCount", "limit", "truncated"],
              "properties": {
                "state": { "type": "string" },
                "items": { "type": "array", "items": { "type": "object" } },
                "totalCount": { "type": "integer" },
                "limit": { "type": "integer" },
                "truncated": { "type": "boolean" },
                "firstCursor": { "type": ["string", "null"] },
                "nextCursor": { "type": ["string", "null"] },
                "mode": { "type": "string", "enum": ["tail", "page"] }
              }
            },
            "artifacts": { "type": "object" },
            "evidence": { "type": "object" }
          }
        }
        """);

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
