// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Ai.Protocols.Mcp.Location;

// -----------------------------------------------------------------------
// Tool argument shapes (tools/call params.arguments)
// -----------------------------------------------------------------------

/// <summary>
/// Arguments for <c>honua_geocode_address</c>. Mirrors
/// <see cref="Honua.Geocoding.Features.Geocoding.Domain.ForwardGeocodeRequest"/>
/// with optional fields so MCP clients only send what they need.
/// </summary>
internal sealed class McpGeocodeArgument
{
    /// <summary>Freeform single-line address text to geocode.</summary>
    [JsonPropertyName("address")]
    public string? Address { get; set; }

    /// <summary>Maximum number of candidates to return (1-50, default 5).</summary>
    [JsonPropertyName("maxResults")]
    public int? MaxResults { get; set; }

    /// <summary>Optional comma-separated ISO country codes to restrict the search.</summary>
    [JsonPropertyName("countryCodes")]
    public string? CountryCodes { get; set; }

    /// <summary>Spatial reference (SRID/WKID) for returned coordinates. Defaults to 4326.</summary>
    [JsonPropertyName("outSrid")]
    public int? OutSrid { get; set; }

    /// <summary>Optional preferred geocoding provider name (e.g. "nominatim").</summary>
    [JsonPropertyName("provider")]
    public string? Provider { get; set; }
}

/// <summary>
/// Arguments for <c>honua_geocode_addresses</c>: a batch of freeform addresses
/// resolved through the same canonical
/// <see cref="Honua.Geocoding.Features.Geocoding.Abstractions.IGeocodeCoordinatorService"/>
/// pipeline as the single-address tool, one top candidate per input.
/// </summary>
internal sealed class McpGeocodeAddressesArgument
{
    /// <summary>Freeform single-line addresses to geocode, in result order (1-100).</summary>
    [JsonPropertyName("addresses")]
    public IReadOnlyList<string?>? Addresses { get; set; }

    /// <summary>Optional comma-separated ISO country codes to restrict the search.</summary>
    [JsonPropertyName("countryCodes")]
    public string? CountryCodes { get; set; }

    /// <summary>Spatial reference (SRID/WKID) for returned coordinates. Defaults to 4326.</summary>
    [JsonPropertyName("outSrid")]
    public int? OutSrid { get; set; }

    /// <summary>Optional preferred geocoding provider name (e.g. "nominatim").</summary>
    [JsonPropertyName("provider")]
    public string? Provider { get; set; }
}

/// <summary>
/// A single stop in a <c>honua_solve_route</c> request. Coordinates are
/// nullable so missing ordinates surface as <c>invalid_argument</c> instead of
/// silently defaulting to 0.
/// </summary>
internal sealed class McpRouteStopInput
{
    /// <summary>Longitude (X) ordinate in the request spatial reference.</summary>
    [JsonPropertyName("lon")]
    public double? Lon { get; set; }

    /// <summary>Latitude (Y) ordinate in the request spatial reference.</summary>
    [JsonPropertyName("lat")]
    public double? Lat { get; set; }
}

/// <summary>
/// Arguments for <c>honua_solve_route</c>. Mirrors
/// <see cref="Honua.Routing.Features.Routing.Domain.RouteSolveRequest"/>.
/// </summary>
internal sealed class McpRouteArgument
{
    /// <summary>Ordered stops to route through, in visit order (2-25 stops).</summary>
    [JsonPropertyName("stops")]
    public IReadOnlyList<McpRouteStopInput?>? Stops { get; set; }

    /// <summary>Optional travel profile / cost-model selector (e.g. "driving").</summary>
    [JsonPropertyName("travelProfile")]
    public string? TravelProfile { get; set; }

    /// <summary>Output spatial reference (SRID/WKID). Defaults to 4326.</summary>
    [JsonPropertyName("outSrid")]
    public int? OutSrid { get; set; }

    /// <summary>Input spatial reference for stop ordinates. Defaults to <c>outSrid</c>.</summary>
    [JsonPropertyName("inSrid")]
    public int? InSrid { get; set; }
}

// -----------------------------------------------------------------------
// Tool output shapes (tools/call result.structuredContent)
// -----------------------------------------------------------------------

/// <summary>
/// A geocode candidate returned by <c>honua_geocode_address</c>. Wire
/// projection of <see cref="Honua.Geocoding.Features.Geocoding.Domain.GeocodeCandidate"/>.
/// </summary>
internal sealed class McpGeocodeCandidate
{
    /// <summary>Matched address text.</summary>
    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    /// <summary>X (longitude/easting) ordinate in <see cref="Srid"/>.</summary>
    [JsonPropertyName("x")]
    public double X { get; set; }

    /// <summary>Y (latitude/northing) ordinate in <see cref="Srid"/>.</summary>
    [JsonPropertyName("y")]
    public double Y { get; set; }

    /// <summary>Provider match score (0-100).</summary>
    [JsonPropertyName("score")]
    public double Score { get; set; }

    /// <summary>Spatial reference (SRID/WKID) of the coordinates.</summary>
    [JsonPropertyName("srid")]
    public int Srid { get; set; }

    /// <summary>Match level (e.g. "exact", "interpolated"), when the provider reports one.</summary>
    [JsonPropertyName("matchLevel")]
    public string? MatchLevel { get; set; }

    /// <summary>Address type (e.g. "PointAddress", "Locality"), when the provider reports one.</summary>
    [JsonPropertyName("addressType")]
    public string? AddressType { get; set; }
}

/// <summary>
/// Structured output for <c>honua_geocode_address</c>.
/// </summary>
internal sealed class McpGeocodeOutput
{
    /// <summary>Name of the geocoding provider that produced the candidates.</summary>
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    /// <summary>Ranked geocode candidates for the input address.</summary>
    [JsonPropertyName("candidates")]
    public IReadOnlyList<McpGeocodeCandidate> Candidates { get; set; } = [];
}

/// <summary>
/// Resolved coordinate for one input address in a
/// <c>honua_geocode_addresses</c> batch.
/// </summary>
internal sealed class McpGeocodeAddressLocation
{
    /// <summary>X (longitude) ordinate in <see cref="Srid"/>.</summary>
    [JsonPropertyName("x")]
    public double X { get; set; }

    /// <summary>Y (latitude) ordinate in <see cref="Srid"/>.</summary>
    [JsonPropertyName("y")]
    public double Y { get; set; }

    /// <summary>Spatial reference (SRID/WKID) of the coordinates.</summary>
    [JsonPropertyName("srid")]
    public int Srid { get; set; }
}

/// <summary>
/// Per-address result in a <c>honua_geocode_addresses</c> batch. Results are
/// returned in input order; a failed address carries <c>ok=false</c> and an
/// error message without failing the rest of the batch.
/// </summary>
internal sealed class McpGeocodeAddressResult
{
    /// <summary>The input address exactly as supplied.</summary>
    [JsonPropertyName("input")]
    public string Input { get; set; } = string.Empty;

    /// <summary>Whether the address resolved to a location.</summary>
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    /// <summary>Resolved coordinate of the top candidate; null when <see cref="Ok"/> is false.</summary>
    [JsonPropertyName("location")]
    public McpGeocodeAddressLocation? Location { get; set; }

    /// <summary>Provider match score (0-100) of the top candidate.</summary>
    [JsonPropertyName("score")]
    public double? Score { get; set; }

    /// <summary>Matched address text of the top candidate.</summary>
    [JsonPropertyName("matchedAddress")]
    public string? MatchedAddress { get; set; }

    /// <summary>Match level (e.g. "exact", "interpolated"), when the provider reports one.</summary>
    [JsonPropertyName("matchLevel")]
    public string? MatchLevel { get; set; }

    /// <summary>Name of the geocoding provider that produced the candidate.</summary>
    [JsonPropertyName("provider")]
    public string? Provider { get; set; }

    /// <summary>Failure reason when <see cref="Ok"/> is false.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// Structured output for <c>honua_geocode_addresses</c>.
/// </summary>
internal sealed class McpGeocodeAddressesOutput
{
    /// <summary>Per-address results, in the same order as the input addresses.</summary>
    [JsonPropertyName("results")]
    public IReadOnlyList<McpGeocodeAddressResult> Results { get; set; } = [];

    /// <summary>Number of addresses that resolved to a location.</summary>
    [JsonPropertyName("succeeded")]
    public int Succeeded { get; set; }

    /// <summary>Number of addresses that failed to resolve.</summary>
    [JsonPropertyName("failed")]
    public int Failed { get; set; }

    /// <summary>Spatial reference (SRID/WKID) of the returned coordinates.</summary>
    [JsonPropertyName("srid")]
    public int Srid { get; set; }
}

/// <summary>
/// A turn-by-turn direction step in a solved route. Wire projection of
/// <see cref="Honua.Routing.Features.Routing.Domain.RouteDirectionStep"/>.
/// </summary>
internal sealed class McpRouteDirectionStep
{
    /// <summary>Human-readable maneuver text.</summary>
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    /// <summary>Step length in meters.</summary>
    [JsonPropertyName("lengthMeters")]
    public double LengthMeters { get; set; }

    /// <summary>Step travel time in minutes.</summary>
    [JsonPropertyName("timeMinutes")]
    public double TimeMinutes { get; set; }

    /// <summary>Maneuver classifier (e.g. "depart", "straight", "arrive").</summary>
    [JsonPropertyName("maneuverType")]
    public string ManeuverType { get; set; } = string.Empty;
}

/// <summary>
/// Structured output for <c>honua_solve_route</c>.
/// </summary>
internal sealed class McpRouteOutput
{
    /// <summary>Name of the routing provider that solved the route.</summary>
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    /// <summary>Whether a route was found between the stops.</summary>
    [JsonPropertyName("solved")]
    public bool Solved { get; set; }

    /// <summary>Merged route geometry as a GeoJSON LineString string; null when unsolved.</summary>
    [JsonPropertyName("routeGeometryGeoJson")]
    public string? RouteGeometryGeoJson { get; set; }

    /// <summary>Total route length in meters.</summary>
    [JsonPropertyName("totalLengthMeters")]
    public double TotalLengthMeters { get; set; }

    /// <summary>Total travel time in minutes.</summary>
    [JsonPropertyName("totalTimeMinutes")]
    public double TotalTimeMinutes { get; set; }

    /// <summary>Ordered turn-by-turn steps; may be empty when the provider does not compute directions.</summary>
    [JsonPropertyName("directions")]
    public IReadOnlyList<McpRouteDirectionStep> Directions { get; set; } = [];
}
