// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// JSON schemas for platform-release and deploy-operation MCP tools.
/// </summary>
internal static class McpPlatformOpsSchemas
{
    public static readonly JsonElement PlatformReleaseStatusInputSchema = Parse(
        """
        {
          "type": "object",
          "properties": {},
          "additionalProperties": false
        }
        """);

    public static readonly JsonElement SupportedOperationKindsInputSchema = Parse(
        """
        {
          "type": "object",
          "properties": {},
          "additionalProperties": false
        }
        """);

    public static readonly JsonElement DeployOperationsInputSchema = Parse(
        """
        {
          "type": "object",
          "properties": {
            "operationId": {
              "type": "string",
              "minLength": 1,
              "description": "Deploy-operation identifier to fetch a single operation. When omitted, the tool lists deploy operations."
            },
            "status": {
              "type": "string",
              "enum": [
                "Planned",
                "AwaitingApproval",
                "Submitted",
                "Reconciling",
                "Succeeded",
                "Failed",
                "RollbackRequested",
                "RolledBack",
                "ManualInterventionRequired"
              ]
            },
            "kind": {
              "type": "string",
              "enum": ["Deploy", "Rollback", "Migration", "MetadataRelease", "CoordinatedRelease"]
            },
            "page": {
              "type": "integer",
              "minimum": 1,
              "default": 1
            },
            "pageSize": {
              "type": "integer",
              "minimum": 1,
              "maximum": 200,
              "default": 50
            }
          },
          "additionalProperties": false
        }
        """);

    public static readonly JsonElement ProposeRollbackInputSchema = Parse(
        """
        {
          "type": "object",
          "required": ["targetId"],
          "properties": {
            "targetId": {
              "type": "string",
              "minLength": 1,
              "description": "Deploy target to roll back."
            },
            "toRevision": {
              "type": "string",
              "description": "Optional prior revision to roll forward to. When omitted, the server selects the target's most recent prior succeeded Deploy revision."
            },
            "reason": {
              "type": "string",
              "description": "Operator-facing reason recorded on the proposal."
            },
            "idempotencyKey": {
              "type": "string",
              "description": "Stable idempotency key for the underlying forward Deploy operation."
            }
          },
          "additionalProperties": false
        }
        """);

    public static readonly JsonElement PlatformReleaseStatusOutputSchema = Parse(
        """
        {
          "type": "object",
          "additionalProperties": true,
          "required": ["releaseDeclared", "isCoVersioned", "serving", "execution", "skewedIds"],
          "properties": {
            "releaseVersion": { "type": ["string", "null"] },
            "releaseDeclared": { "type": "boolean" },
            "isCoVersioned": { "type": "boolean" },
            "serving": {
              "type": "array",
              "items": { "type": "object", "additionalProperties": true }
            },
            "execution": {
              "type": "array",
              "items": { "type": "object", "additionalProperties": true }
            },
            "skewedIds": {
              "type": "array",
              "items": { "type": "string" }
            }
          }
        }
        """);

    public static readonly JsonElement DeployOperationsOutputSchema = Parse(
        """
        {
          "type": "object",
          "additionalProperties": true,
          "required": ["items", "page", "pageSize", "totalCount", "hasMore"],
          "properties": {
            "items": {
              "type": "array",
              "items": { "type": "object", "additionalProperties": true }
            },
            "page": { "type": "integer" },
            "pageSize": { "type": "integer" },
            "totalCount": { "type": "integer" },
            "hasMore": { "type": "boolean" }
          }
        }
        """);

    public static readonly JsonElement SupportedOperationKindsOutputSchema = Parse(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["supportedKinds"],
          "properties": {
            "supportedKinds": {
              "type": "array",
              "items": {
                "type": "string",
                "enum": ["AdminConfigChange", "Deploy", "MetadataRelease", "Seed"]
              },
              "uniqueItems": true
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
