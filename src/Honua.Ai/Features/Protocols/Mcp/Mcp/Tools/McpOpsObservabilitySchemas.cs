// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// JSON schemas for the platform-operations observability MCP tools and their
/// resource-shaped structured results.
/// </summary>
internal static class McpOpsObservabilitySchemas
{
    public static readonly JsonElement OpsHealthInputSchema = Parse(
        """
        {
          "type": "object",
          "properties": {},
          "additionalProperties": false
        }
        """);

    public static readonly JsonElement OpsFindingsInputSchema = Parse(
        """
        {
          "type": "object",
          "properties": {
            "findingId": {
              "type": "string",
              "minLength": 1,
              "description": "Deterministic finding identifier to fetch one finding."
            },
            "severity": {
              "type": "string",
              "enum": ["Info", "Warning", "Critical"],
              "description": "Optional list-mode severity filter."
            },
            "rule": {
              "type": "string",
              "description": "Optional kebab-case finding rule filter."
            }
          },
          "additionalProperties": false
        }
        """);

    public static readonly JsonElement AlertEventsInputSchema = Parse(
        """
        {
          "type": "object",
          "properties": {
            "source": {
              "type": "string",
              "enum": ["gis", "ops"],
              "description": "gis for alert-rule events, ops for operations notifications not linked to a rule."
            },
            "severity": {
              "type": "string",
              "enum": ["info", "warning", "critical"]
            },
            "rule": {
              "type": "string",
              "description": "Source alert-rule identifier or name."
            },
            "lifecycleState": {
              "type": "string",
              "enum": ["open", "acknowledged", "suppressed", "resolved"]
            },
            "from": {
              "type": "string",
              "format": "date-time"
            },
            "to": {
              "type": "string",
              "format": "date-time"
            },
            "pageSize": {
              "type": "integer",
              "minimum": 1
            },
            "cursor": {
              "type": "string"
            }
          },
          "additionalProperties": false
        }
        """);

    public static readonly JsonElement OperateEventsInputSchema = Parse(
        """
        {
          "type": "object",
          "properties": {
            "kind": {
              "type": "array",
              "items": {
                "type": "string",
                "enum": ["alert", "audit", "job", "release", "syncConflict", "temporalOp", "log"]
              }
            },
            "correlationId": {
              "type": "string"
            },
            "operationId": {
              "type": "string"
            },
            "releaseId": {
              "type": "string"
            },
            "from": {
              "type": "string",
              "format": "date-time"
            },
            "to": {
              "type": "string",
              "format": "date-time"
            },
            "pageSize": {
              "type": "integer",
              "minimum": 1
            }
          },
          "additionalProperties": false
        }
        """);

    public static readonly JsonElement OpsHealthOutputSchema = Parse(
        """
        {
          "type": "object",
          "additionalProperties": true,
          "required": ["generatedAt", "overallStatus", "health", "servingLatency", "geoprocessing", "alertDispatch", "deploy", "database"],
          "properties": {
            "generatedAt": {
              "type": "string",
              "format": "date-time"
            },
            "overallStatus": {
              "type": "string"
            },
            "health": {
              "type": "object",
              "additionalProperties": true
            },
            "servingLatency": {
              "type": "object",
              "additionalProperties": true
            },
            "geoprocessing": {
              "type": "object",
              "additionalProperties": true
            },
            "alertDispatch": {
              "type": "object",
              "additionalProperties": true
            },
            "deploy": {
              "type": "object",
              "additionalProperties": true
            },
            "database": {
              "type": "object",
              "additionalProperties": true
            }
          }
        }
        """);

    public static readonly JsonElement OpsFindingsOutputSchema = Parse(
        """
        {
          "type": "object",
          "additionalProperties": true,
          "required": ["generatedAt", "findings"],
          "properties": {
            "findings": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": true,
                "required": ["id", "rule", "severity", "title", "explanation", "detectedAt", "subject", "evidenceRefs"]
              }
            }
          }
        }
        """);

    public static readonly JsonElement AlertEventsOutputSchema = Parse(
        """
        {
          "type": "object",
          "additionalProperties": true,
          "required": ["items"],
          "properties": {
            "items": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": true,
                "required": ["eventId", "ruleId", "serviceId", "layerId", "objectId", "triggerType", "severity", "occurredAt", "incidentStatus", "incidentDurationMs", "lifecycleStatus", "resourceRef"]
              }
            },
            "nextCursor": {
              "type": "string"
            }
          }
        }
        """);

    public static readonly JsonElement OperateEventsOutputSchema = Parse(
        """
        {
          "type": "object",
          "additionalProperties": true,
          "required": ["items"],
          "properties": {
            "items": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": true,
                "required": ["eventId", "kind", "severity", "occurredAt", "title"]
              }
            },
            "partialResult": {
              "type": "boolean"
            },
            "sourceErrors": {
              "type": "object",
              "additionalProperties": { "type": "string" }
            }
          }
        }
        """);

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
