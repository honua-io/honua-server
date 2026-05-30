// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.NAServer.Models;

/// <summary>
/// Minimal NAServer route solve response.
/// </summary>
internal sealed class NAServerRouteSolveResponse
{
    /// <summary>Route feature set.</summary>
    public NAServerRouteFeatureSet Routes { get; init; } = new();

    /// <summary>Turn-by-turn directions.</summary>
    public NAServerDirection[] Directions { get; init; } = [];
}

/// <summary>
/// Minimal NAServer closest facility response.
/// </summary>
internal sealed class NAServerClosestFacilityResponse
{
    /// <summary>Closest-facility route feature set.</summary>
    public NAServerRouteFeatureSet? Routes { get; init; }

    /// <summary>Closest-facility directions.</summary>
    public NAServerDirection[] Directions { get; init; } = [];
}

/// <summary>
/// Empty service-area response accepted by the first mobile routing contract.
/// </summary>
internal sealed class NAServerServiceAreaResponse
{
}

/// <summary>
/// GeoServices feature set carrying route features.
/// </summary>
internal sealed class NAServerRouteFeatureSet
{
    /// <summary>Route features.</summary>
    public NAServerRouteFeature[] Features { get; init; } = [];
}

/// <summary>
/// Route feature wrapper.
/// </summary>
internal sealed class NAServerRouteFeature
{
    /// <summary>Route attributes.</summary>
    public NAServerRouteAttributes Attributes { get; init; } = new();
}

/// <summary>
/// Route attributes parsed by first-party mobile clients.
/// </summary>
internal sealed class NAServerRouteAttributes
{
    /// <summary>Route name.</summary>
    [JsonPropertyName("Name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Total route length.</summary>
    [JsonPropertyName("Total_Length")]
    public double TotalLength { get; init; }

    /// <summary>Total route travel time.</summary>
    [JsonPropertyName("Total_Time")]
    public double TotalTime { get; init; }
}

/// <summary>
/// Direction result wrapper.
/// </summary>
internal sealed class NAServerDirection
{
    /// <summary>Direction features.</summary>
    public NAServerDirectionFeature[]? Features { get; init; }

    /// <summary>Direction summary.</summary>
    public NAServerDirectionSummary? Summary { get; init; }
}

/// <summary>
/// Direction feature wrapper.
/// </summary>
internal sealed class NAServerDirectionFeature
{
    /// <summary>Direction attributes.</summary>
    public NAServerDirectionAttributes Attributes { get; init; } = new();
}

/// <summary>
/// Turn-by-turn direction attributes parsed by first-party mobile clients.
/// </summary>
internal sealed class NAServerDirectionAttributes
{
    /// <summary>Instruction text.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Segment length.</summary>
    public double Length { get; init; }

    /// <summary>Segment travel time.</summary>
    public double Time { get; init; }

    /// <summary>Esri maneuver type.</summary>
    public string ManeuverType { get; init; } = string.Empty;
}

/// <summary>
/// Closest-facility direction summary.
/// </summary>
internal sealed class NAServerDirectionSummary
{
    /// <summary>Route name.</summary>
    public string RouteName { get; init; } = string.Empty;

    /// <summary>Total length.</summary>
    public double TotalLength { get; init; }

    /// <summary>Total travel time.</summary>
    public double TotalTime { get; init; }
}
