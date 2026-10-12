// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing;

/// <summary>Provider-backed routing operations executed by the shared managed job runtime.</summary>
internal static class RoutingProcessDefinitions
{
    internal const string ClosestFacilities = "routing.closest-facilities";
    internal const string OdCostMatrix = "routing.od-cost-matrix";
    internal const string LocationAllocation = "routing.location-allocation";

    internal static bool Handles(string processId)
        => processId is ClosestFacilities or OdCostMatrix or LocationAllocation;

    internal static ProcessDefinition[] Create() =>
    [
        Definition(ClosestFacilities, "Find Closest Facilities", "Finds ranked network routes between incidents and facilities.",
        [
            Points("incidents", "Incidents"), Points("facilities", "Facilities"),
            Parameter("facility_count", "Number of facilities to find per incident.", ProcessParameterValueType.WholeNumber, "1"),
            Parameter("travel_direction", "Travel from incident to facility, or facility to incident.", ProcessParameterValueType.Text,
                "Incident to Facility", ["Incident to Facility", "Facility to Incident"]),
        ]),
        Definition(OdCostMatrix, "Generate Origin Destination Cost Matrix", "Computes ranked network costs between origins and destinations.",
        [
            Points("origins", "Origins"), Points("destinations", "Destinations"),
            Parameter("destination_count", "Maximum destinations to find per origin.", ProcessParameterValueType.WholeNumber),
            Parameter("line_shape", "Cost-only output or straight lines between input points.", ProcessParameterValueType.Text,
                "No Lines", ["No Lines", "Straight Line"]),
        ]),
        Definition(LocationAllocation, "Solve Location Allocation", "Chooses candidate facilities and allocates weighted demand using network costs.",
        [
            Points("facilities", "Facilities"), Points("demand_points", "Demand Points"),
            Parameter("facility_count", "Number of candidate facilities to select.", ProcessParameterValueType.WholeNumber, "1"),
            Parameter("problem_type", "Optimization objective.", ProcessParameterValueType.Text,
                "Minimize Impedance", ["Minimize Impedance", "Maximize Coverage", "Minimize Facilities"]),
            Parameter("travel_direction", "Measure costs from candidate facilities to demand points.", ProcessParameterValueType.Text,
                "Facility to Demand", ["Facility to Demand"]),
        ]),
    ];

    private static ProcessDefinition Definition(string id, string title, string description, ProcessParameterSpec[] inputs)
        => new()
        {
            ProcessId = id, Title = title, Description = description, Category = "routing",
            Parameters =
            [
                .. inputs,
                Parameter("srid", "Input coordinate reference; embedded declarations must agree.", ProcessParameterValueType.Srid, "4326"),
                Parameter("output_srid", "Output coordinate reference.", ProcessParameterValueType.Srid, "4326"),
                Parameter("cutoff", "Maximum network travel time in the selected time units.", ProcessParameterValueType.FloatingPoint),
                Parameter("time_units", "Time units for cutoffs and reported travel times.", ProcessParameterValueType.Text,
                    "Minutes", ["Seconds", "Minutes", "Hours", "Days"]),
                .. (id == LocationAllocation ? Array.Empty<ProcessParameterSpec>() :
                [Parameter("distance_units", "Distance units for reported route lengths.", ProcessParameterValueType.Text,
                    "Miles", ["Meters", "Kilometers", "Miles", "Feet", "Yards", "NauticalMiles"])]),
                Parameter("travel_mode", "Provider-advertised travel-mode identifier.", ProcessParameterValueType.Text),
                Points("point_barriers", "Point Barriers", required: false),
                Points("line_barriers", "Line Barriers", required: false),
                Points("polygon_barriers", "Polygon Barriers", required: false),
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer, ArtifactKind.FeatureLayer, ArtifactKind.FeatureLayer, ArtifactKind.Scalar],
        };

    private static ProcessParameterSpec Points(string name, string title, bool required = true)
        => new()
        {
            Name = name, DisplayName = title, Description = "Feature collection; input geometry and location identity attributes are retained.",
            ValueType = ProcessParameterValueType.Text, Required = required, AcceptsGeoJsonDataUri = true,
        };

    private static ProcessParameterSpec Parameter(string name, string description, ProcessParameterValueType type,
        string? defaultValue = null, IReadOnlyList<string>? choices = null)
        => new() { Name = name, DisplayName = name, Description = description, ValueType = type, DefaultValue = defaultValue, AllowedValues = choices };
}
