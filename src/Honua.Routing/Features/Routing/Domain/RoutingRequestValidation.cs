// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Routing.Features.Routing.Domain;

/// <summary>Validates canonical routing inputs against the provider that will solve them.</summary>
public static class RoutingRequestValidation
{
    /// <summary>Validates a canonical route request, including worker-side input limits.</summary>
    public static string? ValidateRoute(RouteSolveRequest request, RoutingConfiguration configuration, RoutingProviderCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(capabilities);
        var travelMode = request.TravelMode ?? request.TravelProfile;
        // The legacy driving profile denotes the default for providers with no named modes.
        // An explicit TravelMode must still be advertised, even when it is "driving".
        if (request.TravelMode is null && capabilities.SupportedTravelModes.Count == 0
            && string.Equals(travelMode, "driving", StringComparison.OrdinalIgnoreCase))
        {
            travelMode = null;
        }
        return !capabilities.SupportsRoute
            ? "Route solves are not supported by the configured routing provider."
            : ValidatePoints(request.Stops, 2, configuration.MaxStops, request.InSrid, request.OutSrid)
                ?? ValidateBarriers(request.Barriers, configuration.MaxBarriers)
                ?? ValidateCapabilities(capabilities, request.Barriers, travelMode);
    }

    /// <summary>Validates a canonical service-area request, including worker-side input limits.</summary>
    public static string? ValidateServiceArea(ServiceAreaSolveRequest request, RoutingConfiguration configuration, RoutingProviderCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (!capabilities.SupportsServiceArea)
        {
            return "Service-area solves are not supported by the configured routing provider.";
        }
        if (request.Breaks is null || request.Breaks.Count == 0 || request.Breaks.Count > configuration.MaxBreaks
            || request.Breaks.Any(value => !double.IsFinite(value) || value <= 0)
            || !request.Breaks.SequenceEqual(request.Breaks.Distinct().Order()))
        {
            return "Service-area breaks must be distinct positive minutes in ascending order within the configured limit.";
        }
        if (!Enum.IsDefined(request.TravelDirection) || !capabilities.SupportedTravelDirections.Contains(request.TravelDirection))
        {
            return "Service-area travel direction is invalid.";
        }
        return ValidatePoints(request.Facilities, 1, configuration.MaxFacilities, request.InSrid, request.OutSrid)
            ?? ValidateBarriers(request.Barriers, configuration.MaxBarriers)
            ?? ValidateCapabilities(capabilities, request.Barriers, request.TravelMode);
    }

    private static string? ValidatePoints(IReadOnlyList<RoutePoint>? points, int minimum, int maximum, int inSrid, int outSrid)
        => points is null || points.Count < minimum || points.Count > maximum
            ? $"Routing requires between {minimum} and {maximum} input points."
            : inSrid <= 0 || outSrid <= 0
                ? "Routing spatial references must be positive identifiers."
                : points.Any(point => !double.IsFinite(point.Lon) || !double.IsFinite(point.Lat))
                    ? "Routing point coordinates must be finite."
                    : null;

    private static string? ValidateBarriers(IReadOnlyList<RouteBarrier>? barriers, int maximum)
        => barriers is null || barriers.Count > maximum || barriers.Any(barrier => barrier is null || !IsValidBarrierGeometry(barrier))
            ? "Routing barriers must contain valid geometries within the configured limit."
            : null;

    private static bool IsValidBarrierGeometry(RouteBarrier barrier)
    {
        if (barrier.Kind == RouteBarrierKind.Line)
        {
            return NetworkTopologyEditValidation.TryValidateLinearGeoJson(barrier.GeometryGeoJson, out _);
        }
        if (string.IsNullOrWhiteSpace(barrier.GeometryGeoJson))
        {
            return false;
        }
        try
        {
            using var document = JsonDocument.Parse(barrier.GeometryGeoJson);
            var geometry = document.RootElement;
            if (geometry.ValueKind != JsonValueKind.Object
                || !geometry.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
                || !geometry.TryGetProperty("coordinates", out var coordinates))
            {
                return false;
            }
            return (barrier.Kind, type.GetString()) switch
            {
                (RouteBarrierKind.Point, "Point") => IsPosition(coordinates),
                (RouteBarrierKind.Point, "MultiPoint") => IsNonEmptyArrayOf(coordinates, IsPosition),
                (RouteBarrierKind.Polygon, "Polygon") => IsPolygon(coordinates),
                (RouteBarrierKind.Polygon, "MultiPolygon") => IsNonEmptyArrayOf(coordinates, IsPolygon),
                _ => false,
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsPosition(JsonElement position)
        => position.ValueKind == JsonValueKind.Array && position.GetArrayLength() >= 2
            && position.EnumerateArray().All(ordinate => ordinate.ValueKind == JsonValueKind.Number
                && ordinate.TryGetDouble(out var value) && double.IsFinite(value));

    private static bool IsNonEmptyArrayOf(JsonElement coordinates, Func<JsonElement, bool> validate)
        => coordinates.ValueKind == JsonValueKind.Array && coordinates.GetArrayLength() > 0
            && coordinates.EnumerateArray().All(validate);

    private static bool IsPolygon(JsonElement coordinates)
        => IsNonEmptyArrayOf(coordinates, IsLinearRing);

    private static bool IsLinearRing(JsonElement ring)
    {
        // RFC 7946 section 3.1.6: at least four positions, with identical first/last values.
        // This checks structure and finite coordinates; it does not claim topological validity.
        if (ring.ValueKind != JsonValueKind.Array || ring.GetArrayLength() < 4
            || !ring.EnumerateArray().All(IsPosition))
        {
            return false;
        }
        return ring[0].EnumerateArray().Select(ordinate => ordinate.GetDouble())
            .SequenceEqual(ring[ring.GetArrayLength() - 1].EnumerateArray().Select(ordinate => ordinate.GetDouble()));
    }

    /// <summary>
    /// Returns an error for an unsupported barrier kind or travel mode, or null when
    /// the provider supports the inputs. An absent travel mode uses the provider default.
    /// </summary>
    public static string? ValidateCapabilities(
        RoutingProviderCapabilities capabilities,
        IReadOnlyList<RouteBarrier> barriers,
        string? travelMode)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(barriers);

        foreach (var kind in barriers.Select(b => b.Kind).Distinct())
        {
            if (!capabilities.SupportedBarrierKinds.Contains(kind))
            {
                return $"{kind} barriers are not supported by the configured routing provider.";
            }
        }

        if (!string.IsNullOrWhiteSpace(travelMode) &&
            !capabilities.SupportedTravelModes.Any(m => string.Equals(m, travelMode, StringComparison.OrdinalIgnoreCase)))
        {
            return $"travelMode '{travelMode}' is not supported by the configured routing provider.";
        }

        return null;
    }
}
