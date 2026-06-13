// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Ai.Protocols.Mcp.Location;

/// <summary>
/// JSON-schema documents advertised in <c>tools/list</c> for the geocode and
/// route tools. Schemas are immutable <see cref="JsonElement"/> values parsed
/// at type-load time to keep the MCP surface AOT-safe.
/// </summary>
internal static class LocationToolSchemas
{
    /// <summary>Lower bound (inclusive) for <c>maxResults</c>.</summary>
    public const int MinGeocodeResults = 1;

    /// <summary>Upper bound (inclusive) for <c>maxResults</c>.</summary>
    public const int MaxGeocodeResults = 50;

    /// <summary>Default candidate count when <c>maxResults</c> is omitted.</summary>
    public const int DefaultGeocodeResults = 5;

    /// <summary>Minimum number of stops required to solve a route.</summary>
    public const int MinRouteStops = 2;

    /// <summary>Maximum number of stops accepted per route solve.</summary>
    public const int MaxRouteStops = 25;

    private const string GeocodeArgumentSchemaJson = """
        {
          "type": "object",
          "required": ["address"],
          "properties": {
            "address": {
              "type": "string",
              "minLength": 1,
              "description": "Freeform single-line address text to geocode."
            },
            "maxResults": {
              "type": "integer",
              "minimum": 1,
              "maximum": 50,
              "default": 5,
              "description": "Maximum number of candidates to return."
            },
            "countryCodes": {
              "type": "string",
              "description": "Optional comma-separated ISO 3166 country codes to restrict the search (e.g. \"us,ca\")."
            },
            "outSrid": {
              "type": "integer",
              "default": 4326,
              "description": "Spatial reference (SRID/WKID) for returned candidate coordinates."
            },
            "provider": {
              "type": "string",
              "description": "Optional preferred geocoding provider name; the server falls back to provider priority order when omitted."
            }
          }
        }
        """;

    private const string RouteArgumentSchemaJson = """
        {
          "type": "object",
          "required": ["stops"],
          "properties": {
            "stops": {
              "type": "array",
              "minItems": 2,
              "maxItems": 25,
              "description": "Ordered stops to route through, in visit order.",
              "items": {
                "type": "object",
                "required": ["lon", "lat"],
                "properties": {
                  "lon": {
                    "type": "number",
                    "description": "Longitude (X) ordinate in the request spatial reference."
                  },
                  "lat": {
                    "type": "number",
                    "description": "Latitude (Y) ordinate in the request spatial reference."
                  }
                }
              }
            },
            "travelProfile": {
              "type": "string",
              "default": "driving",
              "description": "Travel profile / cost-model selector (e.g. \"driving\", \"walking\")."
            },
            "outSrid": {
              "type": "integer",
              "default": 4326,
              "description": "Output spatial reference (SRID/WKID) for the returned route geometry."
            },
            "inSrid": {
              "type": "integer",
              "description": "Spatial reference of the input stop ordinates; defaults to outSrid."
            }
          }
        }
        """;

    /// <summary>
    /// Schema for <see cref="McpGeocodeArgument"/>.
    /// </summary>
    public static readonly JsonElement GeocodeArgumentSchema = Parse(GeocodeArgumentSchemaJson);

    /// <summary>
    /// Schema for <see cref="McpRouteArgument"/>.
    /// </summary>
    public static readonly JsonElement RouteArgumentSchema = Parse(RouteArgumentSchemaJson);

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
