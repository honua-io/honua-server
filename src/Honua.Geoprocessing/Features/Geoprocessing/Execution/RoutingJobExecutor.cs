// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.ControlPlane;
using Honua.Infrastructure.Rendering;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Geoprocessing.Execution;

/// <summary>Runs bounded routing solves through the canonical provider and shared job publication fence.</summary>
internal sealed class RoutingJobExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options,
    IOptions<RoutingConfiguration> configuration,
    IRoutingProvider? routing = null,
    ICoordinateTransformService? transforms = null) : IProcessExecutor
{
    public IReadOnlySet<string> ProcessIds { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        RoutingProcessDefinitions.ClosestFacilities, RoutingProcessDefinitions.OdCostMatrix, RoutingProcessDefinitions.LocationAllocation,
    };

    public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

    public async Task<JobExecutionResult> ExecuteAsync(ExecutionJobRecord job, IJobExecutionContext context, CancellationToken cancellationToken)
    {
        var processId = GeoprocessingDispatchHelper.ResolveProcessId(job.Spec.Parameters);
        if (processId is null || !ProcessIds.Contains(processId))
        {
            return JobExecutionResult.Failed("The routing executor does not handle this process.");
        }
        if (routing is null)
        {
            return JobExecutionResult.Failed("No routing provider is configured.");
        }

        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";
        var inputs = job.Spec.Parameters.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key[prefix.Length..], pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inSrid = ReadSrid(inputs, "srid");
            var outSrid = ReadSrid(inputs, "output_srid");
            var timeUnits = inputs.GetValueOrDefault("time_units") ?? "Minutes";
            var distanceUnits = inputs.GetValueOrDefault("distance_units") ?? "Miles";
            var minutesPerUnit = (inputs.GetValueOrDefault("time_units") ?? "Minutes").ToUpperInvariant() switch
            {
                "SECONDS" => 1d / 60,
                "MINUTES" => 1,
                "HOURS" => 60,
                "DAYS" => 1440,
                _ => throw new ArgumentException("Routing impedance must use supported time units; distance-based cutoffs are not supported."),
            };
            var metersPerUnit = (inputs.GetValueOrDefault("distance_units") ?? "Miles").ToUpperInvariant() switch
            {
                "METERS" => 1,
                "KILOMETERS" => 1000,
                "MILES" => 1609.344,
                "FEET" => 0.3048,
                "YARDS" => 0.9144,
                "NAUTICALMILES" => 1852,
                _ => throw new ArgumentException("Unsupported routing distance units."),
            };
            var cutoff = ReadPositiveDouble(inputs, "cutoff") * minutesPerUnit;
            var capabilities = await routing.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            var barriers = ReadBarriers(inputs);
            var mode = ReadTravelMode(inputs.GetValueOrDefault("travel_mode"));
            var capabilityError = RoutingRequestValidation.ValidateCapabilities(capabilities, barriers, mode);
            if (capabilityError is not null)
            {
                return JobExecutionResult.Failed(capabilityError);
            }
            await context.ReportProgressAsync(10, "Solving network analysis", cancellationToken).ConfigureAwait(false);
            List<IFeature> solution;
            FeatureCollection first;
            FeatureCollection second;

            if (processId == RoutingProcessDefinitions.ClosestFacilities)
            {
                Require(capabilities.SupportsClosestFacility, "The routing provider does not support closest facilities.");
                first = ReadPoints(inputs, "incidents", configuration.Value.MaxIncidents);
                second = ReadPoints(inputs, "facilities", configuration.Value.MaxClosestFacilities);
                var direction = inputs.GetValueOrDefault("travel_direction") ?? "Incident to Facility";
                Require(direction.Equals("Incident to Facility", StringComparison.OrdinalIgnoreCase)
                    || direction.Equals("Facility to Incident", StringComparison.OrdinalIgnoreCase), "Unsupported closest-facility travel direction.");
                var request = new ClosestFacilitySolveRequest(Points(first), Points(second), ReadCount(inputs, "facility_count") ?? 1,
                    cutoff, direction.Equals("Incident to Facility", StringComparison.OrdinalIgnoreCase)
                        ? ClosestFacilityTravelDirection.ToFacility : ClosestFacilityTravelDirection.FromFacility, outSrid)
                { InSrid = inSrid, Barriers = barriers, TravelMode = mode };
                var result = await routing.SolveClosestFacilityAsync(request, cancellationToken).ConfigureAwait(false);
                solution = result.Routes.Select(route => Feature(route.RouteGeometryGeoJson,
                    ("IncidentID", Identity(first, route.IncidentId)), ("FacilityID", Identity(second, route.FacilityId)),
                    ("FacilityRank", route.Rank), ("Name", request.Direction == ClosestFacilityTravelDirection.ToFacility
                        ? $"{Name(first, route.IncidentId)} - {Name(second, route.FacilityId)}"
                        : $"{Name(second, route.FacilityId)} - {Name(first, route.IncidentId)}"),
                    ("IncidentName", Name(first, route.IncidentId)), ("FacilityName", Name(second, route.FacilityId)),
                    ("IncidentOID", ObjectId(first, route.IncidentId)), ("FacilityOID", ObjectId(second, route.FacilityId)),
                    ("Total_Minutes", route.TotalTimeMinutes), ("Total_Miles", route.TotalLengthMeters / 1609.344),
                    ("Total_Kilometers", route.TotalLengthMeters / 1000))).ToList<IFeature>();
                for (var index = 0; index < solution.Count; index++)
                {
                    AddSelectedUnits(solution[index], result.Routes[index].TotalTimeMinutes, result.Routes[index].TotalLengthMeters,
                        timeUnits, distanceUnits, minutesPerUnit, metersPerUnit);
                }
            }
            else if (processId == RoutingProcessDefinitions.OdCostMatrix)
            {
                Require(capabilities.SupportsOdCostMatrix, "The routing provider does not support OD cost matrices.");
                first = ReadPoints(inputs, "origins", configuration.Value.MaxOrigins);
                second = ReadPoints(inputs, "destinations", configuration.Value.MaxDestinations);
                var lineShape = inputs.GetValueOrDefault("line_shape") ?? "No Lines";
                Require(lineShape.Equals("No Lines", StringComparison.OrdinalIgnoreCase)
                    || lineShape.Equals("Straight Line", StringComparison.OrdinalIgnoreCase), "Unsupported OD line shape.");
                var straight = lineShape.Equals("Straight Line", StringComparison.OrdinalIgnoreCase);
                Require(!straight || capabilities.SupportsOdStraightLines, "The routing provider does not support OD straight lines.");
                var request = new OdCostMatrixSolveRequest(Points(first), Points(second), outSrid)
                {
                    InSrid = inSrid,
                    Cutoff = cutoff,
                    DestinationCount = ReadCount(inputs, "destination_count"),
                    OutputType = straight ? OdLineOutputType.StraightLines : OdLineOutputType.NoLines,
                    Barriers = barriers,
                    TravelMode = mode,
                };
                var result = await routing.SolveOdCostMatrixAsync(request, cancellationToken).ConfigureAwait(false);
                solution = result.Lines.Select(line => Feature(line.GeometryGeoJson,
                    ("OriginID", Identity(first, line.OriginId)), ("DestinationID", Identity(second, line.DestinationId)),
                    ("DestinationRank", line.DestinationRank), ("OriginName", Name(first, line.OriginId)), ("DestinationName", Name(second, line.DestinationId)),
                    ("OriginOID", ObjectId(first, line.OriginId)), ("DestinationOID", ObjectId(second, line.DestinationId)),
                    ("Total_Time", line.TotalCostMinutes / minutesPerUnit), ("Total_Distance", line.TotalLengthMeters / metersPerUnit))).ToList<IFeature>();
            }
            else
            {
                Require(capabilities.SupportsLocationAllocation, "The routing provider does not support location allocation.");
                Require((inputs.GetValueOrDefault("travel_direction") ?? "Facility to Demand").Equals("Facility to Demand", StringComparison.OrdinalIgnoreCase),
                    "The routing provider supports location-allocation costs only from facilities to demand points.");
                first = ReadPoints(inputs, "facilities", configuration.Value.MaxLocationAllocationFacilities);
                second = ReadPoints(inputs, "demand_points", configuration.Value.MaxDemandPoints, weighted: true);
                var objective = (inputs.GetValueOrDefault("problem_type") ?? "Minimize Impedance").ToUpperInvariant() switch
                {
                    "MINIMIZE IMPEDANCE" => LocationAllocationProblemType.MinimizeImpedance,
                    "MAXIMIZE COVERAGE" => LocationAllocationProblemType.MaximizeCoverage,
                    "MINIMIZE FACILITIES" => LocationAllocationProblemType.MinimizeFacilities,
                    _ => throw new ArgumentException("Unsupported location-allocation problem type."),
                };
                Require(capabilities.SupportedLocationAllocationProblemTypes.Contains(objective), "The routing provider does not support the requested location-allocation objective.");
                Require(objective == LocationAllocationProblemType.MinimizeImpedance || cutoff is not null, "Coverage objectives require a positive cutoff in minutes.");
                var demands = second.Select(feature => new DemandPoint(Point(feature), Weight(feature))).ToArray();
                var request = new LocationAllocationSolveRequest(Points(first), demands, objective, ReadCount(inputs, "facility_count") ?? 1, cutoff, outSrid)
                { InSrid = inSrid, Barriers = barriers, TravelMode = mode };
                var result = await routing.SolveLocationAllocationAsync(request, cancellationToken).ConfigureAwait(false);
                Require(result.Allocations.All(allocation => allocation.DemandPointId >= 0 && allocation.DemandPointId < second.Count
                    && allocation.AllocatedFacilityId >= -1 && allocation.AllocatedFacilityId < first.Count),
                    "The provider returned an invalid location-allocation index.");
                Require(result.Allocations.Select(allocation => allocation.DemandPointId).Distinct().Count() == result.Allocations.Count,
                    "The provider returned multiple allocations for one demand point.");
                solution = [];
                foreach (var allocation in result.Allocations)
                {
                    if (allocation.AllocatedFacilityId < 0)
                    {
                        continue;
                    }
                    var demand = await ProjectPointAsync(second[allocation.DemandPointId], inSrid, outSrid, cancellationToken).ConfigureAwait(false);
                    var facility = await ProjectPointAsync(first[allocation.AllocatedFacilityId], inSrid, outSrid, cancellationToken).ConfigureAwait(false);
                    var geometry = new LineString([facility.Coordinate.Copy(), demand.Coordinate.Copy()]);
                    var feature = new Feature(geometry, Attributes(("DemandID", Identity(second, allocation.DemandPointId)),
                        ("FacilityID", Identity(first, allocation.AllocatedFacilityId)), ("Weight", allocation.Weight),
                        ("DemandOID", ObjectId(second, allocation.DemandPointId)), ("FacilityOID", ObjectId(first, allocation.AllocatedFacilityId)),
                        ("Name", $"{Name(first, allocation.AllocatedFacilityId)} - {Name(second, allocation.DemandPointId)}"),
                        ("Total_Minutes", allocation.ImpedanceMinutes), ("TotalWeighted_Minutes", allocation.ImpedanceMinutes * allocation.Weight)));
                    AddSelectedUnits(feature, allocation.ImpedanceMinutes, null, timeUnits, distanceUnits, minutesPerUnit, metersPerUnit, allocation.Weight);
                    solution.Add(feature);
                }
                for (var index = 0; index < first.Count; index++)
                {
                    var key = first[index].Attributes.GetNames().FirstOrDefault(name => name.Equals("FacilityType", StringComparison.OrdinalIgnoreCase));
                    if (key is not null)
                    {
                        first[index].Attributes.DeleteAttribute(key);
                    }
                    first[index].Attributes.Add("FacilityType", result.ChosenFacilityIds.Contains(index) ? 3 : 0);
                    var allocations = result.Allocations.Where(allocation => allocation.AllocatedFacilityId == index).ToArray();
                    SetAttribute(first[index], "DemandCount", allocations.Length);
                    SetAttribute(first[index], "DemandWeight", allocations.Sum(allocation => allocation.Weight));
                    SetAttribute(first[index], "Total_Minutes", allocations.Sum(allocation => allocation.ImpedanceMinutes));
                    SetAttribute(first[index], "TotalWeighted_Minutes", allocations.Sum(allocation => allocation.ImpedanceMinutes * allocation.Weight));
                    if (!timeUnits.Equals("Minutes", StringComparison.OrdinalIgnoreCase))
                    {
                        SetAttribute(first[index], "Total_" + timeUnits, allocations.Sum(allocation => allocation.ImpedanceMinutes) / minutesPerUnit);
                        SetAttribute(first[index], "TotalWeighted_" + timeUnits, allocations.Sum(allocation => allocation.ImpedanceMinutes * allocation.Weight) / minutesPerUnit);
                    }
                }
                var demandAllocations = result.Allocations.ToDictionary(allocation => allocation.DemandPointId);
                for (var index = 0; index < second.Count; index++)
                {
                    demandAllocations.TryGetValue(index, out var allocation);
                    var assigned = allocation is { AllocatedFacilityId: >= 0 };
                    SetAttribute(second[index], "DemandOID", InputObjectId(second, index));
                    SetAttribute(second[index], "FacilityOID", assigned ? InputObjectId(first, allocation!.AllocatedFacilityId) : null);
                    SetAttribute(second[index], "AllocatedWeight", assigned ? allocation!.Weight : null);
                }
            }

            var firstOutput = await ProjectInputsAsync(first, inSrid, outSrid, cancellationToken).ConfigureAwait(false);
            var secondOutput = await ProjectInputsAsync(second, inSrid, outSrid, cancellationToken).ConfigureAwait(false);
            var payloads = new[]
            {
                FeatureCollectionArtifact.WriteFeatureCollection(solution, processId),
                FeatureCollectionArtifact.WriteFeatureCollection(firstOutput, processId),
                FeatureCollectionArtifact.WriteFeatureCollection(secondOutput, processId),
                Encoding.UTF8.GetBytes(solution.Count > 0 ? "true" : "false"),
            };
            Require(payloads.All(payload => payload.Length <= options.CurrentValue.MaxArtifactBytes), "Routing output exceeds MaxArtifactBytes.");
            // Encode/validate all outputs before publishing; provider failures cannot leave partial success artifacts.
            for (var index = 0; index < payloads.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var uri = index == 3 ? "data:application/json;base64," + Convert.ToBase64String(payloads[index])
                    : FeatureCollectionArtifact.BuildDataUri(payloads[index]);
                Require(await context.TryPublishArtifactAsync(uri, cancellationToken).ConfigureAwait(false), "Routing artifact publication lost its execution fence.");
            }
            await context.ReportProgressAsync(100, "Network analysis completed", cancellationToken).ConfigureAwait(false);
            return JobExecutionResult.Succeeded();
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or JsonException or NotSupportedException)
        {
            return JobExecutionResult.Failed(exception.Message);
        }
    }

    private FeatureCollection ReadPoints(IReadOnlyDictionary<string, string> inputs, string name, int cap, bool weighted = false)
    {
        Require(FeatureCollectionArtifact.TryParseDataUri(inputs.GetValueOrDefault(name), out var features, out var error, options.CurrentValue.MaxArtifactBytes), $"{name}: {error}");
        Require(features.Count is > 0 && features.Count <= cap, $"{name} must contain between 1 and {cap} points.");
        var identities = new HashSet<long>();
        for (var index = 0; index < features.Count; index++)
        {
            var feature = features[index];
            Require(feature.Geometry is Point point && !point.IsEmpty && double.IsFinite(point.X) && double.IsFinite(point.Y), $"{name} requires finite, nonempty point geometry for every feature.");
            foreach (var attribute in feature.Attributes.GetNames())
            {
                // Nullable GPFeatureRecordSetLayer defaults request no per-location override.
                if (feature.Attributes[attribute] is null)
                {
                    continue;
                }
                if (attribute.Equals("AdditionalTime", StringComparison.OrdinalIgnoreCase) || attribute.Equals("AdditionalDistance", StringComparison.OrdinalIgnoreCase)
                    || attribute.Equals("AdditionalCost", StringComparison.OrdinalIgnoreCase) || attribute.Equals("CurbApproach", StringComparison.OrdinalIgnoreCase))
                {
                    Require(Number(feature.Attributes[attribute], attribute) == 0, $"Nondefault {attribute} is not supported.");
                }
                else if (attribute.Equals("Cutoff", StringComparison.OrdinalIgnoreCase) || attribute.Equals("Cutoff_TravelTime", StringComparison.OrdinalIgnoreCase)
                    || attribute.Equals("TargetFacilityCount", StringComparison.OrdinalIgnoreCase)
                    || attribute.Equals("TargetDestinationCount", StringComparison.OrdinalIgnoreCase)
                    || attribute.Equals("Capacity", StringComparison.OrdinalIgnoreCase) || attribute.Equals("Attractiveness", StringComparison.OrdinalIgnoreCase))
                {
                    Require(Number(feature.Attributes[attribute], attribute) == 0, $"Nondefault per-location {attribute} is not supported.");
                }
                else if (attribute.Equals("FacilityType", StringComparison.OrdinalIgnoreCase))
                {
                    Require(feature.Attributes[attribute] is null || Number(feature.Attributes[attribute], attribute) == 0,
                        "Only candidate facilities (FacilityType=0) are supported.");
                }
            }
            Require(identities.Add(Identity(features, index)), $"{name} contains duplicate IDs.");
            _ = ObjectId(features, index);
            if (weighted)
            {
                _ = Weight(feature);
            }
        }
        return features;
    }

    private List<RouteBarrier> ReadBarriers(Dictionary<string, string> inputs)
    {
        List<RouteBarrier> barriers = [];
        foreach (var (name, kind) in new[] { ("point_barriers", RouteBarrierKind.Point), ("line_barriers", RouteBarrierKind.Line), ("polygon_barriers", RouteBarrierKind.Polygon) })
        {
            if (!inputs.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }
            Require(FeatureCollectionArtifact.TryParseDataUri(value, out var features, out var error, options.CurrentValue.MaxArtifactBytes), $"{name}: {error}");
            foreach (var feature in features)
            {
                Require(feature.Geometry is { IsEmpty: false }, "Barrier geometry must be nonempty.");
                var geometry = feature.Geometry!;
                Require(kind switch
                {
                    RouteBarrierKind.Point => geometry is NetTopologySuite.Geometries.Point or MultiPoint,
                    RouteBarrierKind.Line => geometry is LineString or MultiLineString,
                    _ => geometry is Polygon or MultiPolygon,
                }, "Barrier geometry does not match its declared family.");
                foreach (var attribute in feature.Attributes.GetNames())
                {
                    if (feature.Attributes[attribute] is null)
                    {
                        continue;
                    }
                    if (attribute.Equals("BarrierType", StringComparison.OrdinalIgnoreCase))
                    {
                        Require(Number(feature.Attributes[attribute], attribute) == 0, "Only restriction barriers are supported.");
                    }
                    else if (attribute.StartsWith("Attr_", StringComparison.OrdinalIgnoreCase))
                    {
                        var adjustment = Number(feature.Attributes[attribute], attribute);
                        Require(adjustment == 0 || (attribute.Equals("Attr_TravelTime", StringComparison.OrdinalIgnoreCase) && adjustment == 1),
                            "Nondefault barrier cost adjustments are not supported.");
                    }
                }
                barriers.Add(new RouteBarrier(kind, GeoJsonArtifactCodec.CreateWriter().Write(geometry)));
            }
        }
        Require(barriers.Count <= configuration.Value.MaxBarriers, "Barrier count exceeds the configured maximum.");
        return barriers;
    }

    private async Task<List<IFeature>> ProjectInputsAsync(FeatureCollection features, int fromSrid, int toSrid, CancellationToken ct)
    {
        List<IFeature> result = [];
        for (var index = 0; index < features.Count; index++)
        {
            var attributes = Attributes(("ID", Identity(features, index)), ("Name", Name(features, index)));
            foreach (var name in features[index].Attributes.GetNames())
            {
                if (!attributes.GetNames().Any(existing => existing.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    attributes.Add(name, features[index].Attributes[name]);
                }
            }
            result.Add(new Feature(await ProjectPointAsync(features[index], fromSrid, toSrid, ct).ConfigureAwait(false), attributes));
        }
        return result;
    }

    private async ValueTask<Point> ProjectPointAsync(IFeature feature, int fromSrid, int toSrid, CancellationToken ct)
    {
        var point = (Point)feature.Geometry;
        if (fromSrid == toSrid)
        {
            return (Point)point.Copy();
        }
        var coordinate = transforms is null ? CoordinateTransformer.TransformPoint(point.X, point.Y, fromSrid, toSrid)
            : await transforms.TransformPointAsync(point.X, point.Y, fromSrid, toSrid, ct).ConfigureAwait(false)
                ?? throw new NotSupportedException("The input-point output projection is unavailable.");
        return new Point(coordinate.X, coordinate.Y) { SRID = toSrid };
    }

    private static RoutePoint Point(IFeature feature) => new(((Point)feature.Geometry).X, ((Point)feature.Geometry).Y);
    private static RoutePoint[] Points(FeatureCollection features) => features.Select(Point).ToArray();
    private static Feature Feature(string? geometry, params (string Name, object Value)[] properties)
        => new Feature(string.IsNullOrWhiteSpace(geometry) ? null : GeoJsonArtifactCodec.CreateReader().Read<Geometry>(geometry), Attributes(properties));
    private static AttributesTable Attributes(params (string Name, object Value)[] properties)
    {
        var result = new AttributesTable();
        foreach (var (name, value) in properties)
        {
            result.Add(name, value);
        }
        return result;
    }
    private static object? Attribute(IFeature feature, string name)
    {
        var key = feature.Attributes.GetNames().FirstOrDefault(key => key.Equals(name, StringComparison.OrdinalIgnoreCase));
        return key is null ? null : feature.Attributes[key];
    }
    private static long Identity(FeatureCollection features, int index)
    {
        Require(index >= 0 && index < features.Count, "The provider returned an invalid location index.");
        var value = Attribute(features[index], "ID") ?? Attribute(features[index], "OBJECTID");
        if (value is null)
        {
            return index + 1;
        }
        var number = Number(value, "ID");
        Require(number >= 0 && number <= 9_007_199_254_740_991 && Math.Truncate(number) == number, "Location IDs must be nonnegative exactly represented integers.");
        return (long)number;
    }
    private static string Name(FeatureCollection features, int index) => Attribute(features[index], "Name")?.ToString() ?? $"Location {index + 1}";
    private static long ObjectId(FeatureCollection features, int index)
    {
        if (Attribute(features[index], "OBJECTID") is not { } value)
        {
            return Identity(features, index);
        }
        var number = Number(value, "OBJECTID");
        Require(number >= 0 && number <= 9_007_199_254_740_991 && Math.Truncate(number) == number,
            "Location OBJECTIDs must be nonnegative exactly represented integers.");
        return (long)number;
    }

    private static long? InputObjectId(FeatureCollection features, int index)
        => Attribute(features[index], "OBJECTID") is null ? null : ObjectId(features, index);

    private static string? ReadTravelMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (!value.TrimStart().StartsWith('{'))
        {
            return value;
        }
        using var document = JsonDocument.Parse(value);
        var mode = document.RootElement;
        Require(mode.ValueKind == JsonValueKind.Object && mode.TryGetProperty("name", out var name)
            && name.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString()),
            "A travel-mode object must name a provider-advertised mode.");
        // These are the same canonical settings advertised by NAServerMetadata.BuildTravelMode.
        // Descriptive identity fields may vary; semantic overrides must match the supported profile.
        foreach (var property in mode.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }
            var supported = property.Name switch
            {
                "name" or "id" or "itemId" or "type" or "description" => property.Value.ValueKind == JsonValueKind.String,
                "impedanceAttributeName" or "timeAttributeName" => property.Value.ValueKind == JsonValueKind.String
                    && property.Value.GetString() == "TravelTime",
                "distanceAttributeName" => property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() == "Kilometers",
                "restrictionAttributeNames" or "attributeParameterValues" => property.Value.ValueKind == JsonValueKind.Array
                    && property.Value.GetArrayLength() == 0,
                "useHierarchy" => property.Value.ValueKind == JsonValueKind.False,
                "uturnAtJunctions" => property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() == "esriNFSBAllowBacktrack",
                "simplificationTolerance" => property.Value.ValueKind == JsonValueKind.Number
                    && property.Value.TryGetDouble(out var tolerance) && tolerance == 10,
                "simplificationToleranceUnits" => property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() == "esriMeters",
                _ => false,
            };
            Require(supported, $"The travel-mode setting '{property.Name}' is not supported by the canonical routing profile.");
        }
        return mode.GetProperty("name").GetString();
    }
    private static void SetAttribute(IFeature feature, string name, object? value)
    {
        var existing = feature.Attributes.GetNames().FirstOrDefault(key => key.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            feature.Attributes.DeleteAttribute(existing);
        }
        feature.Attributes.Add(name, value);
    }
    private static void AddSelectedUnits(IFeature feature, double minutes, double? meters, string timeUnits, string distanceUnits,
        double minutesPerUnit, double metersPerUnit, double? weight = null)
    {
        if (!timeUnits.Equals("Minutes", StringComparison.OrdinalIgnoreCase))
        {
            SetAttribute(feature, "Total_" + timeUnits, minutes / minutesPerUnit);
            if (weight is { } weighted)
            {
                SetAttribute(feature, "TotalWeighted_" + timeUnits, minutes * weighted / minutesPerUnit);
            }
        }
        if (meters is { } distance && !distanceUnits.Equals("Miles", StringComparison.OrdinalIgnoreCase)
            && !distanceUnits.Equals("Kilometers", StringComparison.OrdinalIgnoreCase))
        {
            SetAttribute(feature, "Total_" + distanceUnits, distance / metersPerUnit);
        }
    }
    private static double Weight(IFeature feature)
    {
        var weight = Attribute(feature, "Weight") is { } raw ? Number(raw, "Weight") : 1;
        Require(weight >= 0, "Demand weight must be nonnegative.");
        return weight;
    }
    private static double Number(object? value, string name)
    {
        Require(value is not null && double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed), $"{name} must be finite numeric data.");
        return Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }
    private static int ReadSrid(IReadOnlyDictionary<string, string> inputs, string name)
    {
        var value = inputs.GetValueOrDefault(name) ?? "4326";
        Require(int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var srid) && srid > 0, $"{name} must be a positive SRID.");
        return SpatialReferenceExtensions.NormalizeWebMercatorSrid(srid);
    }
    private static double? ReadPositiveDouble(IReadOnlyDictionary<string, string> inputs, string name)
    {
        if (!inputs.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var result = Number(value, name);
        Require(result > 0, $"{name} must be positive.");
        return result;
    }
    private static int? ReadCount(IReadOnlyDictionary<string, string> inputs, string name)
    {
        var value = ReadPositiveDouble(inputs, name);
        Require(value is null || (value <= int.MaxValue && Math.Truncate(value.Value) == value), $"{name} must be a positive integer.");
        return value is null ? null : (int)value.Value;
    }
    private static void Require(bool condition, string error)
    {
        if (!condition)
        {
            throw new ArgumentException(error);
        }
    }
}
