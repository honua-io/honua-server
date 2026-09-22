// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
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
    /// <summary>
    /// The synthetic NetworkAnalysisUtilities tasks every GPServer resolves by name. They
    /// are not listed in the GP service resource: that listing is the callable-name set
    /// SOAP <c>GetTaskNames</c> mirrors, and these tasks have no SOAP or job form.
    /// </summary>
    public const string GetTravelModesTask = "GetTravelModes";

    /// <inheritdoc cref="GetTravelModesTask"/>
    public const string GetToolInfoTask = "GetToolInfo";

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

        document["networkDatasetLayers"] = new JsonArray();
        document["serviceLimits"] = new JsonObject();
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

        // Shaped after a real ArcGIS Enterprise 11.5 analysis layer: travel modes carry
        // an ordinal itemId here (the 16-character id lives in GetTravelModes),
        // defaultTravelMode is that ordinal, network classes describe their fields as
        // fieldName/defaultValue/candidateFields, and the locate settings name the
        // sources. ArcGIS Pro's native reader is exact about these shapes.
        var travelModes = dataset.TravelProfiles
            .Select((profile, index) => BuildTravelMode(profile, dataset, itemId: (index + 1).ToString(CultureInfo.InvariantCulture)))
            .ToList();
        var document = new JsonObject
        {
            ["layerName"] = layer.Name,
            ["layerType"] = layer.LayerType,
            ["impedance"] = TimeAttributeName,
            ["restrictions"] = new JsonArray(),
            ["snapTolerance"] = 0,
            ["maxSnapTolerance"] = 20000,
            ["snapToleranceUnits"] = "esriMeters",
            ["locateSettings"] = new JsonObject
            {
                ["default"] = new JsonObject
                {
                    ["tolerance"] = 20000,
                    ["toleranceUnits"] = "esriMeters",
                    ["allowAutoRelocate"] = true,
                    ["sources"] = new JsonArray(new JsonObject { ["name"] = dataset.EdgeTable }),
                },
            },
            ["ignoreInvalidLocations"] = true,
            ["restrictUTurns"] = "esriNFSBAllowBacktrack",
            ["useHierarchy"] = false,
            ["hierarchyAttributeName"] = "",
            ["hierarchyLevelCount"] = 0,
            ["hierarchyMaxValues"] = new JsonArray(),
            ["hierarchyNumTransitions"] = new JsonArray(),
            ["hasZ"] = false,
            ["hasM"] = false,
            ["outputSpatialReference"] = new JsonObject { ["wkid"] = dataset.Srid },
            ["defaultTravelMode"] = "1",
            ["supportedTravelModes"] = new JsonArray([.. travelModes]),
            ["networkDataset"] = BuildNetworkDataset(dataset),
            ["networkClasses"] = BuildNetworkClasses(layer.Name),
            ["accumulateAttributeNames"] = new JsonArray(),
            ["attributeParameterValues"] = new JsonArray(),
            ["trafficSupport"] = "esriNTSNone",
            ["startTime"] = null,
            ["startTimeIsUTC"] = false,
            ["useStartTime"] = false,
            ["timeWindowsAreUTC"] = false,
            ["preserveObjectID"] = false,
            ["serviceLimits"] = new JsonObject(),
        };

        switch (layer.Name)
        {
            case "Route":
                document["returnDirections"] = true;
                document["supportsDirections"] = true;
                document["directionsLanguage"] = "en";
                document["directionsSupportedLanguages"] = new JsonArray("en");
                document["directionsStyleNames"] = new JsonArray("NA Desktop", "NA Navigation");
                document["directionsLengthUnits"] = "esriNAUKilometers";
                document["directionsTimeAttribute"] = TimeAttributeName;
                document["findBestSequence"] = false;
                document["preserveFirstStop"] = true;
                document["preserveLastStop"] = true;
                document["useTimeWindows"] = false;
                document["outputLineType"] = "esriNAOutputLineTrueShape";
                document["supportsPreservingObjectID"] = true;
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
    /// The <c>GetToolInfo/execute</c> result envelope. It always describes the network;
    /// the <c>serviceLimits</c> section is tool-specific only when the named tool maps
    /// to a solver the provider supports.
    /// </summary>
    public static JsonObject BuildGetToolInfoResult(
        string? serviceName,
        string? toolName,
        RoutingProviderCapabilities capabilities,
        RoutingConfiguration configuration)
    {
        // ArcGIS Pro's native reader expects a toolInfo document back from every
        // GetToolInfo call and dereferences an error envelope. In portal mode (captured
        // on Pro 3.7.1 through a logging proxy) it calls the task with only
        // f=json&includeNetworkSourceInfo=true - no serviceName, no toolName - and in
        // stand-alone mode it names asyncRoute/FindRoutes; it may also ask about tools
        // no solver here owns (asyncVRP, ...). The document describes the network in
        // every case; only the serviceLimits section varies with the tool named.
        var layer = ResolveLayer(serviceName, toolName);
        var limitsFor = layer is not null && layer.IsSupported(capabilities) ? layer.Name : string.Empty;

        // Esri publishes toolInfo as an embedded JSON object, exactly the shape an
        // ArcGIS Enterprise 11.5 NetworkAnalysisUtilities service answers with:
        // isPortal, networkDataset {attributeParameterValues, defaultCostAttribute,
        // defaultRestrictions, networkAttributes, trafficSupport} and serviceLimits.
        // That reference omits networkSources even with includeNetworkSourceInfo=true,
        // and ArcGIS Pro's native reader is exact about the shape, so that flag is
        // accepted and deliberately changes nothing.
        var toolInfo = new JsonObject
        {
            ["isPortal"] = true,
            ["networkDataset"] = new JsonObject
            {
                ["attributeParameterValues"] = new JsonArray(),
                ["defaultCostAttribute"] = TimeAttributeName,
                ["defaultRestrictions"] = new JsonArray(),
                ["networkAttributes"] = BuildNetworkAttributes(),
                ["trafficSupport"] = "NONE",
            },
            ["serviceLimits"] = BuildServiceLimits(configuration, limitsFor),
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

    /// <summary>
    /// One travel mode document. GetTravelModes carries the 16-character <c>id</c>; the
    /// analysis layer resource carries the ordinal <c>itemId</c> instead, as real
    /// ArcGIS Server layers do.
    /// </summary>
    private static JsonObject BuildTravelMode(RoutingTravelProfile profile, NetworkDataset dataset, string? itemId = null)
        => new()
        {
            [itemId is null ? "id" : "itemId"] = itemId ?? TravelModeId(profile),
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
            ["networkSources"] = BuildNetworkSources(dataset),
        };

    /// <summary>The edge and junction sources of the dataset.</summary>
    private static JsonArray BuildNetworkSources(NetworkDataset dataset)
    {
        static JsonObject Source(int id, string name, string elementType, string sourceType)
            => new()
            {
                ["id"] = id,
                ["name"] = name,
                ["elementType"] = elementType,
                ["sourceType"] = sourceType,
            };

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

    /// <summary>
    /// The input network classes of an analysis layer in the shape ArcGIS Server
    /// publishes: each class lists its fields as fieldName / defaultValue /
    /// candidateFields (the names a client maps input columns from).
    /// </summary>
    private static JsonArray BuildNetworkClasses(string layerName)
    {
        static JsonObject NaField(string name, JsonNode? defaultValue = null, params string[] candidates)
            => new()
            {
                ["fieldName"] = name,
                ["defaultValue"] = defaultValue,
                ["candidateFields"] = candidates.Length == 0 ? null : new JsonArray([.. candidates.Select(c => (JsonNode)JsonValue.Create(c))]),
            };

        static JsonObject NaClass(string name, params JsonObject[] fields)
            => new() { ["className"] = name, ["fields"] = new JsonArray([.. fields]) };

        var nameCandidates = new[] { "Name", "Address", "Label", "Location", "Description", "Title" };
        JsonObject Points(string className, params JsonObject[] extra)
            => NaClass(className, [NaField("Shape"), NaField("Name", null, nameCandidates), NaField("CurbApproach", 0), .. extra]);

        var classes = new List<JsonObject>();
        switch (layerName)
        {
            case "Route":
                classes.Add(NaClass("Stops",
                    NaField("Shape"), NaField("Name", null, nameCandidates),
                    NaField("RouteName", null, "RouteName", "Route", "RouteID"),
                    NaField("Sequence", 1), NaField("TimeWindowStart"), NaField("TimeWindowEnd"),
                    NaField("CurbApproach", 0), NaField("LocationType", 0)));
                break;
            case "ServiceArea":
                classes.Add(Points("Facilities", NaField($"Breaks_{TimeAttributeName}")));
                break;
            case "ClosestFacility":
                classes.Add(Points("Incidents", NaField("TargetFacilityCount"), NaField($"Cutoff_{TimeAttributeName}")));
                classes.Add(Points("Facilities"));
                break;
            case "ODCostMatrix":
                classes.Add(Points("Origins", NaField("TargetDestinationCount"), NaField($"Cutoff_{TimeAttributeName}")));
                classes.Add(Points("Destinations"));
                break;
            case "LocationAllocation":
                classes.Add(Points("Facilities", NaField("FacilityType", 0), NaField("Weight", 1)));
                classes.Add(Points("DemandPoints", NaField("Weight", 1)));
                break;
        }

        classes.Add(NaClass("Barriers", NaField("Shape"), NaField("Name", null, nameCandidates), NaField("BarrierType", 0), NaField("CurbApproach", 0)));
        classes.Add(NaClass("PolylineBarriers", NaField("Shape"), NaField("Name", null, nameCandidates), NaField("BarrierType", 0), NaField($"Attr_{TimeAttributeName}", 1)));
        classes.Add(NaClass("PolygonBarriers", NaField("Shape"), NaField("Name", null, nameCandidates), NaField("BarrierType", 0), NaField($"Attr_{TimeAttributeName}", 1)));
        return new JsonArray([.. classes]);
    }

    /// <summary>
    /// The serviceLimits block of toolInfo, with the key names an ArcGIS Enterprise
    /// utility service publishes for the solver in question; a null value means the
    /// limit is not enforced, as on a real server.
    /// </summary>
    private static JsonObject BuildServiceLimits(RoutingConfiguration configuration, string layerName)
    {
        var limits = new JsonObject
        {
            ["forceHierarchyBeyondDistance"] = null,
            ["forceHierarchyBeyondDistanceUnits"] = "Miles",
            ["maximumFeaturesAffectedByLineBarriers"] = configuration.MaxBarriers,
            ["maximumFeaturesAffectedByPointBarriers"] = configuration.MaxBarriers,
            ["maximumFeaturesAffectedByPolygonBarriers"] = configuration.MaxBarriers,
        };
        switch (layerName)
        {
            case "Route":
                limits["maximumStops"] = configuration.MaxStops;
                limits["maximumStopsPerRoute"] = configuration.MaxStops;
                break;
            case "ServiceArea":
                limits["maximumFacilities"] = configuration.MaxFacilities;
                limits["maximumNumberOfBreaks"] = configuration.MaxBreaks;
                limits["maximumBreakTimeValue"] = null;
                limits["maximumBreakDistanceValue"] = null;
                break;
            case "ClosestFacility":
                limits["maximumFacilities"] = configuration.MaxFacilities;
                limits["maximumFacilitiesToFind"] = configuration.MaxClosestFacilities;
                limits["maximumIncidents"] = configuration.MaxIncidents;
                break;
            case "ODCostMatrix":
                limits["maximumOrigins"] = configuration.MaxOrigins;
                limits["maximumDestinations"] = configuration.MaxDestinations;
                break;
            case "LocationAllocation":
                limits["maximumFacilities"] = configuration.MaxFacilities;
                limits["maximumFacilitiesToFind"] = configuration.MaxFacilities;
                limits["maximumDemandPoints"] = configuration.MaxStops;
                break;
        }

        return limits;
    }

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
