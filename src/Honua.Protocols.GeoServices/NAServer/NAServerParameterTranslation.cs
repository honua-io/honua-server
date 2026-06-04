// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Routing.Features.Routing.Domain;

namespace Honua.Protocols.GeoServices.NAServer;

/// <summary>
/// Caps on input counts enforced by the NAServer adapter to bound serial DB
/// fan-out (DoS guard). Sourced from <see cref="RoutingConfiguration"/>.
/// </summary>
/// <param name="MaxStops">Maximum number of stops on a route solve.</param>
/// <param name="MaxFacilities">Maximum number of facilities on a service-area solve.</param>
/// <param name="MaxBreaks">Maximum number of distinct breaks on a service-area solve.</param>
internal readonly record struct NAServerInputCaps(int MaxStops, int MaxFacilities, int MaxBreaks)
{
    /// <summary>
    /// Conservative defaults used when no <see cref="RoutingConfiguration"/> is
    /// supplied (e.g. focused unit tests that do not exercise the cap path).
    /// </summary>
    public static NAServerInputCaps Default => new(1000, 1000, 50);

    /// <summary>
    /// Builds caps from the bound routing configuration.
    /// </summary>
    /// <param name="configuration">Routing configuration.</param>
    /// <returns>Input caps mirroring the configuration's Max* values.</returns>
    public static NAServerInputCaps FromConfiguration(RoutingConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new NAServerInputCaps(configuration.MaxStops, configuration.MaxFacilities, configuration.MaxBreaks);
    }
}

/// <summary>
/// Translates Esri NAServer request parameters into the canonical, protocol-neutral
/// <see cref="Honua.Routing"/> request contracts. Per ADR-0029, parameter parsing
/// and validation are the adapter's responsibility; routing itself stays in the
/// shared <c>IRoutingProvider</c> pipeline.
/// </summary>
internal static class NAServerParameterTranslation
{
    /// <summary>Default output spatial reference (WGS84) when none is supplied.</summary>
    public const int DefaultSrid = 4326;

    /// <summary>
    /// Raised when a request parameter cannot be translated to a canonical value.
    /// The adapter maps this to a GeoServices 400 error envelope.
    /// </summary>
    internal sealed class NAServerParameterException(string message) : Exception(message);

    /// <summary>
    /// Builds a canonical <see cref="RouteSolveRequest"/> from raw NAServer
    /// parameters using default input caps. Requires at least two stops.
    /// </summary>
    public static RouteSolveRequest BuildRouteSolveRequest(IReadOnlyDictionary<string, string> parameters)
        => BuildRouteSolveRequest(parameters, NAServerInputCaps.Default);

    /// <summary>
    /// Builds a canonical <see cref="RouteSolveRequest"/> from raw NAServer
    /// parameters. Requires at least two stops and enforces the supplied stop cap.
    /// </summary>
    public static RouteSolveRequest BuildRouteSolveRequest(
        IReadOnlyDictionary<string, string> parameters,
        NAServerInputCaps caps)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var outSrid = ParseOutSr(parameters);
        var inSrid = ParseInSr(parameters, outSrid);
        var stops = ParsePoints(GetValue(parameters, "stops"), "stops");
        if (stops.Count < 2)
        {
            throw new NAServerParameterException(
                "At least two 'stops' are required to solve a route.");
        }

        if (stops.Count > caps.MaxStops)
        {
            throw new NAServerParameterException(
                $"'stops' count {stops.Count} exceeds the maximum of {caps.MaxStops}.");
        }

        return new RouteSolveRequest(stops, OutSrid: outSrid) { InSrid = inSrid };
    }

    /// <summary>
    /// Builds a canonical <see cref="ServiceAreaSolveRequest"/> from raw NAServer
    /// parameters using default input caps. Requires at least one facility and one
    /// positive break.
    /// </summary>
    public static ServiceAreaSolveRequest BuildServiceAreaSolveRequest(IReadOnlyDictionary<string, string> parameters)
        => BuildServiceAreaSolveRequest(parameters, NAServerInputCaps.Default);

    /// <summary>
    /// Builds a canonical <see cref="ServiceAreaSolveRequest"/> from raw NAServer
    /// parameters. Requires at least one facility and one positive break, and
    /// enforces the supplied facility/break caps.
    /// </summary>
    public static ServiceAreaSolveRequest BuildServiceAreaSolveRequest(
        IReadOnlyDictionary<string, string> parameters,
        NAServerInputCaps caps)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var outSrid = ParseOutSr(parameters);
        var inSrid = ParseInSr(parameters, outSrid);
        var facilities = ParsePoints(GetValue(parameters, "facilities"), "facilities");
        if (facilities.Count == 0)
        {
            throw new NAServerParameterException(
                "At least one 'facilities' point is required to solve a service area.");
        }

        if (facilities.Count > caps.MaxFacilities)
        {
            throw new NAServerParameterException(
                $"'facilities' count {facilities.Count} exceeds the maximum of {caps.MaxFacilities}.");
        }

        var breaks = ParseBreaks(GetValue(parameters, "defaultBreaks"));
        if (breaks.Count == 0)
        {
            throw new NAServerParameterException(
                "At least one positive value in 'defaultBreaks' is required to solve a service area.");
        }

        if (breaks.Count > caps.MaxBreaks)
        {
            throw new NAServerParameterException(
                $"'defaultBreaks' count {breaks.Count} exceeds the maximum of {caps.MaxBreaks}.");
        }

        var travelDirection = ParseTravelDirection(GetValue(parameters, "travelDirection"));
        return new ServiceAreaSolveRequest(facilities, breaks, travelDirection, outSrid) { InSrid = inSrid };
    }

    private static string? GetValue(IReadOnlyDictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var value) ? value : null;

    /// <summary>
    /// Reads the output spatial reference from <c>outSR</c>. Accepts a bare WKID or a
    /// spatial-reference JSON object (<c>{ "wkid": 3857 }</c>). Defaults to 4326.
    /// </summary>
    private static int ParseOutSr(IReadOnlyDictionary<string, string> parameters)
        => ParseSpatialReference(parameters, "outSR") ?? DefaultSrid;

    /// <summary>
    /// Reads the input spatial reference from <c>inSR</c>, falling back to the
    /// resolved output SRID when <c>inSR</c> is absent. Input ordinates are
    /// interpreted in this SRID and transformed to the graph SRID before snapping.
    /// </summary>
    private static int ParseInSr(IReadOnlyDictionary<string, string> parameters, int outSrid)
        => ParseSpatialReference(parameters, "inSR") ?? outSrid;

    /// <summary>
    /// Reads a spatial-reference parameter. Accepts a bare WKID or a
    /// spatial-reference JSON object (<c>{ "wkid": 3857 }</c> / <c>latestWkid</c>).
    /// Returns <c>null</c> when the parameter is absent/empty; throws on a malformed
    /// value.
    /// </summary>
    private static int? ParseSpatialReference(IReadOnlyDictionary<string, string> parameters, string key)
    {
        var value = GetValue(parameters, key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bare) && bare > 0)
        {
            return bare;
        }

        if (trimmed[0] == '{')
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("wkid", out var wkid) && wkid.ValueKind == JsonValueKind.Number)
                    {
                        return wkid.GetInt32();
                    }

                    if (root.TryGetProperty("latestWkid", out var latest) && latest.ValueKind == JsonValueKind.Number)
                    {
                        return latest.GetInt32();
                    }
                }
            }
            catch (JsonException)
            {
                throw new NAServerParameterException(
                    $"'{key}' value '{value}' is not a valid WKID or spatial-reference object.");
            }
        }

        throw new NAServerParameterException(
            $"'{key}' value '{value}' is not a valid WKID or spatial-reference object.");
    }

    /// <summary>
    /// Maps the Esri <c>travelDirection</c> token to the canonical enum. Accepts the
    /// Esri string constants and the numeric forms (0 = from facility, 1 = to facility).
    /// </summary>
    public static ServiceAreaTravelDirection ParseTravelDirection(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ServiceAreaTravelDirection.FromFacility;
        }

        var trimmed = value.Trim();
        if (string.Equals(trimmed, "esriNATravelDirectionToFacility", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "1", StringComparison.Ordinal))
        {
            return ServiceAreaTravelDirection.ToFacility;
        }

        if (string.Equals(trimmed, "esriNATravelDirectionFromFacility", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "0", StringComparison.Ordinal))
        {
            return ServiceAreaTravelDirection.FromFacility;
        }

        throw new NAServerParameterException(
            $"'travelDirection' value '{value}' is not recognized. Use " +
            "'esriNATravelDirectionFromFacility' or 'esriNATravelDirectionToFacility'.");
    }

    /// <summary>
    /// Parses a <c>defaultBreaks</c> string (e.g. <c>"5,10,15"</c>) into an ascending,
    /// de-duplicated list of positive minute cutoffs.
    /// </summary>
    public static IReadOnlyList<double> ParseBreaks(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var parts = value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var breaks = new SortedSet<double>();
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                throw new NAServerParameterException(
                    $"'defaultBreaks' value '{part}' is not a valid number.");
            }

            if (parsed > 0)
            {
                breaks.Add(parsed);
            }
        }

        return [.. breaks];
    }

    /// <summary>
    /// Parses a stops/facilities parameter into canonical <see cref="RoutePoint"/>s.
    /// Supports the Esri FeatureSet JSON form (<c>{ "features": [{ "geometry": { "x", "y" } }] }</c>),
    /// the geometry-collection form (<c>{ "geometries": [{ "x", "y" }] }</c>), a bare
    /// points array (<c>{ "points": [[x, y], ...] }</c>), and the simple
    /// <c>"x,y; x,y"</c> delimited fallback.
    /// </summary>
    public static IReadOnlyList<RoutePoint> ParsePoints(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var trimmed = value.Trim();
        if (trimmed[0] == '{' || trimmed[0] == '[')
        {
            return ParseJsonPoints(trimmed, parameterName);
        }

        return ParseDelimitedPoints(trimmed, parameterName);
    }

    private static List<RoutePoint> ParseJsonPoints(string json, string parameterName)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new NAServerParameterException(
                $"'{parameterName}' is not valid JSON: {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            var points = new List<RoutePoint>();

            // FeatureSet: { "features": [ { "geometry": { "x", "y" } }, ... ] }
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("features", out var features) &&
                features.ValueKind == JsonValueKind.Array)
            {
                foreach (var feature in features.EnumerateArray())
                {
                    if (feature.ValueKind == JsonValueKind.Object &&
                        feature.TryGetProperty("geometry", out var geometry) &&
                        TryReadXy(geometry, out var point))
                    {
                        points.Add(point);
                    }
                }

                return points;
            }

            // Geometry collection: { "geometries": [ { "x", "y" }, ... ] }
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("geometries", out var geometries) &&
                geometries.ValueKind == JsonValueKind.Array)
            {
                foreach (var geometry in geometries.EnumerateArray())
                {
                    if (TryReadXy(geometry, out var point))
                    {
                        points.Add(point);
                    }
                }

                return points;
            }

            // Multipoint: { "points": [ [x, y], ... ] }
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("points", out var multipoint) &&
                multipoint.ValueKind == JsonValueKind.Array)
            {
                foreach (var coordinate in multipoint.EnumerateArray())
                {
                    if (TryReadCoordinatePair(coordinate, out var point))
                    {
                        points.Add(point);
                    }
                }

                return points;
            }

            // Bare single point: { "x", "y" }
            if (TryReadXy(root, out var single))
            {
                points.Add(single);
                return points;
            }

            // Bare coordinate array: [ [x, y], ... ] or [x, y]
            if (root.ValueKind == JsonValueKind.Array)
            {
                if (TryReadCoordinatePair(root, out var bare))
                {
                    points.Add(bare);
                    return points;
                }

                foreach (var coordinate in root.EnumerateArray())
                {
                    if (TryReadCoordinatePair(coordinate, out var point))
                    {
                        points.Add(point);
                    }
                }

                return points;
            }

            throw new NAServerParameterException(
                $"'{parameterName}' JSON did not contain any recognizable points.");
        }
    }

    private static bool TryReadXy(JsonElement element, out RoutePoint point)
    {
        point = default;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("x", out var x) || x.ValueKind != JsonValueKind.Number ||
            !element.TryGetProperty("y", out var y) || y.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        point = new RoutePoint(x.GetDouble(), y.GetDouble());
        return true;
    }

    private static bool TryReadCoordinatePair(JsonElement element, out RoutePoint point)
    {
        point = default;
        if (element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var ordinates = new List<double>(2);
        foreach (var ordinate in element.EnumerateArray())
        {
            if (ordinate.ValueKind != JsonValueKind.Number)
            {
                return false;
            }

            ordinates.Add(ordinate.GetDouble());
            if (ordinates.Count == 2)
            {
                break;
            }
        }

        if (ordinates.Count < 2)
        {
            return false;
        }

        point = new RoutePoint(ordinates[0], ordinates[1]);
        return true;
    }

    private static List<RoutePoint> ParseDelimitedPoints(string value, string parameterName)
    {
        var points = new List<RoutePoint>();
        var pairs = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var pair in pairs)
        {
            var ordinates = pair.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (ordinates.Length < 2 ||
                !double.TryParse(ordinates[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !double.TryParse(ordinates[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            {
                throw new NAServerParameterException(
                    $"'{parameterName}' entry '{pair}' is not a valid 'x,y' point.");
            }

            points.Add(new RoutePoint(x, y));
        }

        return points;
    }
}
