// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
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
internal static class SensorThingsEndpoints
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
        ConfigureCollection(endpoints.MapGet("/sta/v1.1/Things", HandleListThings), "Things");
        ConfigureById(endpoints.MapGet("/sta/v1.1/Things({id:long})", HandleGetThing), "Things");
        ConfigureCollection(endpoints.MapGet("/sta/v1.1/Sensors", HandleListSensors), "Sensors");
        ConfigureById(endpoints.MapGet("/sta/v1.1/Sensors({id:long})", HandleGetSensor), "Sensors");
        ConfigureCollection(endpoints.MapGet("/sta/v1.1/ObservedProperties", HandleListObservedProperties), "ObservedProperties");
        ConfigureById(endpoints.MapGet("/sta/v1.1/ObservedProperties({id:long})", HandleGetObservedProperty), "ObservedProperties");
        ConfigureCollection(endpoints.MapGet("/sta/v1.1/Datastreams", HandleListDatastreams), "Datastreams");
        ConfigureById(endpoints.MapGet("/sta/v1.1/Datastreams({id:long})", HandleGetDatastream), "Datastreams");
        ConfigureCollection(endpoints.MapGet("/sta/v1.1/Observations", HandleListObservations), "Observations");
        ConfigureById(endpoints.MapGet("/sta/v1.1/Observations({id:long})", HandleGetObservation), "Observations");

        endpoints.MapGet("/sta/v1.1/Datastreams({id:long})/Observations", HandleDatastreamObservations)
            .WithDisplayName("STA Datastream Observations")
            .WithName("StaDatastreamObservations")
            .WithSummary("Get the Observations of a Datastream")
            .WithTags("SensorThings")
            .Produces<StaEntitySet<StaObservation>>(200, "application/json")
            .Produces(404);

        // Phase 2 ingest (REST/bulk observation creation + datastream creation) and
        // Phase 3 real-time streaming (SSE/WebSocket) are mapped from their partial-class
        // files so each route stays a literal MapPost/MapGet the source-scan governance can
        // anchor to an EndpointRegistry entry.
        endpoints.MapSensorThingsIngestEndpoints();
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
            .Produces(400);

    private static void ConfigureById(RouteHandlerBuilder builder, string entitySet) =>
        builder
            .WithDisplayName($"STA {entitySet} by id")
            .WithName($"Sta{entitySet}ById")
            .WithSummary($"Get a single {entitySet} entity by id")
            .WithTags("SensorThings")
            .Produces(200, contentType: "application/json")
            .Produces(404);

    private static string StaBase(HttpContext context) => $"{BaseUrlResolver.GetBaseUrl(context)}{BasePath}";

    private static IResult HandleServiceRoot(HttpContext context)
    {
        var staBase = StaBase(context);
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
                new() { Name = "Observations", Url = $"{staBase}/Observations" }
            ],
            ServerSettings = new StaServerSettings()
        };
        return Results.Json(document, SensorThingsJsonContext.Default.StaServiceDocument);
    }

    private static string? NextLink(HttpContext context, StaQueryOptions options, int returnedCount)
    {
        if (options.Top == 0 || returnedCount <= options.Top)
        {
            return null;
        }

        // Never advertise a continuation the server cannot follow: $skip binds to the
        // store's 32-bit offset, so a link past int.MaxValue would fail to parse on the
        // way back in and restart pagination at the first page.
        if (options.NextSkip is not { } nextSkip)
        {
            return null;
        }

        var staBase = BaseUrlResolver.GetBaseUrl(context);
        var path = context.Request.Path.Value ?? string.Empty;
        // Preserve parsed system options only; arbitrary query parameters may contain credentials.
        var query = new Dictionary<string, string?>
        {
            ["$top"] = options.Top.ToString(CultureInfo.InvariantCulture),
            ["$skip"] = nextSkip.ToString(CultureInfo.InvariantCulture),
            ["$filter"] = options.Filter,
            ["$orderby"] = options.OrderBy,
            ["$select"] = options.Select,
            ["$expand"] = options.Expand,
            ["$count"] = options.Count ? "true" : null
        };
        return QueryHelpers.AddQueryString($"{staBase}{path}", query);
    }

    // ---- Things ----

    private static async Task<IResult> HandleListThings(HttpContext context, [FromServices] IObservationStore store)
    {
        var options = StaQueryOptions.FromRequest(context.Request);
        var ct = context.RequestAborted;
        var things = await store.ListThingsAsync(options.Skip, options.FetchTop, ct).ConfigureAwait(false);
        var staBase = StaBase(context);
        var value = things.Take(options.Top).Select(t => StaEntityMapper.MapThing(t, staBase)).ToList();
        return Results.Json(
            new StaEntitySet<StaThing>
            {
                Value = value,
                Count = options.Count ? await store.CountThingsAsync(ct).ConfigureAwait(false) : null,
                NextLink = NextLink(context, options, things.Count)
            },
            SensorThingsJsonContext.Default.StaEntitySetStaThing);
    }

    private static async Task<IResult> HandleGetThing(long id, HttpContext context, [FromServices] IObservationStore store)
    {
        var thing = await store.GetThingAsync(id, context.RequestAborted).ConfigureAwait(false);
        return thing is null
            ? StandardErrorHelpers.CreateNotFound(context, $"Thing({id}) not found.")
            : Results.Json(StaEntityMapper.MapThing(thing, StaBase(context)), SensorThingsJsonContext.Default.StaThing);
    }

    // ---- Sensors ----

    private static async Task<IResult> HandleListSensors(HttpContext context, [FromServices] IObservationStore store)
    {
        var options = StaQueryOptions.FromRequest(context.Request);
        var sensors = await store.ListSensorsAsync(options.Skip, options.FetchTop, context.RequestAborted).ConfigureAwait(false);
        var staBase = StaBase(context);
        var value = sensors.Take(options.Top).Select(s => StaEntityMapper.MapSensor(s, staBase)).ToList();
        return Results.Json(
            new StaEntitySet<StaSensor>
            {
                Value = value,
                Count = options.Count ? await store.CountSensorsAsync(context.RequestAborted).ConfigureAwait(false) : null,
                NextLink = NextLink(context, options, sensors.Count)
            },
            SensorThingsJsonContext.Default.StaEntitySetStaSensor);
    }

    private static async Task<IResult> HandleGetSensor(long id, HttpContext context, [FromServices] IObservationStore store)
    {
        var sensor = await store.GetSensorAsync(id, context.RequestAborted).ConfigureAwait(false);
        return sensor is null
            ? StandardErrorHelpers.CreateNotFound(context, $"Sensor({id}) not found.")
            : Results.Json(StaEntityMapper.MapSensor(sensor, StaBase(context)), SensorThingsJsonContext.Default.StaSensor);
    }

    // ---- ObservedProperties ----

    private static async Task<IResult> HandleListObservedProperties(HttpContext context, [FromServices] IObservationStore store)
    {
        var options = StaQueryOptions.FromRequest(context.Request);
        var properties = await store.ListObservedPropertiesAsync(options.Skip, options.FetchTop, context.RequestAborted).ConfigureAwait(false);
        var staBase = StaBase(context);
        var value = properties.Take(options.Top).Select(p => StaEntityMapper.MapObservedProperty(p, staBase)).ToList();
        return Results.Json(
            new StaEntitySet<StaObservedProperty>
            {
                Value = value,
                Count = options.Count ? await store.CountObservedPropertiesAsync(context.RequestAborted).ConfigureAwait(false) : null,
                NextLink = NextLink(context, options, properties.Count)
            },
            SensorThingsJsonContext.Default.StaEntitySetStaObservedProperty);
    }

    private static async Task<IResult> HandleGetObservedProperty(long id, HttpContext context, [FromServices] IObservationStore store)
    {
        var property = await store.GetObservedPropertyAsync(id, context.RequestAborted).ConfigureAwait(false);
        return property is null
            ? StandardErrorHelpers.CreateNotFound(context, $"ObservedProperty({id}) not found.")
            : Results.Json(StaEntityMapper.MapObservedProperty(property, StaBase(context)), SensorThingsJsonContext.Default.StaObservedProperty);
    }

    // ---- Datastreams ----

    private static async Task<IResult> HandleListDatastreams(HttpContext context, [FromServices] IObservationStore store)
    {
        var options = StaQueryOptions.FromRequest(context.Request);
        var ct = context.RequestAborted;
        var datastreams = await store.ListDatastreamsAsync(options.Skip, options.FetchTop, ct).ConfigureAwait(false);
        var staBase = StaBase(context);

        var value = new List<StaDatastream>(datastreams.Count);
        foreach (var datastream in datastreams.Take(options.Top))
        {
            value.Add(StaEntityMapper.MapDatastream(
                datastream,
                staBase,
                await ExpandObservationsAsync(options, store, datastream.Id, staBase, ct).ConfigureAwait(false)));
        }

        return Results.Json(
            new StaEntitySet<StaDatastream>
            {
                Value = value,
                Count = options.Count ? await store.CountDatastreamsAsync(ct).ConfigureAwait(false) : null,
                NextLink = NextLink(context, options, datastreams.Count)
            },
            SensorThingsJsonContext.Default.StaEntitySetStaDatastream);
    }

    private static async Task<IResult> HandleGetDatastream(long id, HttpContext context, [FromServices] IObservationStore store)
    {
        var options = StaQueryOptions.FromRequest(context.Request);
        var ct = context.RequestAborted;
        var datastream = await store.GetDatastreamAsync(id, ct).ConfigureAwait(false);
        if (datastream is null)
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Datastream({id}) not found.");
        }

        var staBase = StaBase(context);
        var expanded = await ExpandObservationsAsync(options, store, id, staBase, ct).ConfigureAwait(false);
        return Results.Json(
            StaEntityMapper.MapDatastream(datastream, staBase, expanded),
            SensorThingsJsonContext.Default.StaDatastream);
    }

    private static async Task<IReadOnlyList<StaObservation>?> ExpandObservationsAsync(
        StaQueryOptions options,
        IObservationStore store,
        long datastreamId,
        string staBase,
        CancellationToken ct)
    {
        if (!options.ExpandsTo("Observations"))
        {
            return null;
        }

        var observations = await store.QueryObservationsAsync(
            new ObservationQuery(datastreamId, null, Array.Empty<object?>(), OrderByDescending: true, Skip: 0, Top: StaQueryOptions.DefaultTop),
            ct).ConfigureAwait(false);
        return observations.Select(o => StaEntityMapper.MapObservation(o, staBase)).ToList();
    }

    // ---- Observations ----

    private static async Task<IResult> HandleListObservations(
        HttpContext context,
        [FromServices] IObservationStore store,
        [FromServices] StaObservationFilterTranslator filterTranslator)
    {
        return await QueryObservationsAsync(context, store, filterTranslator, datastreamId: null).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleDatastreamObservations(
        long id,
        HttpContext context,
        [FromServices] IObservationStore store,
        [FromServices] StaObservationFilterTranslator filterTranslator)
    {
        var datastream = await store.GetDatastreamAsync(id, context.RequestAborted).ConfigureAwait(false);
        if (datastream is null)
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Datastream({id}) not found.");
        }

        return await QueryObservationsAsync(context, store, filterTranslator, datastreamId: id).ConfigureAwait(false);
    }

    private static async Task<IResult> QueryObservationsAsync(
        HttpContext context,
        IObservationStore store,
        StaObservationFilterTranslator filterTranslator,
        long? datastreamId)
    {
        var options = StaQueryOptions.FromRequest(context.Request);

        var translation = filterTranslator.Translate(options.Filter);
        if (!translation.IsSuccess)
        {
            return StandardErrorHelpers.CreateBadRequest(context, translation.Error ?? "Invalid $filter.");
        }

        var query = new ObservationQuery(
            datastreamId,
            translation.Sql,
            translation.Parameters,
            options.OrderByDescending,
            options.Skip,
            options.FetchTop);

        var observations = await store.QueryObservationsAsync(query, context.RequestAborted).ConfigureAwait(false);
        var staBase = StaBase(context);
        var value = observations.Take(options.Top).Select(o => StaEntityMapper.MapObservation(o, staBase)).ToList();

        return Results.Json(
            new StaEntitySet<StaObservation>
            {
                Value = value,
                Count = options.Count ? await store.CountObservationsAsync(query, context.RequestAborted).ConfigureAwait(false) : null,
                NextLink = NextLink(context, options, observations.Count)
            },
            SensorThingsJsonContext.Default.StaEntitySetStaObservation);
    }

    private static async Task<IResult> HandleGetObservation(long id, HttpContext context, [FromServices] IObservationStore store)
    {
        var observation = await store.GetObservationAsync(id, context.RequestAborted).ConfigureAwait(false);
        return observation is null
            ? StandardErrorHelpers.CreateNotFound(context, $"Observation({id}) not found.")
            : Results.Json(StaEntityMapper.MapObservation(observation, StaBase(context)), SensorThingsJsonContext.Default.StaObservation);
    }
}
