// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Ai.Protocols.Mcp.Location;

// -----------------------------------------------------------------------
// Tool argument shapes (tools/call params.arguments)
// -----------------------------------------------------------------------

internal sealed class McpGeocodeArgument
{
    [JsonPropertyName("address")]
    public string? Address { get; set; }

    [JsonPropertyName("maxResults")]
    public int? MaxResults { get; set; }

    [JsonPropertyName("countryCodes")]
    public string? CountryCodes { get; set; }

    [JsonPropertyName("outSrid")]
    public int? OutSrid { get; set; }

    [JsonPropertyName("provider")]
    public string? Provider { get; set; }
}

internal sealed class McpGeocodeAddressesArgument
{
    [JsonPropertyName("addresses")]
    public IReadOnlyList<string?>? Addresses { get; set; }

    [JsonPropertyName("countryCodes")]
    public string? CountryCodes { get; set; }

    [JsonPropertyName("outSrid")]
    public int? OutSrid { get; set; }

    [JsonPropertyName("provider")]
    public string? Provider { get; set; }
}

internal sealed class McpRouteStopInput
{
    [JsonPropertyName("lon")]
    public double? Lon { get; set; }

    [JsonPropertyName("lat")]
    public double? Lat { get; set; }
}

internal sealed class McpRouteArgument
{
    [JsonPropertyName("stops")]
    public IReadOnlyList<McpRouteStopInput?>? Stops { get; set; }

    [JsonPropertyName("travelProfile")]
    public string? TravelProfile { get; set; }

    [JsonPropertyName("outSrid")]
    public int? OutSrid { get; set; }

    [JsonPropertyName("inSrid")]
    public int? InSrid { get; set; }
}

// -----------------------------------------------------------------------
// Tool output shapes (tools/call result.structuredContent)
// -----------------------------------------------------------------------

internal sealed class McpGeocodeCandidate
{
    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("score")]
    public double Score { get; set; }

    [JsonPropertyName("srid")]
    public int Srid { get; set; }

    [JsonPropertyName("matchLevel")]
    public string? MatchLevel { get; set; }

    [JsonPropertyName("addressType")]
    public string? AddressType { get; set; }
}

internal sealed class McpGeocodeOutput
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("candidates")]
    public IReadOnlyList<McpGeocodeCandidate> Candidates { get; set; } = [];
}

internal sealed class McpGeocodeAddressLocation
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("srid")]
    public int Srid { get; set; }
}

internal sealed class McpGeocodeAddressResult
{
    [JsonPropertyName("input")]
    public string Input { get; set; } = string.Empty;

    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("location")]
    public McpGeocodeAddressLocation? Location { get; set; }

    [JsonPropertyName("score")]
    public double? Score { get; set; }

    [JsonPropertyName("matchedAddress")]
    public string? MatchedAddress { get; set; }

    [JsonPropertyName("matchLevel")]
    public string? MatchLevel { get; set; }

    [JsonPropertyName("provider")]
    public string? Provider { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

internal sealed class McpGeocodeAddressesOutput
{
    [JsonPropertyName("results")]
    public IReadOnlyList<McpGeocodeAddressResult> Results { get; set; } = [];

    [JsonPropertyName("succeeded")]
    public int Succeeded { get; set; }

    [JsonPropertyName("failed")]
    public int Failed { get; set; }

    [JsonPropertyName("srid")]
    public int Srid { get; set; }
}

internal sealed class McpRouteDirectionStep
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("lengthMeters")]
    public double LengthMeters { get; set; }

    [JsonPropertyName("timeMinutes")]
    public double TimeMinutes { get; set; }

    [JsonPropertyName("maneuverType")]
    public string ManeuverType { get; set; } = string.Empty;
}

internal sealed class McpRouteOutput
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("solved")]
    public bool Solved { get; set; }

    [JsonPropertyName("routeGeometryGeoJson")]
    public string? RouteGeometryGeoJson { get; set; }

    [JsonPropertyName("totalLengthMeters")]
    public double TotalLengthMeters { get; set; }

    [JsonPropertyName("totalTimeMinutes")]
    public double TotalTimeMinutes { get; set; }

    [JsonPropertyName("directions")]
    public IReadOnlyList<McpRouteDirectionStep> Directions { get; set; } = [];
}
