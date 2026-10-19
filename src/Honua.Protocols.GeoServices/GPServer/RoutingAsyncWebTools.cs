// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Geoprocessing;

namespace Honua.Protocols.GeoServices.GPServer;

/// <summary>Adapts documented routing GP inputs and outputs to the canonical managed processes.</summary>
internal static class RoutingAsyncWebTools
{
    internal static bool IsTask(string? taskName)
        => taskName is not null && (taskName.Equals("FindClosestFacilities", StringComparison.OrdinalIgnoreCase)
            || taskName.Equals("GenerateOriginDestinationCostMatrix", StringComparison.OrdinalIgnoreCase)
            || taskName.Equals("SolveLocationAllocation", StringComparison.OrdinalIgnoreCase));

    private static readonly Dictionary<string, string> CommonInputs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Incidents"] = "incidents",
        ["Facilities"] = "facilities",
        ["Origins"] = "origins",
        ["Destinations"] = "destinations",
        ["Demand_Points"] = "demand_points",
        ["Number_of_Facilities_to_Find"] = "facility_count",
        ["Number_of_Destinations_to_Find"] = "destination_count",
        ["Default_Cutoff"] = "cutoff",
        ["Cutoff"] = "cutoff",
        ["Default_Measurement_Cutoff"] = "cutoff",
        ["Default_Impedance_Cutoff"] = "cutoff",
        ["Travel_Direction"] = "travel_direction",
        ["Origin_Destination_Line_Shape"] = "line_shape",
        ["Problem_Type"] = "problem_type",
        ["Travel_Mode"] = "travel_mode",
        ["Travel_Modes"] = "travel_mode",
        ["Point_Barriers"] = "point_barriers",
        ["Line_Barriers"] = "line_barriers",
        ["Polygon_Barriers"] = "polygon_barriers",
        ["inSR"] = "srid",
        ["outSR"] = "output_srid",
        ["Measurement_Units"] = "time_units",
        ["Time_Units"] = "time_units",
        ["Distance_Units"] = "distance_units",
    };

    internal static string InputName(string canonicalName, string processId) => canonicalName switch
    {
        "incidents" => "Incidents",
        "facilities" => "Facilities",
        "origins" => "Origins",
        "destinations" => "Destinations",
        "demand_points" => "Demand_Points",
        "facility_count" => "Number_of_Facilities_to_Find",
        "destination_count" => "Number_of_Destinations_to_Find",
        "cutoff" => processId == RoutingProcessDefinitions.LocationAllocation ? "Default_Measurement_Cutoff" : "Cutoff",
        "travel_direction" => "Travel_Direction",
        "line_shape" => "Origin_Destination_Line_Shape",
        "problem_type" => "Problem_Type",
        "travel_mode" => "Travel_Mode",
        "srid" => "inSR",
        "output_srid" => "outSR",
        "point_barriers" => "Point_Barriers",
        "line_barriers" => "Line_Barriers",
        "polygon_barriers" => "Polygon_Barriers",
        "time_units" => processId == RoutingProcessDefinitions.OdCostMatrix ? "Time_Units" : "Measurement_Units",
        "distance_units" => "Distance_Units",
        _ => canonicalName,
    };

    internal static string[] Outputs(string processId) => processId switch
    {
        RoutingProcessDefinitions.ClosestFacilities => ["Output_Routes", "Output_Incidents", "Output_Facilities", "Solve_Succeeded"],
        RoutingProcessDefinitions.OdCostMatrix => ["Output_Origin_Destination_Lines", "Output_Origins", "Output_Destinations", "Solve_Succeeded"],
        RoutingProcessDefinitions.LocationAllocation => ["Output_Allocation_Lines", "Output_Facilities", "Output_Demand_Points", "Solve_Succeeded"],
        _ => throw new ArgumentException("Not a routing process.", nameof(processId)),
    };

    internal static JsonElement DefaultFeatureSet(string parameter)
    {
        var geometryType = parameter switch
        {
            "line_barriers" => "esriGeometryPolyline",
            "polygon_barriers" => "esriGeometryPolygon",
            _ => "esriGeometryPoint",
        };
        using var document = JsonDocument.Parse($$"""
            {"geometryType":"{{geometryType}}","spatialReference":{"wkid":4326},"fields":[{"name":"OBJECTID","type":"esriFieldTypeOID"},{"name":"Name","type":"esriFieldTypeString"}],"features":[]}
            """);
        return document.RootElement.Clone();
    }

    internal static Dictionary<string, string> Translate(ProcessDefinition definition, IReadOnlyDictionary<string, string> raw, int? environmentOutSr)
    {
        var translated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in raw)
        {
            if (IsControl(name))
            {
                translated[name] = value;
                continue;
            }
            var canonical = CommonInputs.GetValueOrDefault(name) ?? name;
            if (!definition.Parameters.Any(parameter => parameter.Name.Equals(canonical, StringComparison.OrdinalIgnoreCase)))
            {
                // False/empty packaging controls request no extra effect. Other advanced settings are rejected.
                if ((name.Equals("Save_Route_Data", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Save_Output_Network_Analysis_Layer", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Populate_Directions", StringComparison.OrdinalIgnoreCase)) && bool.TryParse(value, out var flag) && !flag)
                {
                    continue;
                }
                if (name.Equals("Output_Format", StringComparison.OrdinalIgnoreCase) && value.Equals("Feature Set", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                throw new GeoprocessingValidationException($"Routing input '{name}' is not supported.");
            }
            if (translated.TryGetValue(canonical, out var previous) && !previous.Equals(value, StringComparison.Ordinal))
            {
                throw new GeoprocessingValidationException($"Conflicting values for routing input '{canonical}'.");
            }
            translated[canonical] = value;
        }

        if (environmentOutSr is { } requested)
        {
            var normalized = SpatialReferenceExtensions.NormalizeWebMercatorSrid(requested);
            if (translated.TryGetValue("output_srid", out var explicitOutput) && ReadSrid(explicitOutput) != normalized)
            {
                throw new GeoprocessingValidationException("outSR conflicts with the GP output spatial-reference control.");
            }
            translated["output_srid"] = normalized.ToString(CultureInfo.InvariantCulture);
        }
        translated["output_srid"] = ReadSrid(translated.GetValueOrDefault("output_srid") ?? "4326").ToString(CultureInfo.InvariantCulture);

        int? inputSrid = translated.TryGetValue("srid", out var declaredInput) ? ReadSrid(declaredInput) : null;
        foreach (var parameter in definition.Parameters.Where(parameter => parameter.AcceptsGeoJsonDataUri))
        {
            if (!translated.TryGetValue(parameter.Name, out var value) || !value.TrimStart().StartsWith('{'))
            {
                continue;
            }
            var node = JsonNode.Parse(value) as JsonObject ?? throw new GeoprocessingValidationException("Routing feature inputs must be objects.");
            InspectSpatialReference(node["spatialReference"]);
            if (node["features"] is JsonArray features)
            {
                foreach (var feature in features)
                {
                    InspectSpatialReference(feature?["geometry"]?["spatialReference"]);
                }
            }
            translated[parameter.Name] = node.ToJsonString();
        }
        translated["srid"] = (inputSrid ?? 4326).ToString(CultureInfo.InvariantCulture);
        if (translated.TryGetValue("line_shape", out var shape) && shape.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            translated["line_shape"] = "No Lines";
        }
        if (translated.TryGetValue("distance_units", out var distance) && distance.Equals("Nautical Miles", StringComparison.OrdinalIgnoreCase))
        {
            translated["distance_units"] = "NauticalMiles";
        }
        if (translated.TryGetValue("travel_mode", out var mode) && mode.Equals("Custom", StringComparison.OrdinalIgnoreCase))
        {
            translated.Remove("travel_mode");
        }
        return translated;

        void InspectSpatialReference(JsonNode? reference)
        {
            if (reference is null)
            {
                return;
            }
            if (reference is not JsonObject spatialReference)
            {
                throw new GeoprocessingValidationException("Routing feature spatial references must be WKID objects.");
            }
            var srid = ReadSrid(spatialReference.ToJsonString());
            if (inputSrid is { } prior && prior != srid)
            {
                throw new GeoprocessingValidationException("Routing feature inputs have conflicting spatial references.");
            }
            inputSrid = srid;
            spatialReference["wkid"] = srid;
            spatialReference.Remove("latestWkid");
        }
    }

    internal static JsonElement BooleanResult(string uri)
    {
        const string prefix = "data:application/json;base64,";
        if (!uri.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new GeoprocessingValidationException("Routing solve status is not a canonical scalar artifact.");
        }
        using var result = JsonDocument.Parse(Convert.FromBase64String(uri[prefix.Length..]));
        if (result.RootElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new GeoprocessingValidationException("Routing solve status must be Boolean.");
        }
        return result.RootElement.Clone();
    }

    private static bool IsControl(string name)
        => name.Equals("f", StringComparison.OrdinalIgnoreCase) || name.Equals("token", StringComparison.OrdinalIgnoreCase)
            || name.Equals("context", StringComparison.OrdinalIgnoreCase) || name.Equals("idempotencyKey", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("env:", StringComparison.OrdinalIgnoreCase);

    private static int ReadSrid(string value)
    {
        if (value.TrimStart().StartsWith('{'))
        {
            using var reference = JsonDocument.Parse(value);
            if (reference.RootElement.TryGetProperty("latestWkid", out var latest))
            {
                value = latest.GetRawText();
            }
            else if (reference.RootElement.TryGetProperty("wkid", out var wkid))
            {
                value = wkid.GetRawText();
            }
        }
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var srid) || srid <= 0)
        {
            throw new GeoprocessingValidationException("Routing spatial references require a positive WKID.");
        }
        return SpatialReferenceExtensions.NormalizeWebMercatorSrid(srid);
    }
}
