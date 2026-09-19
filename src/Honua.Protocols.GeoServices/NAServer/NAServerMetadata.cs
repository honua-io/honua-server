// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Routing.Features.Routing.Domain;

namespace Honua.Protocols.GeoServices.NAServer;

/// <summary>
/// Projects the configured routing capability onto the metadata resources Esri
/// clients read before they will address a routing service: the NAServer service
/// resource, its per-solver analysis layers, and the NetworkAnalysisUtilities tasks
/// (<c>GetTravelModes</c>, <c>GetToolInfo</c>).
/// </summary>
/// <remarks>
/// <para>
/// ArcGIS Pro 3.7 and <c>arcpy.nax</c> bind a stand-alone routing service through a
/// dictionary <c>{url: .../NAServer, utilityUrl: .../GPServer}</c> and, before any
/// solve, execute <c>GetTravelModes</c> then <c>GetToolInfo</c> on the utility service;
/// the Pro Catalog pane reads the service resource and the analysis layer resource.
/// Until #5035 Honua served the solve operations only, so no Pro or arcpy routing
/// client could reach them ("Task 'GetTravelModes' on service ... was not found").
/// </para>
/// <para>
/// Everything here derives from what the provider actually advertises: one analysis
/// layer per solver the <see cref="RoutingProviderCapabilities"/> support, one travel
/// mode per <see cref="RoutingTravelProfile"/> of the active <see cref="NetworkDataset"/>,
/// and service limits from <see cref="RoutingConfiguration"/>. Nothing is stated that a
/// solve would then refuse.
/// </para>
/// </remarks>
internal static class NAServerMetadata
{
    /// <summary>The synthetic NetworkAnalysisUtilities tasks published on every GPServer.</summary>
    public const string GetTravelModesTask = "GetTravelModes";

    /// <inheritdoc cref="GetTravelModesTask"/>
    public const string GetToolInfoTask = "GetToolInfo";

    /// <summary>Utility task names in the order they are listed.</summary>
    public static readonly string[] UtilityTaskNames = [GetToolInfoTask, GetTravelModesTask];

    /// <summary>Impedance attribute name advertised for every travel mode.</summary>
    public const string TimeAttributeName = "TravelTime";

    /// <summary>Distance attribute name advertised for every travel mode.</summary>
    public const string DistanceAttributeName = "Kilometers";

    private static readonly JsonSerializerOptions CompactJson = new() { WriteIndented = false };
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    /// <summary>An analysis layer Honua can publish, gated on a provider capability.</summary>
    private sealed record AnalysisLayer(
        string Name,
        string LayerType,
        string ServiceCollection,
        string EsriServiceName,
        string EsriToolName,
        Func<RoutingProviderCapabilities, bool> IsSupported);

    private static readonly AnalysisLayer[] Layers =
    [
        new("Route", "esriNAServerRouteLayer", "routeLayers", "asyncRoute", "FindRoutes", c => c.SupportsRoute),
        new("ServiceArea", "esriNAServerServiceAreaLayer", "serviceAreaLayers", "asyncServiceArea", "GenerateServiceAreas", c => c.SupportsServiceArea),
        new("ClosestFacility", "esriNAServerClosestFacilityLayer", "closestFacilityLayers", "asyncClosestFacility", "FindClosestFacilities", c => c.SupportsClosestFacility),
        new("ODCostMatrix", "esriNAServerODCostMatrixLayer", "odCostMatrixLayers", "asyncODCostMatrix", "GenerateOriginDestinationCostMatrix", c => c.SupportsOdCostMatrix),
        new("LocationAllocation", "esriNAServerLocationAllocationLayer", "locationAllocationLayers", "asyncLocationAllocation", "SolveLocationAllocation", c => c.SupportsLocationAllocation),
    ];

    public static bool IsUtilityTask(string? taskName)
        => taskName is not null
           && (taskName.Equals(GetTravelModesTask, StringComparison.OrdinalIgnoreCase)
               || taskName.Equals(GetToolInfoTask, StringComparison.OrdinalIgnoreCase));

    public static bool IsKnownLayer(string? layerName)
        => layerName is not null && Layers.Any(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Serializes a document as compact or indented JSON.</summary>
    public static string Serialize(JsonNode document, bool pretty)
        => document.ToJsonString(pretty ? PrettyJson : CompactJson);

    // -----------------------------------------------------------------------
    // NAServer service and layer resources
    // -----------------------------------------------------------------------

    /// <summary>The NAServer service resource: one entry per supported solver.</summary>
    public static JsonObject BuildServiceResource(string serviceId, RoutingProviderCapabilities capabilities)
    {
        var document = new JsonObject
        {
            ["serviceDescription"] = $"Network analysis service for {serviceId}",
        };
        foreach (var layer in Layers)
        {
            var names = new JsonArray();
            if (layer.IsSupported(capabilities))
            {
                // JsonValue.Create(string) writes the primitive directly; the generic
                // Add<T> would route through the serializer, which the published
                // (reflection-free) host cannot satisfy for System.String.
                names.Add(JsonValue.Create(layer.Name));
            }

            document[layer.ServiceCollection] = names;
        }

        document["serviceLimits"] = BuildServiceLimits(null);
        return document;
    }

    /// <summary>
    /// The analysis layer resource, or <c>null</c> when the layer is unknown or the
    /// provider does not support that solver.
    /// </summary>
    public static JsonObject? BuildLayerResource(
        string layerName,
        RoutingProviderCapabilities capabilities,
        NetworkDataset dataset,
        RoutingConfiguration configuration)
    {
        var layer = Layers.FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
        if (layer is null || !layer.IsSupported(capabilities))
        {
            return null;
        }

        var travelModes = BuildTravelModes(dataset);
        var document = new JsonObject
        {
            ["layerName"] = layer.Name,
            ["layerType"] = layer.LayerType,
            ["impedance"] = TimeAttributeName,
            ["restrictions"] = new JsonArray(),
            ["snapTolerance"] = 5000,
            ["maxSnapTolerance"] = 5000,
            ["snapToleranceUnits"] = "esriMeters",
            ["ignoreInvalidLocations"] = true,
            ["restrictUTurns"] = "esriNFSBAllowBacktrack",
            ["useHierarchy"] = false,
            ["hasZ"] = false,
            ["hasM"] = false,
            ["outputSpatialReference"] = SpatialReference(dataset.Srid),
            ["defaultTravelMode"] = TravelModeId(dataset.TravelProfiles[0]),
            ["supportedTravelModes"] = new JsonArray([.. travelModes]),
            ["networkDataset"] = BuildNetworkDataset(dataset),
            ["networkClasses"] = BuildNetworkClasses(layer.Name),
            ["accumulateAttributeNames"] = new JsonArray(),
            ["attributeParameterValues"] = new JsonArray(),
            ["trafficSupport"] = "NONE",
            ["serviceLimits"] = BuildServiceLimits(configuration),
        };

        switch (layer.Name)
        {
            case "Route":
                document["returnDirections"] = false;
                document["supportsDirections"] = true;
                document["directionsLanguage"] = "en";
                document["directionsSupportedLanguages"] = new JsonArray("en");
                document["directionsLengthUnits"] = "esriNAUKilometers";
                document["directionsTimeAttribute"] = TimeAttributeName;
                document["findBestSequence"] = false;
                document["preserveFirstStop"] = true;
                document["preserveLastStop"] = true;
                document["useTimeWindows"] = false;
                document["outputLineType"] = "esriNAOutputLineTrueShape";
                document["supportsPreservingObjectID"] = false;
                break;
            case "ServiceArea":
                document["defaultBreaks"] = new JsonArray(5, 10, 15);
                document["travelDirection"] = "esriNATravelDirectionFromFacility";
                document["outputPolygons"] = "esriNAOutputPolygonSimplified";
                document["outputLines"] = "esriNAOutputLineNone";
                document["splitPolygonsAtBreaks"] = true;
                document["overlapPolygons"] = true;
                document["mergeSimilarPolygonRanges"] = false;
                document["trimOuterPolygon"] = false;
                document["trimPolygonDistance"] = 100;
                document["trimPolygonDistanceUnits"] = "esriMeters";
                break;
            case "ClosestFacility":
                document["defaultCutoff"] = null;
                document["defaultTargetFacilityCount"] = 1;
                document["travelDirection"] = "esriNATravelDirectionToFacility";
                document["outputLineType"] = "esriNAOutputLineTrueShape";
                document["supportsDirections"] = true;
                break;
            case "ODCostMatrix":
                document["defaultCutoff"] = null;
                document["defaultTargetDestinationCount"] = null;
                document["outputLineType"] = capabilities.SupportsOdStraightLines
                    ? "esriNAOutputLineStraight"
                    : "esriNAOutputLineNone";
                break;
            case "LocationAllocation":
                document["problemType"] = "esriNAMinimizeImpedance";
                document["travelDirection"] = "esriNATravelDirectionFromFacility";
                document["defaultCutoff"] = null;
                document["outputLineType"] = "esriNAOutputLineStraight";
                break;
        }

        return document;
    }

    // -----------------------------------------------------------------------
    // NetworkAnalysisUtilities tasks
    // -----------------------------------------------------------------------

    /// <summary>The utility task resource (<c>GET .../GPServer/GetTravelModes</c>).</summary>
    public static JsonObject BuildUtilityTaskInfo(string taskName)
    {
        var isTravelModes = taskName.Equals(GetTravelModesTask, StringComparison.OrdinalIgnoreCase);
        var parameters = new JsonArray();
        if (!isTravelModes)
        {
            parameters.Add(Parameter("serviceName", "GPString", "Service Name",
                "The network analysis service the tool belongs to, for example asyncRoute or asyncServiceArea.",
                "esriGPParameterDirectionInput", "esriGPParameterTypeRequired", "asyncRoute"));
            parameters.Add(Parameter("toolName", "GPString", "Tool Name",
                "The tool whose limits and network description are requested, for example FindRoutes or GenerateServiceAreas.",
                "esriGPParameterDirectionInput", "esriGPParameterTypeRequired", "FindRoutes"));
            parameters.Add(Parameter("toolInfo", "GPString", "Tool Info",
                "JSON describing the network dataset behind the tool and the service limits that apply to it.",
                "esriGPParameterDirectionOutput", "esriGPParameterTypeDerived", null));
        }
        else
        {
            parameters.Add(Parameter("supportedTravelModes", "GPRecordSet", "Supported Travel Modes",
                "One record per travel mode the routing service accepts; TravelMode holds the mode's JSON settings.",
                "esriGPParameterDirectionOutput", "esriGPParameterTypeDerived", null));
            parameters.Add(Parameter("defaultTravelMode", "GPString", "Default Travel Mode",
                "The TravelModeId a client should select when the user has not chosen one.",
                "esriGPParameterDirectionOutput", "esriGPParameterTypeDerived", null));
        }

        return new JsonObject
        {
            ["name"] = isTravelModes ? GetTravelModesTask : GetToolInfoTask,
            ["displayName"] = isTravelModes ? "Get Travel Modes" : "Get Tool Info",
            ["description"] = isTravelModes
                ? "Returns the travel modes the routing service supports, in the form ArcGIS clients read before solving."
                : "Returns the network dataset description and processing limits of a network analysis tool.",
            ["category"] = "network-analysis",
            ["helpUrl"] = "",
            ["executionType"] = "esriExecutionTypeSynchronous",
            ["parameters"] = parameters,
        };
    }

    /// <summary>The <c>GetTravelModes/execute</c> result envelope.</summary>
    public static JsonObject BuildGetTravelModesResult(NetworkDataset dataset)
    {
        var features = new JsonArray();
        var objectId = 1;
        foreach (var profile in dataset.TravelProfiles)
        {
            var mode = BuildTravelMode(profile, dataset);
            features.Add(new JsonObject
            {
                ["attributes"] = new JsonObject
                {
                    ["ObjectID"] = objectId++,
                    ["Name"] = mode["name"]!.GetValue<string>(),
                    ["TravelModeId"] = mode["id"]!.GetValue<string>(),
                    ["TravelMode"] = mode.ToJsonString(CompactJson),
                    ["AltName"] = mode["name"]!.GetValue<string>(),
                },
            });
        }

        var recordSet = new JsonObject
        {
            ["displayFieldName"] = "",
            ["fields"] = new JsonArray(
                Field("ObjectID", "esriFieldTypeOID", "ObjectID", null),
                Field("Name", "esriFieldTypeString", "Travel Mode Name", 255),
                Field("TravelModeId", "esriFieldTypeString", "Travel Mode Identifier", 50),
                Field("TravelMode", "esriFieldTypeString", "Travel Mode Settings", 65536),
                Field("AltName", "esriFieldTypeString", "Alternate Travel Mode Name", 255)),
            ["features"] = features,
            ["exceededTransferLimit"] = false,
        };

        return new JsonObject
        {
            ["results"] = new JsonArray(
                new JsonObject
                {
                    ["paramName"] = "supportedTravelModes",
                    ["dataType"] = "GPRecordSet",
                    ["value"] = recordSet,
                },
                new JsonObject
                {
                    ["paramName"] = "defaultTravelMode",
                    ["dataType"] = "GPString",
                    ["value"] = TravelModeId(dataset.TravelProfiles[0]),
                }),
            ["messages"] = new JsonArray(),
        };
    }

    /// <summary>
    /// The <c>GetToolInfo/execute</c> result envelope, or <c>null</c> when
    /// <paramref name="toolName"/> names no solver the provider supports.
    /// </summary>
    public static JsonObject? BuildGetToolInfoResult(
        string? serviceName,
        string? toolName,
        RoutingProviderCapabilities capabilities,
        NetworkDataset dataset,
        RoutingConfiguration configuration,
        bool includeNetworkSourceInfo = false)
    {
        var layer = ResolveLayer(serviceName, toolName);
        if (layer is null || !layer.IsSupported(capabilities))
        {
            return null;
        }

        // Esri publishes toolInfo as an embedded JSON object (the documented example
        // shows "value": { "networkDataset": ..., "serviceLimits": ... }), not as an
        // encoded string, and arcpy.nax reads it that way.
        var networkDataset = new JsonObject
        {
            ["name"] = dataset.Name,
            ["attributeParameterValues"] = new JsonArray(),
            ["networkAttributes"] = BuildNetworkAttributes(),
            ["trafficSupport"] = "NONE",
        };
        if (includeNetworkSourceInfo)
        {
            networkDataset["networkSources"] = BuildNetworkSources(dataset, includeSchema: true);
        }

        var toolInfo = new JsonObject
        {
            ["networkDataset"] = networkDataset,
            ["serviceLimits"] = BuildServiceLimits(configuration),
            ["supportedTravelModes"] = new JsonArray([.. BuildTravelModes(dataset)]),
            ["defaultTravelMode"] = TravelModeId(dataset.TravelProfiles[0]),
        };

        return new JsonObject
        {
            ["results"] = new JsonArray(
                new JsonObject
                {
                    ["paramName"] = "toolInfo",
                    ["dataType"] = "GPString",
                    ["value"] = toolInfo,
                }),
            ["messages"] = new JsonArray(),
        };
    }

    /// <summary>Names an Esri utility tool that maps to no supported solver.</summary>
    public static string DescribeToolNames()
        => string.Join(", ", Layers.Select(l => $"{l.EsriServiceName}/{l.EsriToolName} ({l.Name})"));

    // -----------------------------------------------------------------------
    // Building blocks
    // -----------------------------------------------------------------------

    private static AnalysisLayer? ResolveLayer(string? serviceName, string? toolName)
    {
        foreach (var layer in Layers)
        {
            if (!string.IsNullOrWhiteSpace(toolName)
                && (layer.EsriToolName.Equals(toolName, StringComparison.OrdinalIgnoreCase)
                    || layer.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase)))
            {
                return layer;
            }
        }

        foreach (var layer in Layers)
        {
            if (!string.IsNullOrWhiteSpace(serviceName)
                && (layer.EsriServiceName.Equals(serviceName, StringComparison.OrdinalIgnoreCase)
                    || layer.Name.Equals(serviceName, StringComparison.OrdinalIgnoreCase)))
            {
                return layer;
            }
        }

        return null;
    }

    private static List<JsonObject> BuildTravelModes(NetworkDataset dataset)
        => dataset.TravelProfiles.Select(profile => BuildTravelMode(profile, dataset)).ToList();

    private static JsonObject BuildTravelMode(RoutingTravelProfile profile, NetworkDataset dataset)
        => new()
        {
            ["id"] = TravelModeId(profile),
            ["name"] = TravelModeName(profile),
            ["type"] = TravelModeType(profile),
            ["description"] = $"Travel profile '{profile.Name}' of network dataset '{dataset.Name}' "
                              + $"(cost column {profile.ForwardCostColumn}, reverse {profile.ReverseCostColumn}).",
            ["impedanceAttributeName"] = TimeAttributeName,
            ["timeAttributeName"] = TimeAttributeName,
            ["distanceAttributeName"] = DistanceAttributeName,
            ["restrictionAttributeNames"] = new JsonArray(),
            ["attributeParameterValues"] = new JsonArray(),
            ["useHierarchy"] = false,
            ["uturnAtJunctions"] = "esriNFSBAllowBacktrack",
            ["simplificationTolerance"] = 10,
            ["simplificationToleranceUnits"] = "esriMeters",
        };

    /// <summary>
    /// A stable 16-character identifier per profile name (Esri ids are 16 characters);
    /// clients persist the id in saved layers, so it must not change between requests.
    /// </summary>
    internal static string TravelModeId(RoutingTravelProfile profile)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("honua-travel-mode:" + profile.Name.ToLowerInvariant()));
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        var id = new char[16];
        for (var i = 0; i < id.Length; i++)
        {
            id[i] = alphabet[hash[i] % alphabet.Length];
        }

        return new string(id);
    }

    private static string TravelModeName(RoutingTravelProfile profile)
    {
        var name = profile.Name.Trim();
        if (name.Length == 0)
        {
            return "Driving Time";
        }

        return char.ToUpperInvariant(name[0]) + name[1..] + " Time";
    }

    private static string TravelModeType(RoutingTravelProfile profile)
    {
        var name = profile.Name.ToLowerInvariant();
        if (name.Contains("walk", StringComparison.Ordinal) || name.Contains("pedestrian", StringComparison.Ordinal))
        {
            return "WALK";
        }

        if (name.Contains("truck", StringComparison.Ordinal))
        {
            return "TRUCK";
        }

        return "AUTOMOBILE";
    }

    private static JsonObject BuildNetworkDataset(NetworkDataset dataset)
        => new()
        {
            ["name"] = dataset.Name,
            ["buildTime"] = 0,
            ["state"] = "esriNDSStateBuilt",
            // The analysis layer resource speaks the esriNA* enumeration vocabulary a
            // real ArcGIS Server layer uses (esriNADTDouble, esriNAUMinutes, ...).
            ["networkAttributes"] = BuildNetworkAttributes(esriVocabulary: true),
            ["networkSources"] = BuildNetworkSources(dataset, includeSchema: false),
        };

    /// <summary>
    /// The edge and junction sources of the dataset. With <paramref name="includeSchema"/>
    /// each source also carries the (minimal) field schema, which is what
    /// <c>GetToolInfo?includeNetworkSourceInfo=true</c> asks for.
    /// </summary>
    private static JsonArray BuildNetworkSources(NetworkDataset dataset, bool includeSchema)
    {
        JsonObject Source(int id, string name, string elementType, string sourceType)
        {
            var source = new JsonObject
            {
                ["id"] = id,
                ["name"] = name,
                ["elementType"] = elementType,
                ["sourceType"] = sourceType,
            };
            if (includeSchema)
            {
                source["fields"] = new JsonArray(
                    Field("gid", "esriFieldTypeOID", "gid", null),
                    Field("name", "esriFieldTypeString", "name", 255));
            }

            return source;
        }

        return new JsonArray(
            Source(1, dataset.EdgeTable, "esriNETEdge", "esriNSTEdgeFeature"),
            Source(2, dataset.VertexTable, "esriNETJunction", "esriNSTSystemJunction"));
    }

    /// <summary>
    /// The two cost attributes every travel mode references. The utility task speaks
    /// the plain vocabulary Esri documents for <c>GetToolInfo</c> ("Double", "Minutes",
    /// "Cost", "NONE"); the analysis layer resource speaks the esriNA* enumerations.
    /// </summary>
    private static JsonArray BuildNetworkAttributes(bool esriVocabulary = false)
    {
        JsonObject Attribute(string name, string units, string esriUnits)
            => new()
            {
                ["name"] = name,
                ["dataType"] = esriVocabulary ? "esriNADTDouble" : "Double",
                ["units"] = esriVocabulary ? esriUnits : units,
                ["usageType"] = esriVocabulary ? "esriNAUTCost" : "Cost",
                ["parameterNames"] = new JsonArray(),
                ["restrictionUsageParameterName"] = null,
                ["trafficSupport"] = esriVocabulary ? "esriNTSNone" : "NONE",
            };

        return new JsonArray(
            Attribute(TimeAttributeName, "Minutes", "esriNAUMinutes"),
            Attribute(DistanceAttributeName, "Kilometers", "esriNAUKilometers"));
    }

    private static JsonArray BuildNetworkClasses(string layerName)
    {
        var classes = new JsonArray();
        var inputs = layerName switch
        {
            "Route" => new[] { "Stops" },
            "ServiceArea" => new[] { "Facilities" },
            "ClosestFacility" => new[] { "Incidents", "Facilities" },
            "ODCostMatrix" => new[] { "Origins", "Destinations" },
            "LocationAllocation" => new[] { "Facilities", "DemandPoints" },
            _ => Array.Empty<string>(),
        };
        foreach (var name in inputs.Concat(["Barriers", "PolylineBarriers", "PolygonBarriers"]))
        {
            classes.Add(new JsonObject
            {
                ["className"] = name,
                ["candidateFieldNames"] = new JsonArray("Name"),
                ["fields"] = new JsonArray(
                    Field("ObjectID", "esriFieldTypeOID", "ObjectID", null),
                    Field("Name", "esriFieldTypeString", "Name", 500)),
            });
        }

        return classes;
    }

    private static JsonObject BuildServiceLimits(RoutingConfiguration? configuration)
    {
        var limits = new JsonObject();
        if (configuration is null)
        {
            return limits;
        }

        limits["maximumFeaturesAffectedByPointBarriers"] = configuration.MaxBarriers;
        limits["maximumFeaturesAffectedByLineBarriers"] = configuration.MaxBarriers;
        limits["maximumFeaturesAffectedByPolygonBarriers"] = configuration.MaxBarriers;
        limits["maximumStops"] = configuration.MaxStops;
        limits["maximumFacilities"] = configuration.MaxFacilities;
        limits["maximumIncidents"] = configuration.MaxIncidents;
        limits["maximumFacilitiesToFind"] = configuration.MaxClosestFacilities;
        limits["maximumOrigins"] = configuration.MaxOrigins;
        limits["maximumDestinations"] = configuration.MaxDestinations;
        limits["maximumBreaks"] = configuration.MaxBreaks;
        return limits;
    }

    private static JsonObject SpatialReference(int srid)
        => new() { ["wkid"] = srid, ["latestWkid"] = srid };

    private static JsonObject Field(string name, string type, string alias, int? length)
    {
        var field = new JsonObject { ["name"] = name, ["type"] = type, ["alias"] = alias };
        if (length is not null)
        {
            field["length"] = length;
        }

        return field;
    }

    private static JsonObject Parameter(
        string name, string dataType, string displayName, string description,
        string direction, string parameterType, string? defaultValue)
        => new()
        {
            ["name"] = name,
            ["dataType"] = dataType,
            ["displayName"] = displayName,
            ["description"] = description,
            ["direction"] = direction,
            ["defaultValue"] = defaultValue,
            ["parameterType"] = parameterType,
        };
}
