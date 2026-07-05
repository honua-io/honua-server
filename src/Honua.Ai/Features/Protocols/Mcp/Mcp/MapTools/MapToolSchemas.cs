// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Ai.Protocols.Mcp.MapTools;

/// <summary>
/// JSON-schema documents advertised in <c>tools/list</c> for the catalog,
/// feature-query, and map-render tools. Schemas are immutable
/// <see cref="JsonElement"/> values parsed at type-load time to keep the MCP
/// surface AOT-safe (no runtime schema reflection). Mirrors
/// <see cref="Honua.Ai.Protocols.Mcp.Location.LocationToolSchemas"/>.
/// </summary>
internal static class MapToolSchemas
{
    /// <summary>Default feature count returned by <c>honua_query_features</c>.</summary>
    public const int DefaultFeatureLimit = 100;

    /// <summary>Hard ceiling on features returned by <c>honua_query_features</c>.</summary>
    public const int MaxFeatureLimit = 1000;

    /// <summary>Default render width/height when omitted.</summary>
    public const int DefaultRenderSize = 512;

    /// <summary>Maximum render width/height per side (caps cost and payload).</summary>
    public const int MaxRenderSize = 1024;

    private const string ListLayersArgumentSchemaJson = """
        {
          "type": "object",
          "properties": {
            "filter": {
              "type": "string",
              "description": "Optional case-insensitive substring to match against service and layer name/title. When omitted, all published layers are returned."
            }
          }
        }
        """;

    private const string QueryFeaturesArgumentSchemaJson = """
        {
          "type": "object",
          "required": ["serviceId", "layerId"],
          "properties": {
            "serviceId": {
              "type": "string",
              "minLength": 1,
              "description": "Published service identifier or name (from honua_list_layers.serviceId)."
            },
            "layerId": {
              "type": "integer",
              "minimum": 0,
              "description": "Service-local layer index (from honua_list_layers.layerId)."
            },
            "where": {
              "type": "string",
              "description": "Optional attribute filter expressed as an ArcGIS/SQL WHERE clause (e.g. \"population > 10000\")."
            },
            "bbox": {
              "type": "array",
              "minItems": 4,
              "maxItems": 4,
              "items": { "type": "number" },
              "description": "Optional spatial filter envelope as [minX, minY, maxX, maxY]."
            },
            "bboxSrid": {
              "type": "integer",
              "default": 4326,
              "description": "Spatial reference (SRID/WKID) of the bbox ordinates."
            },
            "outFields": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Optional attribute fields to return; omit for all fields."
            },
            "limit": {
              "type": "integer",
              "minimum": 1,
              "maximum": 1000,
              "default": 100,
              "description": "Maximum number of features to return (capped at 1000)."
            },
            "outSrid": {
              "type": "integer",
              "default": 4326,
              "description": "Output spatial reference (SRID/WKID) for returned geometries."
            }
          }
        }
        """;

    private const string RenderMapArgumentSchemaJson = """
        {
          "type": "object",
          "required": ["layers", "bbox"],
          "properties": {
            "layers": {
              "type": "array",
              "minItems": 1,
              "description": "Ordered layers to draw, bottom-to-top.",
              "items": {
                "type": "object",
                "required": ["serviceId", "layerId"],
                "properties": {
                  "serviceId": {
                    "type": "string",
                    "minLength": 1,
                    "description": "Published service identifier or name (from honua_list_layers.serviceId)."
                  },
                  "layerId": {
                    "type": "integer",
                    "minimum": 0,
                    "description": "Service-local layer index (from honua_list_layers.layerId)."
                  }
                }
              }
            },
            "bbox": {
              "type": "array",
              "minItems": 4,
              "maxItems": 4,
              "items": { "type": "number" },
              "description": "Map extent as [minX, minY, maxX, maxY]."
            },
            "bboxSrid": {
              "type": "integer",
              "default": 4326,
              "description": "Spatial reference (SRID/WKID) of the bbox and output image."
            },
            "width": {
              "type": "integer",
              "minimum": 1,
              "maximum": 1024,
              "default": 512,
              "description": "Output image width in pixels (capped at 1024)."
            },
            "height": {
              "type": "integer",
              "minimum": 1,
              "maximum": 1024,
              "default": 512,
              "description": "Output image height in pixels (capped at 1024)."
            },
            "transparent": {
              "type": "boolean",
              "default": false,
              "description": "Render a transparent background instead of an opaque one."
            }
          }
        }
        """;

    /// <summary>Schema for <see cref="McpListLayersArgument"/>.</summary>
    public static readonly JsonElement ListLayersArgumentSchema = Parse(ListLayersArgumentSchemaJson);

    /// <summary>Schema for <see cref="McpQueryFeaturesArgument"/>.</summary>
    public static readonly JsonElement QueryFeaturesArgumentSchema = Parse(QueryFeaturesArgumentSchemaJson);

    private const string EditFeaturesArgumentSchemaJson = """
        {
          "type": "object",
          "required": ["serviceId", "layerId"],
          "properties": {
            "serviceId": {
              "type": "string",
              "minLength": 1,
              "description": "Published service identifier or name (from honua_list_layers.serviceId or honua_resolve_entity)."
            },
            "layerId": {
              "type": "integer",
              "minimum": 0,
              "description": "Service-local layer index of the editable layer (from honua_list_layers.layerId)."
            },
            "srid": {
              "type": "integer",
              "default": 4326,
              "description": "Spatial reference (SRID/WKID) of the input feature geometries."
            },
            "adds": {
              "type": "array",
              "description": "Features to insert. Object IDs are assigned by the store.",
              "items": { "$ref": "#/$defs/editFeature" }
            },
            "updates": {
              "type": "array",
              "description": "Features to update. Each MUST carry an objectId identifying the existing feature.",
              "items": { "$ref": "#/$defs/editFeature" }
            },
            "deletes": {
              "type": "array",
              "description": "Object IDs of features to delete.",
              "items": { "type": "integer" }
            },
            "rollbackOnFailure": {
              "type": "boolean",
              "default": true,
              "description": "When true, any failed edit rolls back the whole transaction (all-or-nothing)."
            },
            "returnEditResults": {
              "type": "boolean",
              "default": true,
              "description": "When true, per-edit results are returned; when false only the transaction summary is emitted."
            }
          },
          "$defs": {
            "editFeature": {
              "type": "object",
              "properties": {
                "objectId": {
                  "type": "integer",
                  "description": "Object ID of the target feature. Required for updates; ignored for adds."
                },
                "globalId": {
                  "type": "string",
                  "description": "Optional global ID of the feature."
                },
                "geometry": {
                  "type": ["object", "null"],
                  "description": "RFC 7946 GeoJSON geometry object (e.g. {\"type\":\"Point\",\"coordinates\":[1,2]})."
                },
                "attributes": {
                  "type": "object",
                  "description": "Flat attribute name/value map applied to the feature."
                }
              }
            }
          }
        }
        """;

    /// <summary>Schema for <see cref="McpRenderMapArgument"/>.</summary>
    public static readonly JsonElement RenderMapArgumentSchema = Parse(RenderMapArgumentSchemaJson);

    /// <summary>Schema for <see cref="McpEditFeaturesArgument"/>.</summary>
    public static readonly JsonElement EditFeaturesArgumentSchema = Parse(EditFeaturesArgumentSchemaJson);

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
