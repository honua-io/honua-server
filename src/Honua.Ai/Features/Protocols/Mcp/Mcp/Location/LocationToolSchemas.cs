// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Ai.Protocols.Mcp.Location;

internal static class LocationToolSchemas
{
    public const int MinGeocodeResults = 1;

    public const int MaxGeocodeResults = 50;

    public const int DefaultGeocodeResults = 5;

    public const int MinRouteStops = 2;

    public const int MaxRouteStops = 25;

    public const int MinBatchAddresses = 1;

    public const int MaxBatchAddresses = 100;

    private const string GeocodeArgumentSchemaJson = """
        {
          "type": "object",
          "required": ["address"],
          "properties": {
            "address": {
              "type": "string",
              "minLength": 1,
              "description": "Caller-supplied. Freeform single-line address text to geocode."
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

    private const string GeocodeAddressesArgumentSchemaJson = """
        {
          "type": "object",
          "required": ["addresses"],
          "properties": {
            "addresses": {
              "type": "array",
              "minItems": 1,
              "maxItems": 100,
              "description": "Caller-supplied. Freeform single-line addresses to geocode, in the order results should be returned.",
              "items": {
                "type": "string",
                "minLength": 1
              }
            },
            "countryCodes": {
              "type": "string",
              "description": "Optional comma-separated ISO 3166 country codes to restrict the search (e.g. \"us,ca\")."
            },
            "outSrid": {
              "type": "integer",
              "default": 4326,
              "description": "Spatial reference (SRID/WKID) for returned coordinates. Defaults to 4326 (WGS84 lon/lat)."
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
              "description": "Caller-supplied ordered stop coordinates, in visit order.",
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

    public static readonly JsonElement GeocodeArgumentSchema = Parse(GeocodeArgumentSchemaJson);

    public static readonly JsonElement GeocodeAddressesArgumentSchema = Parse(GeocodeAddressesArgumentSchemaJson);

    public static readonly JsonElement RouteArgumentSchema = Parse(RouteArgumentSchemaJson);

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
