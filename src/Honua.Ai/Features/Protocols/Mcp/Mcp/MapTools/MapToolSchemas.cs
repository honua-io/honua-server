// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Ai.Protocols.Mcp.MapTools;

internal static class MapToolSchemas
{
    public const int DefaultLayerLimit = 50;

    public const int MaxLayerLimit = 500;

    public const int DefaultFeatureLimit = 100;

    public const int MaxFeatureLimit = 1000;

    public const int DefaultRenderSize = 512;

    public const int MaxRenderSize = 1024;

    public const int MaxRenderLayers = 16;

    // Server-side ceiling on inline PNG bytes so a caller cannot flood model context with multi-megabyte base64 payloads.
    public const int MaxInlineBytesCeiling = 262_144;

    public const int DefaultGeometryPrecision = 6;

    // System.Math.Round caps decimal digits at 15, so a geometryPrecision above this ceiling or below zero skips quantization and returns full-precision coordinates.
    public const int MaxGeometryPrecision = 15;

    private const string ListLayersArgumentSchemaJson = """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "filter": {
              "type": "string",
              "description": "Optional case-insensitive substring to match against service and layer name/title. When omitted, all published layers are returned."
            },
            "limit": {
              "type": "integer",
              "minimum": 1,
              "maximum": 500,
              "default": 50,
              "description": "Maximum number of layers to return on this page (capped at 500)."
            },
            "offset": {
              "type": "integer",
              "minimum": 0,
              "default": 0,
              "description": "Matching layers to skip (pagination). While a response reports hasMore=true, repeat the call with offset set to the returned nextOffset."
            }
          }
        }
        """;

    private const string QueryFeaturesArgumentSchemaJson = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["serviceId", "serviceLayerIndex"],
          "properties": {
            "serviceId": {
              "type": "string",
              "minLength": 1,
              "description": "Service ID or name from honua_list_layers.serviceId."
            },
            "serviceLayerIndex": {
              "type": "integer",
              "minimum": 0,
              "description": "Service-local index from honua_list_layers."
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
            "geometry": {
              "type": "object",
              "description": "Optional GeoJSON geometry object; returns features that intersect it. Use instead of bbox, not both."
            },
            "geometrySrid": {
              "type": "integer",
              "default": 4326,
              "description": "SRID/WKID of the geometry."
            },
            "outFields": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Optional visible attribute fields to return; omit or use '*' for all visible fields. Unknown or publisher-hidden names are refused."
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
              "description": "Matching features to skip (pagination). While a response reports exceededTransferLimit=true, repeat the query with resultOffset set to the returned nextOffset."
            },
            "cursor": {
              "type": "string",
              "pattern": "^[0-9]+$",
              "description": "SDK-facing cursor for the same offset as resultOffset. While a response reports exceededTransferLimit=true, repeat the query with cursor set to nextCursor."
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
              "description": "x-honua extension: decimal places for returned coordinates (default 6, about 0.1 m) to keep responses compact. Higher is finer; negative returns full precision. Ignored when returnGeometry=false."
            }
          }
        }
        """;

    private const string RenderMapArgumentSchemaJson = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["layers", "bbox"],
          "properties": {
            "layers": {
              "type": "array",
              "minItems": 1,
              "maxItems": 16,
              "description": "Published layer references from honua_list_layers, ordered bottom to top.",
              "items": {
                "type": "object",
                "required": ["serviceId", "serviceLayerIndex"],
                "properties": {
                  "serviceId": {
                    "type": "string",
                    "minLength": 1,
                    "description": "Service ID or name from honua_list_layers.serviceId."
                  },
                  "serviceLayerIndex": {
                    "type": "integer",
                    "minimum": 0,
                    "description": "Service-local index from honua_list_layers."
                  }
                }
              }
            },
            "bbox": {
              "type": "array",
              "minItems": 4,
              "maxItems": 4,
              "items": { "type": "number" },
              "description": "Caller-supplied. Map extent as [minX, minY, maxX, maxY]."
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
              "description": "x-honua extension: largest encoded PNG (bytes) returned inline as a base64 image block. Larger images, and every image by default (0), come back as a resource_link href with dimensions and size."
            }
          }
        }
        """;

    public static readonly JsonElement ListLayersArgumentSchema = Parse(ListLayersArgumentSchemaJson);

    public static readonly JsonElement QueryFeaturesArgumentSchema = Parse(QueryFeaturesArgumentSchemaJson);

    public static readonly JsonElement RenderMapArgumentSchema = Parse(RenderMapArgumentSchemaJson);

    // No required fields: styleId, serviceId+serviceLayerIndex, and the empty (list) call
    // are all valid. Optional serviceId/serviceLayerIndex are the reference-implementation
    // shape for resolving a published layer's primary style.
    private const string GetStyleArgumentSchemaJson = """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "styleId": {
              "type": "string",
              "minLength": 1,
              "description": "Catalog style identifier (style_…) to resolve. Omit to resolve serviceId+serviceLayerIndex's primary style, or to list the available styles when neither is given."
            },
            "serviceId": {
              "type": "string",
              "minLength": 1,
              "description": "Service ID or name from honua_list_layers. Pair with serviceLayerIndex when styleId is omitted."
            },
            "serviceLayerIndex": {
              "type": "integer",
              "minimum": 0,
              "description": "Service-local index from honua_list_layers; used when styleId is omitted."
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
          "additionalProperties": true,
          "required": ["serviceId", "serviceLayerIndex", "styleId"],
          "properties": {
            "dryRun": {
              "type": "boolean",
              "default": false,
              "description": "Validate the preset application without changing catalog bindings or metadata. A successful preview returns applied=false."
            },
            "serviceId": {
              "type": "string",
              "minLength": 1,
              "description": "Service ID or name from honua_list_layers."
            },
            "serviceLayerIndex": {
              "type": "integer",
              "minimum": 0,
              "description": "Service-local index from honua_list_layers."
            },
            "styleId": {
              "type": "string",
              "minLength": 1,
              "description": "Catalog style preset identifier (style_…) to apply as the layer's primary/default style. Discover valid presets with honua_get_style (omit styleId to list them)."
            }
          }
        }
        """;

    public static readonly JsonElement GetStyleArgumentSchema = Parse(GetStyleArgumentSchemaJson);

    public static readonly JsonElement ApplyStylePresetArgumentSchema = Parse(ApplyStylePresetArgumentSchemaJson);

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
