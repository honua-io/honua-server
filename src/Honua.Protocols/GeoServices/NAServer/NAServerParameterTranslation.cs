// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Shared.Models;
using Honua.Routing.Features.Routing.Domain;

namespace Honua.Protocols.GeoServices.NAServer;

internal readonly record struct NAServerInputCaps(
    int MaxStops,
    int MaxFacilities,
    int MaxBreaks,
    int MaxBarriers,
    int MaxIncidents = 1000,
    int MaxClosestFacilities = 1000,
    int MaxOrigins = 1000,
    int MaxDestinations = 1000,
    int MaxLocationAllocationFacilities = 1000,
    int MaxDemandPoints = 1000,
    int MaxSolvePairs = 10_000)
{
    public static NAServerInputCaps Default => new(1000, 1000, 50, 1000);

    public static NAServerInputCaps FromConfiguration(RoutingConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new NAServerInputCaps(
            configuration.MaxStops,
            configuration.MaxFacilities,
            configuration.MaxBreaks,
            configuration.MaxBarriers,
            configuration.MaxIncidents,
            configuration.MaxClosestFacilities,
            configuration.MaxOrigins,
            configuration.MaxDestinations,
            configuration.MaxLocationAllocationFacilities,
            configuration.MaxDemandPoints,
            configuration.MaxSolvePairs);
    }
}

internal static class NAServerParameterTranslation
{
    public const int DefaultSrid = 4326;

    internal sealed class NAServerParameterException(string message) : Exception(message);

    public static RouteSolveRequest BuildRouteSolveRequest(IReadOnlyDictionary<string, string> parameters)
        => BuildRouteSolveRequest(parameters, NAServerInputCaps.Default);

    public static RouteSolveRequest BuildRouteSolveRequest(
        IReadOnlyDictionary<string, string> parameters,
        NAServerInputCaps caps)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var outSrid = ParseOutSr(parameters);
        var inSrid = ParseInSr(parameters, outSrid, "stops");
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

        var barriers = ParseBarriers(parameters, caps);
        var travelMode = ParseTravelMode(parameters);

        return new RouteSolveRequest(stops, OutSrid: outSrid)
        {
            InSrid = inSrid,
            Barriers = barriers,
            TravelMode = travelMode,
        };
    }

    public static ServiceAreaSolveRequest BuildServiceAreaSolveRequest(IReadOnlyDictionary<string, string> parameters)
        => BuildServiceAreaSolveRequest(parameters, NAServerInputCaps.Default);

    public static ServiceAreaSolveRequest BuildServiceAreaSolveRequest(
        IReadOnlyDictionary<string, string> parameters,
        NAServerInputCaps caps)
        => BuildServiceAreaSolveRequest(parameters, caps, defaultInputSrid: null);

    internal static ServiceAreaSolveRequest BuildServiceAreaSolveRequest(
        IReadOnlyDictionary<string, string> parameters,
        NAServerInputCaps caps,
        int? defaultInputSrid)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var outSrid = ParseOutSr(parameters);
        var inSrid = ParseInSr(parameters, defaultInputSrid ?? outSrid, "facilities");
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
        var barriers = ParseBarriers(parameters, caps);
        var travelMode = ParseTravelMode(parameters);

        return new ServiceAreaSolveRequest(facilities, breaks, travelDirection, outSrid)
        {
            InSrid = inSrid,
            Barriers = barriers,
            TravelMode = travelMode,
        };
    }

    public static ClosestFacilitySolveRequest BuildClosestFacilitySolveRequest(
        IReadOnlyDictionary<string, string> parameters)
        => BuildClosestFacilitySolveRequest(parameters, NAServerInputCaps.Default);

    public static ClosestFacilitySolveRequest BuildClosestFacilitySolveRequest(
        IReadOnlyDictionary<string, string> parameters,
        NAServerInputCaps caps)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var outSrid = ParseOutSr(parameters);
        var inSrid = ParseInSr(parameters, outSrid, "incidents", "facilities");

        var incidents = ParsePoints(GetValue(parameters, "incidents"), "incidents");
        if (incidents.Count == 0)
        {
            throw new NAServerParameterException(
                "At least one 'incidents' point is required to solve closest facility.");
        }

        if (incidents.Count > caps.MaxIncidents)
        {
            throw new NAServerParameterException(
                $"'incidents' count {incidents.Count} exceeds the maximum of {caps.MaxIncidents}.");
        }

        var facilities = ParsePoints(GetValue(parameters, "facilities"), "facilities");
        if (facilities.Count == 0)
        {
            throw new NAServerParameterException(
                "At least one 'facilities' point is required to solve closest facility.");
        }

        if (facilities.Count > caps.MaxClosestFacilities)
        {
            throw new NAServerParameterException(
                $"'facilities' count {facilities.Count} exceeds the maximum of {caps.MaxClosestFacilities}.");
        }

        var targetCount = ParsePositiveInt(GetValue(parameters, "defaultTargetFacilityCount"), "defaultTargetFacilityCount") ?? 1;
        if (targetCount > caps.MaxClosestFacilities)
        {
            throw new NAServerParameterException(
                $"'defaultTargetFacilityCount' {targetCount} exceeds the maximum of {caps.MaxClosestFacilities}.");
        }

        EnsureSolvePairs(
            RoutingSolveBudget.RankedPairCount(incidents.Count, facilities.Count, targetCount),
            caps,
            "Closest facility");
        var cutoff = ParseCutoff(parameters, "defaultCutoff", "cutoff");
        var direction = ParseClosestFacilityTravelDirection(GetValue(parameters, "travelDirection"));
        var barriers = ParseBarriers(parameters, caps);
        var travelMode = ParseTravelMode(parameters);

        return new ClosestFacilitySolveRequest(
            incidents,
            facilities,
            targetCount,
            cutoff,
            direction,
            outSrid)
        {
            InSrid = inSrid,
            Barriers = barriers,
            TravelMode = travelMode,
        };
    }

    public static OdCostMatrixSolveRequest BuildOdCostMatrixSolveRequest(
        IReadOnlyDictionary<string, string> parameters)
        => BuildOdCostMatrixSolveRequest(parameters, NAServerInputCaps.Default);

    public static OdCostMatrixSolveRequest BuildOdCostMatrixSolveRequest(
        IReadOnlyDictionary<string, string> parameters,
        NAServerInputCaps caps)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var outputType = ParseOdOutputType(GetValue(parameters, "outputType"));

        var outSrid = ParseOutSr(parameters);
        var inSrid = ParseInSr(parameters, outSrid, "origins", "destinations");

        var origins = ParsePoints(GetValue(parameters, "origins"), "origins");
        if (origins.Count == 0)
        {
            throw new NAServerParameterException(
                "At least one 'origins' point is required to solve an OD cost matrix.");
        }

        if (origins.Count > caps.MaxOrigins)
        {
            throw new NAServerParameterException(
                $"'origins' count {origins.Count} exceeds the maximum of {caps.MaxOrigins}.");
        }

        var destinations = ParsePoints(GetValue(parameters, "destinations"), "destinations");
        if (destinations.Count == 0)
        {
            throw new NAServerParameterException(
                "At least one 'destinations' point is required to solve an OD cost matrix.");
        }

        if (destinations.Count > caps.MaxDestinations)
        {
            throw new NAServerParameterException(
                $"'destinations' count {destinations.Count} exceeds the maximum of {caps.MaxDestinations}.");
        }

        var cutoff = ParseCutoff(parameters, "defaultCutoff", "cutoff");
        var destinationCount = ParsePositiveInt(
            GetValue(parameters, "defaultTargetDestinationCount"), "defaultTargetDestinationCount");
        if (destinationCount is > 0 && destinationCount > caps.MaxDestinations)
        {
            throw new NAServerParameterException(
                $"'defaultTargetDestinationCount' {destinationCount} exceeds the maximum of {caps.MaxDestinations}.");
        }

        var rankedDestinations = destinationCount is > 0
            ? Math.Min(destinationCount.Value, destinations.Count)
            : destinations.Count;
        EnsureSolvePairs(
            RoutingSolveBudget.RankedPairCount(origins.Count, destinations.Count, rankedDestinations),
            caps,
            "OD cost matrix");
        var barriers = ParseBarriers(parameters, caps);
        var travelMode = ParseTravelMode(parameters);

        return new OdCostMatrixSolveRequest(origins, destinations, outSrid)
        {
            InSrid = inSrid,
            Cutoff = cutoff,
            DestinationCount = destinationCount,
            Barriers = barriers,
            TravelMode = travelMode,
            OutputType = outputType,
        };
    }

    public static LocationAllocationSolveRequest BuildLocationAllocationSolveRequest(
        IReadOnlyDictionary<string, string> parameters)
        => BuildLocationAllocationSolveRequest(parameters, NAServerInputCaps.Default);

    public static LocationAllocationSolveRequest BuildLocationAllocationSolveRequest(
        IReadOnlyDictionary<string, string> parameters,
        NAServerInputCaps caps)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var outSrid = ParseOutSr(parameters);
        var inSrid = ParseInSr(parameters, outSrid, "facilities", "demandPoints");

        var facilities = ParsePoints(GetValue(parameters, "facilities"), "facilities");
        if (facilities.Count == 0)
        {
            throw new NAServerParameterException(
                "At least one candidate 'facilities' point is required to solve location-allocation.");
        }

        if (facilities.Count > caps.MaxLocationAllocationFacilities)
        {
            throw new NAServerParameterException(
                $"'facilities' count {facilities.Count} exceeds the maximum of {caps.MaxLocationAllocationFacilities}.");
        }

        var demandPoints = ParseDemandPoints(GetValue(parameters, "demandPoints"));
        if (demandPoints.Count == 0)
        {
            throw new NAServerParameterException(
                "At least one 'demandPoints' point is required to solve location-allocation.");
        }

        if (demandPoints.Count > caps.MaxDemandPoints)
        {
            throw new NAServerParameterException(
                $"'demandPoints' count {demandPoints.Count} exceeds the maximum of {caps.MaxDemandPoints}.");
        }

        EnsureSolvePairs(
            RoutingSolveBudget.RankedPairCount(facilities.Count, demandPoints.Count, null),
            caps,
            "Location allocation");

        var problemType = ParseLocationAllocationProblemType(GetValue(parameters, "problemType"));
        var facilitiesToFind = ParsePositiveInt(
            GetValue(parameters, "numberFacilitiesToFind"), "numberFacilitiesToFind") ?? 1;
        var cutoff = ParseCutoff(parameters, "impedanceCutoff", "defaultCutoff");
        if (problemType == LocationAllocationProblemType.MinimizeFacilities && cutoff is null)
        {
            throw new NAServerParameterException(
                "location-allocation problem type 'esriMFPMinimizeFacilities' requires " +
                "'impedanceCutoff' or 'defaultCutoff' so demand coverage is bounded.");
        }

        var barriers = ParseBarriers(parameters, caps);
        var travelMode = ParseTravelMode(parameters);

        return new LocationAllocationSolveRequest(
            facilities,
            demandPoints,
            problemType,
            facilitiesToFind,
            cutoff,
            outSrid)
        {
            InSrid = inSrid,
            Barriers = barriers,
            TravelMode = travelMode,
        };
    }

    public static ClosestFacilityTravelDirection ParseClosestFacilityTravelDirection(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ClosestFacilityTravelDirection.ToFacility;
        }

        var trimmed = value.Trim();
        if (string.Equals(trimmed, "esriNATravelDirectionFromFacility", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "1", StringComparison.Ordinal))
        {
            return ClosestFacilityTravelDirection.FromFacility;
        }

        if (string.Equals(trimmed, "esriNATravelDirectionToFacility", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "0", StringComparison.Ordinal))
        {
            return ClosestFacilityTravelDirection.ToFacility;
        }

        throw new NAServerParameterException(
            $"'travelDirection' value '{value}' is not recognized. Use " +
            "'esriNATravelDirectionToFacility' or 'esriNATravelDirectionFromFacility'.");
    }

    public static LocationAllocationProblemType ParseLocationAllocationProblemType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return LocationAllocationProblemType.MinimizeImpedance;
        }

        var trimmed = value.Trim();
        if (string.Equals(trimmed, "esriMFPMinimizeImpedance", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "MinimizeImpedance", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "0", StringComparison.Ordinal))
        {
            return LocationAllocationProblemType.MinimizeImpedance;
        }

        if (string.Equals(trimmed, "esriMFPMaximizeCoverage", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "MaximizeCoverage", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "1", StringComparison.Ordinal))
        {
            return LocationAllocationProblemType.MaximizeCoverage;
        }

        if (string.Equals(trimmed, "esriMFPMinimizeFacilities", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "MinimizeFacilities", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "Minimize Facilities", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "2", StringComparison.Ordinal))
        {
            return LocationAllocationProblemType.MinimizeFacilities;
        }

        throw new NAServerParameterException(
            $"location-allocation problem type '{value}' is not supported. Supported types: " +
            "esriMFPMinimizeImpedance, esriMFPMaximizeCoverage, esriMFPMinimizeFacilities. " +
            "Maximize Attendance requires impedance-transformation inputs; Maximize Capacitated " +
            "Coverage requires facility capacities; market-share objectives require competitor " +
            "facilities and attractiveness weights.");
    }

    private static OdLineOutputType ParseOdOutputType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return OdLineOutputType.NoLines;
        }

        var trimmed = value.Trim();
        if (string.Equals(trimmed, "esriNAODOutputNoLines", StringComparison.OrdinalIgnoreCase))
        {
            return OdLineOutputType.NoLines;
        }

        if (string.Equals(trimmed, "esriNAODOutputStraightLines", StringComparison.OrdinalIgnoreCase))
        {
            return OdLineOutputType.StraightLines;
        }

        if (string.Equals(trimmed, "esriNAODOutputTrueShape", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "esriNAODOutputTrueShapeWithMeasure", StringComparison.OrdinalIgnoreCase))
        {
            throw new NAServerParameterException(
                $"'outputType' value '{value}' is not implemented: true-shape OD lines require " +
                "provider path geometry. Use 'esriNAODOutputNoLines' or 'esriNAODOutputStraightLines'.");
        }

        throw new NAServerParameterException(
            $"'outputType' value '{value}' is not supported. Use 'esriNAODOutputNoLines' " +
            "or 'esriNAODOutputStraightLines'.");
    }

    public static IReadOnlyList<DemandPoint> ParseDemandPoints(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var trimmed = value.Trim();
        if (trimmed[0] == '{')
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(trimmed);
            }
            catch (JsonException ex)
            {
                throw new NAServerParameterException($"'demandPoints' is not valid JSON: {ex.Message}");
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("features", out var features) &&
                    features.ValueKind == JsonValueKind.Array)
                {
                    var demand = new List<DemandPoint>();
                    foreach (var feature in features.EnumerateArray())
                    {
                        if (feature.ValueKind != JsonValueKind.Object ||
                            !feature.TryGetProperty("geometry", out var geometry) ||
                            !TryReadXy(geometry, out var point))
                        {
                            continue;
                        }

                        var weight = 1.0;
                        if (feature.TryGetProperty("attributes", out var attrs) &&
                            attrs.ValueKind == JsonValueKind.Object &&
                            TryReadWeight(attrs, out var w))
                        {
                            weight = w;
                        }

                        demand.Add(new DemandPoint(point, weight));
                    }

                    return demand;
                }
            }
        }

        // Fallback: bare points (weight 1 each).
        return ParsePoints(value, "demandPoints").Select(p => new DemandPoint(p, 1.0)).ToList();
    }

    private static bool TryReadWeight(JsonElement attributes, out double weight)
    {
        weight = 1.0;
        foreach (var name in (ReadOnlySpan<string>)["Weight", "weight", "DemandWeight"])
        {
            if (attributes.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.Number &&
                value.TryGetDouble(out var parsed) && parsed >= 0)
            {
                weight = parsed;
                return true;
            }
        }

        return false;
    }

    private static double? ParseCutoff(IReadOnlyDictionary<string, string> parameters, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = GetValue(parameters, key);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                throw new NAServerParameterException($"'{key}' value '{value}' is not a valid number.");
            }

            return parsed > 0 ? parsed : null;
        }

        return null;
    }

    private static void EnsureSolvePairs(int pairCount, NAServerInputCaps caps, string operation)
    {
        try
        {
            RoutingSolveBudget.Ensure(pairCount, caps.MaxSolvePairs, operation);
        }
        catch (RoutingSolveBudgetExceededException ex)
        {
            throw new NAServerParameterException(ex.Message);
        }
    }

    private static int? ParsePositiveInt(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            throw new NAServerParameterException($"'{parameterName}' value '{value}' must be a positive integer.");
        }

        return parsed;
    }

    public static string? ParseTravelMode(IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var value = GetValue(parameters, "travelMode");
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed[0] != '{')
        {
            return trimmed;
        }

        // Esri travel-mode object: { "name": "Driving Time", "type": ... }. Use the
        // name as the mode token so SDK clients that POST the full object resolve to
        // the same advertised mode as a bare token.
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("name", out var name) &&
                name.ValueKind == JsonValueKind.String)
            {
                var resolved = name.GetString();
                return string.IsNullOrWhiteSpace(resolved) ? null : resolved.Trim();
            }
        }
        catch (JsonException)
        {
            throw new NAServerParameterException(
                "'travelMode' value is not a valid mode token or travel-mode object.");
        }

        throw new NAServerParameterException(
            "'travelMode' object did not contain a 'name'.");
    }

    public static IReadOnlyList<RouteBarrier> ParseBarriers(
        IReadOnlyDictionary<string, string> parameters,
        NAServerInputCaps caps)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var barriers = new List<RouteBarrier>();
        AppendBarriers(barriers, GetValue(parameters, "barriers"), RouteBarrierKind.Point, "barriers");
        AppendBarriers(barriers, GetValue(parameters, "polylineBarriers"), RouteBarrierKind.Line, "polylineBarriers");
        AppendBarriers(barriers, GetValue(parameters, "polygonBarriers"), RouteBarrierKind.Polygon, "polygonBarriers");

        if (barriers.Count > caps.MaxBarriers)
        {
            throw new NAServerParameterException(
                $"barrier count {barriers.Count} exceeds the maximum of {caps.MaxBarriers}.");
        }

        return barriers;
    }

    private static void AppendBarriers(
        List<RouteBarrier> sink,
        string? value,
        RouteBarrierKind kind,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(value.Trim());
        }
        catch (JsonException ex)
        {
            throw new NAServerParameterException(
                $"'{parameterName}' is not valid JSON: {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;

            // Esri FeatureSet: { "features": [ { "geometry": {...} }, ... ] }.
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("features", out var features) &&
                features.ValueKind == JsonValueKind.Array)
            {
                foreach (var geometry in features.EnumerateArray()
                             .Select(feature =>
                                 feature.ValueKind == JsonValueKind.Object &&
                                 feature.TryGetProperty("geometry", out var geometry)
                                     ? (JsonElement?)geometry
                                     : null)
                             .Where(geometry => geometry.HasValue)
                             .Select(geometry => geometry.GetValueOrDefault()))
                {
                    sink.Add(new RouteBarrier(kind, GeoServicesGeometryToGeoJson(geometry, kind, parameterName)));
                }

                return;
            }

            // A bare geometry object (no FeatureSet wrapper).
            sink.Add(new RouteBarrier(kind, GeoServicesGeometryToGeoJson(root, kind, parameterName)));
        }
    }

    private static string GeoServicesGeometryToGeoJson(JsonElement geometry, RouteBarrierKind kind, string parameterName)
    {
        if (geometry.ValueKind != JsonValueKind.Object)
        {
            throw new NAServerParameterException($"'{parameterName}' contains a non-object geometry.");
        }

        return kind switch
        {
            RouteBarrierKind.Point => PointToGeoJson(geometry, parameterName),
            RouteBarrierKind.Line => PathsToGeoJson(geometry, parameterName),
            RouteBarrierKind.Polygon => RingsToGeoJson(geometry, parameterName),
            _ => throw new NAServerParameterException($"'{parameterName}' has an unsupported barrier kind."),
        };
    }

    private static string PointToGeoJson(JsonElement geometry, string parameterName)
    {
        if (!geometry.TryGetProperty("x", out var x) || x.ValueKind != JsonValueKind.Number ||
            !geometry.TryGetProperty("y", out var y) || y.ValueKind != JsonValueKind.Number)
        {
            throw new NAServerParameterException($"'{parameterName}' point geometry must have numeric 'x' and 'y'.");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"type\":\"Point\",\"coordinates\":[{x.GetDouble()},{y.GetDouble()}]}}");
    }

    private static string PathsToGeoJson(JsonElement geometry, string parameterName)
    {
        if (!geometry.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Array)
        {
            throw new NAServerParameterException($"'{parameterName}' polyline geometry must have a 'paths' array.");
        }

        var builder = new System.Text.StringBuilder();
        builder.Append("{\"type\":\"MultiLineString\",\"coordinates\":");
        AppendCoordinateArray(builder, paths, parameterName);
        builder.Append('}');
        return builder.ToString();
    }

    private static string RingsToGeoJson(JsonElement geometry, string parameterName)
    {
        if (!geometry.TryGetProperty("rings", out var rings) || rings.ValueKind != JsonValueKind.Array)
        {
            throw new NAServerParameterException($"'{parameterName}' polygon geometry must have a 'rings' array.");
        }

        // Esri rings collapse to a single GeoJSON Polygon whose ring list is taken
        // as-is. Barrier semantics only need the area for ST_Intersects, so exact
        // outer/hole winding is not material to edge exclusion.
        var builder = new System.Text.StringBuilder();
        builder.Append("{\"type\":\"Polygon\",\"coordinates\":");
        AppendCoordinateArray(builder, rings, parameterName);
        builder.Append('}');
        return builder.ToString();
    }

    private static void AppendCoordinateArray(System.Text.StringBuilder builder, JsonElement partArray, string parameterName)
    {
        builder.Append('[');
        var firstPart = true;
        foreach (var part in partArray.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Array)
            {
                throw new NAServerParameterException($"'{parameterName}' contains a malformed coordinate part.");
            }

            if (!firstPart)
            {
                builder.Append(',');
            }

            firstPart = false;

            builder.Append('[');
            var firstVertex = true;
            foreach (var vertex in part.EnumerateArray())
            {
                if (vertex.ValueKind != JsonValueKind.Array)
                {
                    throw new NAServerParameterException($"'{parameterName}' contains a malformed vertex.");
                }

                double? vx = null;
                double? vy = null;
                foreach (var ordinate in vertex.EnumerateArray())
                {
                    if (ordinate.ValueKind != JsonValueKind.Number)
                    {
                        break;
                    }

                    if (vx is null)
                    {
                        vx = ordinate.GetDouble();
                    }
                    else
                    {
                        vy = ordinate.GetDouble();
                        break;
                    }
                }

                if (vx is not { } xv || vy is not { } yv)
                {
                    throw new NAServerParameterException($"'{parameterName}' vertex is missing x/y ordinates.");
                }

                if (!firstVertex)
                {
                    builder.Append(',');
                }

                firstVertex = false;
                builder.Append(string.Create(CultureInfo.InvariantCulture, $"[{xv},{yv}]"));
            }

            builder.Append(']');
        }

        builder.Append(']');
    }

    private static string? GetValue(IReadOnlyDictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var value) ? value : null;

    internal static int ParseOutSr(IReadOnlyDictionary<string, string> parameters)
        => ParseSpatialReference(parameters, "outSR") ?? DefaultSrid;

    // Input ordinates use the spatial reference declared on the features the operation consumes, latestWkid wins over a legacy wkid PostGIS cannot transform, Web Mercator alias 102100 resolves to EPSG:3857, and conflicting declarations are rejected.
    private static int ParseInSr(
        IReadOnlyDictionary<string, string> parameters,
        int outSrid,
        params string[] inputParameters)
    {
        var explicitInSr = ParseSpatialReference(parameters, "inSR", preferLatestWkid: true);
        int? declaredSrid = null;
        string? declaredBy = null;
        foreach (var parameterName in inputParameters.Concat(BarrierParameters))
        {
            foreach (var srid in ReadDeclaredSpatialReferences(GetValue(parameters, parameterName), parameterName))
            {
                if (declaredSrid is { } existing && existing != srid)
                {
                    throw new NAServerParameterException(
                        $"'{parameterName}' declares spatial reference {srid} but '{declaredBy}' declares {existing}; " +
                        "all input geometries must share one spatial reference.");
                }

                declaredSrid = srid;
                declaredBy ??= parameterName;
            }
        }

        if (declaredSrid is { } declared)
        {
            if (explicitInSr is { } inSr && SpatialReferenceExtensions.NormalizeWebMercatorSrid(inSr) != declared)
            {
                throw new NAServerParameterException(
                    $"'inSR' {inSr} conflicts with spatial reference {declared} declared by '{declaredBy}'.");
            }

            return declared;
        }

        return SpatialReferenceExtensions.NormalizeWebMercatorSrid(explicitInSr ?? outSrid);
    }

    private static readonly string[] BarrierParameters = ["barriers", "polylineBarriers", "polygonBarriers"];

    private static List<int> ReadDeclaredSpatialReferences(string? value, string parameterName)
    {
        var declared = new List<int>();
        if (string.IsNullOrWhiteSpace(value) || value.TrimStart()[0] != '{')
        {
            return declared;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(value);
        }
        catch (JsonException)
        {
            return declared;
        }

        using (doc)
        {
            var root = doc.RootElement;
            AddDeclared(root);
            if (root.TryGetProperty("features", out var features) && features.ValueKind == JsonValueKind.Array)
            {
                foreach (var geometry in features.EnumerateArray()
                             .Where(static feature => feature.ValueKind == JsonValueKind.Object)
                             .Select(static feature => feature.TryGetProperty("geometry", out var geometry) ? geometry : default))
                {
                    AddDeclared(geometry);
                }
            }

            if (root.TryGetProperty("geometries", out var geometries) && geometries.ValueKind == JsonValueKind.Array)
            {
                foreach (var geometry in geometries.EnumerateArray())
                {
                    AddDeclared(geometry);
                }
            }
        }

        return declared;

        void AddDeclared(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty("spatialReference", out var spatialReference) ||
                spatialReference.ValueKind == JsonValueKind.Null)
            {
                return;
            }

            if (!TryReadWkid(spatialReference, preferLatestWkid: true, out var wkid))
            {
                throw new NAServerParameterException(
                    $"'{parameterName}' spatialReference must carry a positive 'wkid' or 'latestWkid'.");
            }

            declared.Add(SpatialReferenceExtensions.NormalizeWebMercatorSrid(wkid));
        }
    }

    private static int? ParseSpatialReference(
        IReadOnlyDictionary<string, string> parameters,
        string key,
        bool preferLatestWkid = false)
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
                if (TryReadWkid(doc.RootElement, preferLatestWkid, out var wkid))
                {
                    return wkid;
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

    // latestWkid is read before wkid because a legacy Esri wkid such as 102067 has no PostGIS transform.
    private static bool TryReadWkid(JsonElement spatialReference, bool preferLatestWkid, out int wkid)
    {
        wkid = 0;
        if (spatialReference.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        ReadOnlySpan<string> names = preferLatestWkid ? ["latestWkid", "wkid"] : ["wkid", "latestWkid"];
        foreach (var name in names)
        {
            if (spatialReference.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt32(out var parsed) && parsed > 0)
            {
                wkid = parsed;
                return true;
            }
        }

        return false;
    }

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
                points.AddRange(features.EnumerateArray()
                    .Select(feature =>
                        feature.ValueKind == JsonValueKind.Object &&
                        feature.TryGetProperty("geometry", out var geometry) &&
                        TryReadXy(geometry, out var point)
                            ? (RoutePoint?)point
                            : null)
                    .Where(point => point.HasValue)
                    .Select(point => point.GetValueOrDefault()));

                return points;
            }

            // Geometry collection: { "geometries": [ { "x", "y" }, ... ] }
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("geometries", out var geometries) &&
                geometries.ValueKind == JsonValueKind.Array)
            {
                points.AddRange(geometries.EnumerateArray()
                    .Select(geometry => TryReadXy(geometry, out var point) ? (RoutePoint?)point : null)
                    .Where(point => point.HasValue)
                    .Select(point => point.GetValueOrDefault()));

                return points;
            }

            // Multipoint: { "points": [ [x, y], ... ] }
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("points", out var multipoint) &&
                multipoint.ValueKind == JsonValueKind.Array)
            {
                points.AddRange(multipoint.EnumerateArray()
                    .Select(coordinate =>
                        TryReadCoordinatePair(coordinate, out var point)
                            ? (RoutePoint?)point
                            : null)
                    .Where(point => point.HasValue)
                    .Select(point => point.GetValueOrDefault()));

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

                points.AddRange(root.EnumerateArray()
                    .Select(coordinate =>
                        TryReadCoordinatePair(coordinate, out var point)
                            ? (RoutePoint?)point
                            : null)
                    .Where(point => point.HasValue)
                    .Select(point => point.GetValueOrDefault()));

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
