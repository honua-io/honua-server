// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.Protocols.GeoServices.GPServer.Models;
using Honua.Protocols.GeoServices.NAServer;

namespace Honua.Protocols.GeoServices.GPServer;

/// <summary>Esri ready-to-use task projection over canonical durable routing processes.</summary>
internal static class RoutingGPTasks
{
    internal static bool IsReadyToUseTask(string taskName)
        => taskName.Equals("FindRoutes", StringComparison.OrdinalIgnoreCase)
            || taskName.Equals("GenerateServiceAreas", StringComparison.OrdinalIgnoreCase);

    internal static string? OutputName(string processId, int index) => processId switch
    {
        RoutingProcessDefinitions.Route => index == 0 ? "Output_Routes" : "Solve_Succeeded",
        RoutingProcessDefinitions.ServiceArea => index == 0 ? "Service_Areas" : "Solve_Succeeded",
        _ => null,
    };

    internal static GPTaskInfoResponse BuildTaskInfo(string taskName, ProcessDefinition definition)
    {
        var route = definition.ProcessId == RoutingProcessDefinitions.Route;
        var parameters = new List<GPParameterInfo>
        {
            Input(route ? "Stops" : "Facilities", "GPFeatureRecordSetLayer", required: true, geometryType: "esriGeometryPoint"),
        };
        if (!route)
        {
            parameters.Add(Input("Break_Values", "GPString", required: true, defaultJson: "\"5\""));
        }
        parameters.Add(Input(route ? "Measurement_Units" : "Break_Units", "GPString", required: true, defaultJson: "\"Minutes\"", choices: ["Minutes"]));
        parameters.Add(Input("Travel_Mode", "GPString"));
        parameters.Add(route
            ? Input("Reorder_Stops_to_Find_Optimal_Routes", "GPBoolean", defaultJson: "false")
            : Input("Travel_Direction", "GPString", defaultJson: "\"Away From Facility\"", choices: ["Away From Facility", "Towards Facility"]));
        foreach (var (kind, geometryType) in new[]
                 {
                     ("Point", "esriGeometryPoint"), ("Line", "esriGeometryPolyline"), ("Polygon", "esriGeometryPolygon"),
                 })
        {
            parameters.Add(Input(kind + "_Barriers", "GPFeatureRecordSetLayer", geometryType: geometryType));
        }
        parameters.Add(new GPParameterInfo
        {
            Name = OutputName(definition.ProcessId, 0),
            DisplayName = route ? "Routes" : "Service Areas",
            DataType = "GPFeatureRecordSetLayer",
            Direction = "esriGPParameterDirectionOutput",
            ParameterType = "esriGPParameterTypeDerived",
            DefaultValue = OutputFeatureSet(definition.ProcessId),
        });
        parameters.Add(new GPParameterInfo
        {
            Name = "Solve_Succeeded",
            DisplayName = "Solve Succeeded",
            DataType = "GPBoolean",
            Direction = "esriGPParameterDirectionOutput",
            ParameterType = "esriGPParameterTypeDerived",
        });
        return new GPTaskInfoResponse
        {
            Name = taskName,
            DisplayName = definition.Title,
            Description = definition.Description,
            Category = "Network Analysis",
            ExecutionType = "esriExecutionTypeAsynchronous",
            HelpUrl = string.Empty,
            Parameters = [.. parameters],
        };
    }

    private static GPParameterInfo Input(string name, string dataType, bool required = false, string? defaultJson = null,
        string[]? choices = null, string? geometryType = null)
    {
        using var document = defaultJson is null ? null : JsonDocument.Parse(defaultJson);
        return new GPParameterInfo
        {
            Name = name,
            DisplayName = name.Replace('_', ' '),
            DataType = dataType,
            Direction = "esriGPParameterDirectionInput",
            ParameterType = required ? "esriGPParameterTypeRequired" : "esriGPParameterTypeOptional",
            DefaultValue = geometryType is null ? document?.RootElement.Clone() : FeatureSet(geometryType),
            ChoiceList = choices,
        };
    }

    private static JsonElement OutputFeatureSet(string processId)
        => processId == RoutingProcessDefinitions.Route
            ? FeatureSet("esriGeometryPolyline", ("Total_Length", "esriFieldTypeDouble"), ("Total_TravelTime", "esriFieldTypeDouble"))
            : FeatureSet("esriGeometryPolygon", ("FacilityID", "esriFieldTypeInteger"), ("FromBreak", "esriFieldTypeDouble"), ("ToBreak", "esriFieldTypeDouble"));

    private static JsonElement FeatureSet(string geometryType, params (string Name, string Type)[] attributeFields)
    {
        // Publish geometry and CRS so clients can materialize the task's input
        // and output schemas. WGS84 is the template CRS, independent of the
        // network's storage CRS; submitted features carry their own input CRS.
        var fields = new JsonArray
        {
            new JsonObject { ["name"] = "OBJECTID", ["alias"] = "OBJECTID", ["type"] = "esriFieldTypeOID" },
        };
        foreach (var (name, type) in attributeFields)
        {
            fields.Add(new JsonObject { ["name"] = name, ["alias"] = name, ["type"] = type });
        }
        var schema = new JsonObject
        {
            ["geometryType"] = geometryType,
            ["spatialReference"] = new JsonObject { ["wkid"] = 4326 },
            ["objectIdFieldName"] = "OBJECTID",
            ["fields"] = fields,
            ["features"] = new JsonArray(),
        };
        using var document = JsonDocument.Parse(schema.ToJsonString());
        return document.RootElement.Clone();
    }

    internal static Dictionary<string, string> TranslateServiceAreaParameters(IReadOnlyDictionary<string, string> parameters)
    {
        var translated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in parameters)
        {
            var target = key.ToLowerInvariant() switch
            {
                "facilities" => "facilities",
                "break_values" => "defaultBreaks",
                "travel_mode" => "travelMode",
                "travel_direction" => "travelDirection",
                "point_barriers" => "barriers",
                "line_barriers" => "polylineBarriers",
                "polygon_barriers" => "polygonBarriers",
                "insr" => "inSR",
                "outsr" => "outSR",
                "break_units" => "units",
                _ => throw new NAServerParameterTranslation.NAServerParameterException($"GenerateServiceAreas parameter '{key}' is not supported."),
            };
            var mapped = value;
            if (target == "units" && !value.Equals("Minutes", StringComparison.OrdinalIgnoreCase))
            {
                throw new NAServerParameterTranslation.NAServerParameterException("GenerateServiceAreas supports only Minutes.");
            }
            if (target == "travelDirection")
            {
                mapped = value.ToLowerInvariant() switch
                {
                    "away from facility" => "0",
                    "towards facility" => "1",
                    _ => throw new NAServerParameterTranslation.NAServerParameterException("Unsupported Travel_Direction."),
                };
            }
            if (target == "defaultBreaks")
            {
                mapped = string.Join(',', value.Split([' ', ';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
            translated[target] = mapped;
        }
        return translated;
    }

    internal static string DataType(ExecutionJobRecord job, ArtifactKind kind)
        => IsRoutingJob(job) && kind == ArtifactKind.Scalar ? "GPBoolean" : GPServerParameterTranslation.ToEsriDataType(kind);

    private static bool IsRoutingJob(ExecutionJobRecord job)
        => GeoprocessingDispatchHelper.ResolveProcessId(job.Spec.Parameters) is { } processId && RoutingProcessDefinitions.IsRouting(processId);

    internal static JsonElement TranslateResult(ExecutionJobRecord job, ArtifactKind kind, string value, int srid, string? schema)
    {
        if (!IsRoutingJob(job))
        {
            return GPServerEsriOutputTranslation.Translate(kind, value, srid, schema);
        }
        if (kind == ArtifactKind.Scalar)
        {
            const string prefix = "data:application/json;base64,";
            if (!value.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The routing result is not a boolean artifact.");
            }
            using var solved = JsonDocument.Parse(Convert.FromBase64String(value[prefix.Length..]));
            if (solved.RootElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new InvalidOperationException("The routing result is not a boolean artifact.");
            }
            return solved.RootElement.Clone();
        }
        // An unsolved route or empty service area still needs a materializable
        // feature-class schema. Infer values from features while retaining the
        // published output contract when there are no features to inspect.
        var outputSchema = OutputFeatureSet(GeoprocessingDispatchHelper.ResolveProcessId(job.Spec.Parameters)!);
        var result = JsonNode.Parse(GPServerEsriOutputTranslation.Translate(kind, value, srid, outputSchema.GetRawText()).GetRawText())!.AsObject();
        var fields = result["fields"]!.AsArray();
        foreach (var (canonical, esri) in new[]
                 {
                     ("totalLengthMeters", "Total_Length"), ("totalTimeMinutes", "Total_TravelTime"),
                     ("facilityId", "FacilityID"), ("fromBreak", "FromBreak"), ("toBreak", "ToBreak"),
                 })
        {
            foreach (var field in fields.Where(field => field!["name"]!.GetValue<string>() == canonical))
            {
                field!["name"] = esri;
                field["alias"] = esri;
            }
            foreach (var attributes in result["features"]!.AsArray().Select(feature => feature!["attributes"]!.AsObject()))
            {
                if (attributes.Remove(canonical, out var attribute))
                {
                    attributes[esri] = attribute;
                }
            }
        }
        using var document = JsonDocument.Parse(result.ToJsonString());
        return document.RootElement.Clone();
    }
}
