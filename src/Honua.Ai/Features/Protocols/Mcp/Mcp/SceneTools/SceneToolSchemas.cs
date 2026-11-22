// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Tools;

namespace Honua.Ai.Protocols.Mcp.SceneTools;

internal static class SceneToolSchemas
{
    public const int DefaultSceneLimit = 50;

    public const int MaxSceneLimit = 500;

    public static readonly JsonElement SceneListArgumentSchema = Parse(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "servingFormat": {
              "type": "string",
              "enum": ["3d-tiles", "i3s", "copc", "quantized-mesh", "terrain-rgb"],
              "description": "Optional canonical serving format filter."
            },
            "contentKind": {
              "type": "string",
              "enum": ["mesh", "buildings", "point-cloud", "terrain", "vector-3d", "unclassified"],
              "description": "Optional semantic content kind filter."
            },
            "filter": {
              "type": "string",
              "description": "Optional case-insensitive substring to match scene id, name, or description."
            },
            "limit": {
              "type": "integer",
              "minimum": 1,
              "maximum": 500,
              "default": 50,
              "description": "Maximum number of scenes to return on this page."
            },
            "offset": {
              "type": "integer",
              "minimum": 0,
              "default": 0,
              "description": "Matching scenes to skip for pagination."
            }
          }
        }
        """);

    public static readonly JsonElement SceneGetArgumentSchema = Parse(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["sceneId"],
          "properties": {
            "sceneId": {
              "type": "string",
              "minLength": 1,
              "description": "Scene identifier from honua_scene_list."
            }
          }
        }
        """);

    public static readonly JsonElement SceneResolveArgumentSchema = Parse(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["sceneId"],
          "properties": {
            "sceneId": {
              "type": "string",
              "minLength": 1,
              "description": "Scene identifier from honua_scene_list."
            },
            "preferredFormats": {
              "type": "array",
              "items": {
                "type": "string",
                "enum": ["3d-tiles", "i3s", "copc", "quantized-mesh", "terrain-rgb"]
              },
              "description": "Optional ordered list of preferred endpoint formats."
            }
          }
        }
        """);

    public static readonly JsonElement SceneListOutputSchema = McpToolOutputSchemas.IncludeErrorEnvelope(Parse(
        """
        {
          "type": "object",
          "required": ["sceneCount", "totalCount", "offset", "hasMore", "scenes"],
          "properties": {
            "sceneCount": { "type": "integer", "minimum": 0 },
            "totalCount": { "type": "integer", "minimum": 0 },
            "offset": { "type": "integer", "minimum": 0 },
            "hasMore": { "type": "boolean" },
            "nextOffset": { "type": ["integer", "null"], "minimum": 0 },
            "scenes": {
              "type": "array",
              "items": { "type": "object" }
            }
          }
        }
        """));

    public static readonly JsonElement SceneGetOutputSchema = McpToolOutputSchemas.IncludeErrorEnvelope(Parse(
        """
        {
          "type": "object",
          "required": ["id", "name", "servingFormat", "contentKind", "capabilities", "auth", "links"],
          "properties": {
            "id": { "type": "string" },
            "name": { "type": "string" },
            "description": { "type": ["string", "null"] },
            "servingFormat": { "type": "string" },
            "contentKind": { "type": "string" },
            "tileset": { "type": ["object", "null"] },
            "center": { "type": ["object", "null"] },
            "bounds": { "type": ["object", "null"] },
            "capabilities": { "type": "array", "items": { "type": "string" } },
            "auth": { "type": "object" },
            "links": { "type": "array", "items": { "type": "object" } },
            "updatedAt": { "type": ["string", "null"] }
          }
        }
        """));

    public static readonly JsonElement SceneResolveOutputSchema = McpToolOutputSchemas.IncludeErrorEnvelope(Parse(
        """
        {
          "type": "object",
          "required": ["sceneId", "servingFormat", "contentKind", "endpoints", "capabilities", "auth"],
          "properties": {
            "sceneId": { "type": "string" },
            "servingFormat": { "type": "string" },
            "contentKind": { "type": "string" },
            "tilesetUrl": { "type": ["string", "null"] },
            "endpoints": { "type": "array", "items": { "type": "object" } },
            "capabilities": { "type": "array", "items": { "type": "string" } },
            "auth": { "type": "object" }
          }
        }
        """));

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
