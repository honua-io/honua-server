// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.SensorThings.Abstractions;
using Honua.Core.Features.SensorThings.Domain;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Protocols.SensorThings.Models;
using Honua.Protocols.SensorThings.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace Honua.Protocols.SensorThings;

/// <summary>
/// Maps OGC SensorThings API (STA v1.1) read endpoints under <c>/sta/v1.1</c>.
/// The query surface reuses the shared OData <c>$filter</c> parser; entity
/// envelopes are STA-conformance-shaped (<c>@iot.id</c>/<c>@iot.selfLink</c>/
/// <c>@iot.navigationLink</c>, <c>value</c> arrays, <c>@iot.nextLink</c> paging).
/// </summary>
/// <remarks>
/// Every request's system query options are turned into a <see cref="StaQueryPlan"/>
/// before the store is touched. An option the plan cannot honour fails the request —
/// 400 when it is malformed or names an unknown property, 501 when it is recognised but
/// unimplemented — rather than being dropped (STA 1.1 Req 28-35, OData 4.0 §8.2.1).
/// </remarks>
internal static partial class SensorThingsEndpoints
{
    internal const string BasePath = "/sta/v1.1";

    /// <summary>Logging category marker for SensorThings endpoints.</summary>
    internal sealed class SensorThingsEndpointsLog
    {
    }

    /// <summary>Maps all SensorThings API read endpoints.</summary>
    /// <remarks>
    /// Routes are declared with literal path strings (not interpolated over
    /// the <c>BasePath</c>/entity-set variables) so the source-scan governance
    /// in <c>EndpointRegistryDriftTests</c> can anchor every
    /// <c>EndpointRegistry</c> entry to a concrete <c>MapGet</c> call.
    /// </remarks>
    public static IEndpointRouteBuilder MapSensorThingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/sta/v1.1", HandleServiceRoot)
            .WithDisplayName("STA Service Document")
            .WithName("StaServiceDocument")
            .WithSummary("Discover the available SensorThings entity sets")
            .WithTags("SensorThings")
            .Produces<StaServiceDocument>(200, "application/json");

        // Each route is a literal first argument to MapGet so the source-scan
        // governance can anchor every EndpointRegistry entry to a concrete
        // MapGet call; the typed {id:long} constraint normalises to {id}.
        ConfigureCollection(endpoints.MapGet("/sta/v1.1/Things", HandleCoreRead), "Things");
        ConfigureById(endpoints.MapGet("/sta/v1.1/Things({id:long})", HandleCoreRead), "Things");
        ConfigureCollection(endpoints.MapGet("/sta/v1.1/Sensors", HandleCoreRead), "Sensors");
        ConfigureById(endpoints.MapGet("/sta/v1.1/Sensors({id:long})", HandleCoreRead), "Sensors");
        ConfigureCollection(endpoints.MapGet("/sta/v1.1/ObservedProperties", HandleCoreRead), "ObservedProperties");
        ConfigureById(endpoints.MapGet("/sta/v1.1/ObservedProperties({id:long})", HandleCoreRead), "ObservedProperties");
        ConfigureCollection(endpoints.MapGet("/sta/v1.1/Datastreams", HandleCoreRead), "Datastreams");
        ConfigureById(endpoints.MapGet("/sta/v1.1/Datastreams({id:long})", HandleCoreRead), "Datastreams");
        ConfigureCollection(endpoints.MapGet("/sta/v1.1/Observations", HandleCoreRead), "Observations");
        ConfigureById(endpoints.MapGet("/sta/v1.1/Observations({id:long})", HandleCoreRead), "Observations");

        endpoints.MapGet("/sta/v1.1/Datastreams({id:long})/Observations", HandleCoreRead)
            .WithDisplayName("STA Datastream Observations")
            .WithName("StaDatastreamObservations")
            .WithSummary("Get the Observations of a Datastream")
            .WithTags("SensorThings")
            .Produces<StaEntitySet<StaObservation>>(200, "application/json")
            .Produces(400)
            .Produces(404)
            .Produces(501);

        ConfigureNavigation(endpoints.MapGet("/sta/v1.1/Things({id:long})/Datastreams", HandleCoreRead), "ThingsDatastreams");
        ConfigureNavigation(endpoints.MapGet("/sta/v1.1/Sensors({id:long})/Datastreams", HandleCoreRead), "SensorsDatastreams");
        ConfigureNavigation(endpoints.MapGet("/sta/v1.1/ObservedProperties({id:long})/Datastreams", HandleCoreRead), "ObservedPropertiesDatastreams");
        ConfigureNavigation(endpoints.MapGet("/sta/v1.1/Datastreams({id:long})/Thing", HandleCoreRead), "DatastreamsThing");
        ConfigureNavigation(endpoints.MapGet("/sta/v1.1/Datastreams({id:long})/Sensor", HandleCoreRead), "DatastreamsSensor");
        ConfigureNavigation(endpoints.MapGet("/sta/v1.1/Datastreams({id:long})/ObservedProperty", HandleCoreRead), "DatastreamsObservedProperty");
        ConfigureNavigation(endpoints.MapGet("/sta/v1.1/Observations({id:long})/Datastream", HandleCoreRead), "ObservationsDatastream");

        // Phase 2 ingest (REST/bulk observation creation + datastream creation) and
        // Phase 3 real-time streaming (SSE/WebSocket) are mapped from their partial-class
        // files so each route stays a literal MapPost/MapGet the source-scan governance can
        // anchor to an EndpointRegistry entry.
        MapCoreEntityEndpoints(endpoints);
        Streaming.ObservationStreamEndpoints.MapSensorThingsStreamEndpoints(endpoints);

        return endpoints;
    }

    private static void ConfigureCollection(RouteHandlerBuilder builder, string entitySet) =>
        builder
            .WithDisplayName($"STA {entitySet}")
            .WithName($"Sta{entitySet}")
            .WithSummary($"List {entitySet}")
            .WithTags("SensorThings")
            .Produces(200, contentType: "application/json")
            .Produces(400)
            .Produces(501);

    private static void ConfigureById(RouteHandlerBuilder builder, string entitySet) =>
        builder
            .WithDisplayName($"STA {entitySet} by id")
            .WithName($"Sta{entitySet}ById")
            .WithSummary($"Get a single {entitySet} entity by id")
            .WithTags("SensorThings")
            .Produces(200, contentType: "application/json")
            .Produces(400)
            .Produces(404)
            .Produces(501);

    private static string StaBase(HttpContext context) => $"{BaseUrlResolver.GetBaseUrl(context)}{BasePath}";

    private static async Task<IResult> HandleServiceRoot(HttpContext context, ISensorThingsEntityStore store)
    {
        var staBase = StaBase(context);
        var unresolved = await store.CountUnresolvedFeaturesAsync(context.RequestAborted).ConfigureAwait(false);
        if (context.RequestServices.GetRequiredService<IConfiguration>().GetValue<bool>("SensorThings:ConformantProfile") && unresolved > 0)
            return StandardErrorHelpers.CreateServiceUnavailable(context, "The conformant sensing profile requires explicit reconciliation of legacy FeatureOfInterest associations.");
        // This preview adapter implements partial requirement classes. Do not
        // advertise complete conformance classes or unimplemented entity sets.
        var document = new StaServiceDocument
        {
            Value =
            [
                new() { Name = "Things", Url = $"{staBase}/Things" },
                new() { Name = "Sensors", Url = $"{staBase}/Sensors" },
                new() { Name = "ObservedProperties", Url = $"{staBase}/ObservedProperties" },
                new() { Name = "Datastreams", Url = $"{staBase}/Datastreams" },
                new() { Name = "Observations", Url = $"{staBase}/Observations" },
                new() { Name = "Locations", Url = $"{staBase}/Locations" },
                new() { Name = "HistoricalLocations", Url = $"{staBase}/HistoricalLocations" },
                new() { Name = "FeaturesOfInterest", Url = $"{staBase}/FeaturesOfInterest" }
            ],
            ServerSettings = new StaServerSettings { UnresolvedFeatureOfInterestCount = unresolved }
        };
        return Results.Json(document, SensorThingsJsonContext.Default.StaServiceDocument);
    }

}
