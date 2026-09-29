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
        return !capabilities.SupportsRoute
            ? "Route solves are not supported by the configured routing provider."
            : ValidatePoints(request.Stops, 2, configuration.MaxStops, request.InSrid, request.OutSrid)
                ?? ValidateBarriers(request.Barriers, configuration.MaxBarriers)
                ?? ValidateCapabilities(capabilities, request.Barriers, request.TravelMode ?? request.TravelProfile);
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
        if (string.IsNullOrWhiteSpace(barrier.GeometryGeoJson))
        {
            return false;
        }
        try
        {
            using var document = JsonDocument.Parse(barrier.GeometryGeoJson);
            var geometry = document.RootElement;
            if (geometry.ValueKind != JsonValueKind.Object || !geometry.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String || !geometry.TryGetProperty("coordinates", out var coordinates))
            {
                return false;
            }
            return (barrier.Kind, type.GetString()) switch
            {
                (RouteBarrierKind.Point, "Point") => Position(coordinates),
                (RouteBarrierKind.Point, "MultiPoint") => ArrayOf(coordinates, 1, Position),
                (RouteBarrierKind.Line, "LineString") => Line(coordinates),
                (RouteBarrierKind.Line, "MultiLineString") => ArrayOf(coordinates, 1, Line),
                (RouteBarrierKind.Polygon, "Polygon") => Polygon(coordinates),
                (RouteBarrierKind.Polygon, "MultiPolygon") => ArrayOf(coordinates, 1, Polygon),
                _ => false,
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ArrayOf(JsonElement value, int minimum, Func<JsonElement, bool> validate)
        => value.ValueKind == JsonValueKind.Array && value.GetArrayLength() >= minimum && value.EnumerateArray().All(validate);

    private static bool Position(JsonElement value)
        => ArrayOf(value, 2, ordinate => ordinate.ValueKind == JsonValueKind.Number
            && ordinate.TryGetDouble(out var number) && double.IsFinite(number));

    private static bool Line(JsonElement value) => ArrayOf(value, 2, Position);

    private static bool Polygon(JsonElement value) => ArrayOf(value, 1, ring => ArrayOf(ring, 4, Position)
        && ring[0].EnumerateArray().Select(ordinate => ordinate.GetDouble())
            .SequenceEqual(ring[ring.GetArrayLength() - 1].EnumerateArray().Select(ordinate => ordinate.GetDouble())));

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
