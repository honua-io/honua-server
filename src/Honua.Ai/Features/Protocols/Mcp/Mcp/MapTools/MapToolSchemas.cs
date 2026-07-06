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

    /// <summary>
    /// Default number of decimal places returned <c>honua_query_features</c>
    /// geometry coordinates are rounded to (~0.1&#160;m at the equator). Trims
    /// full-precision coordinate noise that otherwise dominates the response
    /// token budget.
    /// </summary>
    public const int DefaultGeometryPrecision = 6;

    /// <summary>
    /// Maximum decimal places <c>geometryPrecision</c> can request. Values above
    /// this (or below zero) skip quantization and return full-precision
    /// coordinates; <see cref="System.Math.Round(double, int)"/> caps at 15.
    /// </summary>
    public const int MaxGeometryPrecision = 15;

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
            "resultOffset": {
              "type": "integer",
              "minimum": 0,
              "default": 0,
              "description": "Number of matching features to skip before returning results (pagination). Pair with limit to page mechanically through large result sets: when a response reports exceededTransferLimit=true, re-issue the same query with resultOffset set to the returned nextOffset until exceededTransferLimit is false."
            },
            "returnGeometry": {
              "type": "boolean",
              "default": true,
              "description": "When false, features are returned without geometry (attribute-only rows). Use for attribute scans where returning geometry would waste the response/context budget."
            },
            "returnCountOnly": {
              "type": "boolean",
              "default": false,
              "description": "When true, return only the matching feature count and no features. Use for cheap cardinality checks before deciding whether/how to page."
            },
            "outSrid": {
              "type": "integer",
              "default": 4326,
              "description": "Output spatial reference (SRID/WKID) for returned geometries."
            },
            "geometryPrecision": {
              "type": "integer",
              "default": 6,
              "description": "x-honua extension: decimal places to round returned geometry coordinates to. Defaults to 6 (~0.1 m at the equator) so coordinates stay compact instead of emitting 15-digit full-precision noise that inflates the response token budget. Pass a higher value for finer precision, or a negative value for full unrounded coordinates. Ignored when returnGeometry=false."
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
            },
            "maxInlineBytes": {
              "type": "integer",
              "minimum": 0,
              "default": 0,
              "description": "x-honua extension: opt-in ceiling (bytes) for inlining the rendered PNG as a base64 image content block. When the encoded image is at or below this size it is returned inline; otherwise, and by default (0), the tool returns a fetchable artifact href (resource_link) with the image dimensions and byte size in text so a multi-megabyte render never floods the model context."
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

    // No required fields: styleId, serviceId+layerId, and the empty (list) call
    // are all valid. Optional serviceId/layerId are the reference-implementation
    // shape for resolving a published layer's primary style.
    private const string GetStyleArgumentSchemaJson = """
        {
          "type": "object",
          "properties": {
            "styleId": {
              "type": "string",
              "minLength": 1,
              "description": "Catalog style identifier (style_…) to resolve. Omit to resolve serviceId+layerId's primary style, or to list the available styles when neither is given."
            },
            "serviceId": {
              "type": "string",
              "minLength": 1,
              "description": "Published service identifier or name (from honua_list_layers.serviceId). Paired with layerId, resolves that layer's primary/default style when styleId is omitted."
            },
            "layerId": {
              "type": "integer",
              "minimum": 0,
              "description": "Service-local layer index (from honua_list_layers.layerId). Paired with serviceId, resolves that layer's primary/default style when styleId is omitted."
            },
            "encoding": {
              "type": "string",
              "enum": ["mapbox-style", "sld-1.0.0", "sld-1.1.0", "esri-drawing-info"],
              "default": "mapbox-style",
              "description": "Stylesheet encoding to inline when includeStylesheet=true. mapbox-style is the canonical MapLibre style JSON; esri-drawing-info is the Esri renderer; SLD 1.0/1.1 are derived."
            },
            "includeStylesheet": {
              "type": "boolean",
              "default": false,
              "description": "When true, inline the resolved stylesheet body for the selected encoding; otherwise encodings are advertised by reference."
            }
          }
        }
        """;

    private const string ApplyStylePresetArgumentSchemaJson = """
        {
          "type": "object",
          "required": ["serviceId", "layerId", "styleId"],
          "properties": {
            "serviceId": {
              "type": "string",
              "minLength": 1,
              "description": "Published service identifier or name whose layer is being styled (from honua_list_layers.serviceId)."
            },
            "layerId": {
              "type": "integer",
              "minimum": 0,
              "description": "Service-local layer index to apply the preset to (from honua_list_layers.layerId)."
            },
            "styleId": {
              "type": "string",
              "minLength": 1,
              "description": "Catalog style preset identifier (style_…) to apply as the layer's primary/default style. Discover valid presets with honua_get_style (omit styleId to list them)."
            }
          }
        }
        """;

    /// <summary>Schema for <see cref="McpGetStyleArgument"/>.</summary>
    public static readonly JsonElement GetStyleArgumentSchema = Parse(GetStyleArgumentSchemaJson);

    /// <summary>Schema for <see cref="McpApplyStylePresetArgument"/>.</summary>
    public static readonly JsonElement ApplyStylePresetArgumentSchema = Parse(ApplyStylePresetArgumentSchemaJson);

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
