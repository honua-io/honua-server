// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.SensorThings.Abstractions;
using Honua.Core.Features.SensorThings.Domain;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Protocols.SensorThings.Models;
using Honua.Protocols.SensorThings.Services;
using Microsoft.AspNetCore.Mvc;

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
    public static IEndpointRouteBuilder MapSensorThingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        MapEntityCollection(endpoints, "Things", HandleListThings, HandleGetThing);
        MapEntityCollection(endpoints, "Sensors", HandleListSensors, HandleGetSensor);
        MapEntityCollection(endpoints, "ObservedProperties", HandleListObservedProperties, HandleGetObservedProperty);
        MapEntityCollection(endpoints, "Datastreams", HandleListDatastreams, HandleGetDatastream);
        MapEntityCollection(endpoints, "Observations", HandleListObservations, HandleGetObservation);

        endpoints.MapGet($"{BasePath}/Datastreams({{id:long}})/Observations", HandleDatastreamObservations)
            .WithDisplayName("STA Datastream Observations")
            .WithName("StaDatastreamObservations")
            .WithSummary("Get the Observations of a Datastream")
            .WithTags("SensorThings")
            .Produces<StaEntitySet<StaObservation>>(200, "application/json")
            .Produces(404);

        return endpoints;
    }

    private static void MapEntityCollection(
        IEndpointRouteBuilder endpoints,
        string entitySet,
        Delegate listHandler,
        Delegate byIdHandler)
    {
        endpoints.MapGet($"{BasePath}/{entitySet}", listHandler)
            .WithDisplayName($"STA {entitySet}")
            .WithName($"Sta{entitySet}")
            .WithSummary($"List {entitySet}")
            .WithTags("SensorThings")
            .Produces(200, contentType: "application/json")
            .Produces(400);

        endpoints.MapGet($"{BasePath}/{entitySet}({{id:long}})", byIdHandler)
            .WithDisplayName($"STA {entitySet} by id")
            .WithName($"Sta{entitySet}ById")
            .WithSummary($"Get a single {entitySet} entity by id")
            .WithTags("SensorThings")
            .Produces(200, contentType: "application/json")
            .Produces(404);
    }

    private static string StaBase(HttpContext context) => $"{BaseUrlResolver.GetBaseUrl(context)}{BasePath}";

    private static string? NextLink(HttpContext context, StaQueryOptions options, int returnedCount)
    {
        if (returnedCount < options.Top)
        {
            return null;
        }

        var staBase = BaseUrlResolver.GetBaseUrl(context);
        var path = context.Request.Path.Value ?? string.Empty;
        return $"{staBase}{path}?$top={options.Top}&$skip={options.Skip + options.Top}";
    }

    // ---- Things ----

    private static async Task<IResult> HandleListThings(HttpContext context, [FromServices] IObservationStore store)
    {
        var options = StaQueryOptions.FromRequest(context.Request);
        var ct = context.RequestAborted;
        var things = await store.ListThingsAsync(options.Skip, options.Top, ct).ConfigureAwait(false);
        var staBase = StaBase(context);
        var value = things.Select(t => StaEntityMapper.MapThing(t, staBase)).ToList();
        return Results.Json(
            new StaEntitySet<StaThing> { Value = value, NextLink = NextLink(context, options, value.Count) },
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
        var sensors = await store.ListSensorsAsync(options.Skip, options.Top, context.RequestAborted).ConfigureAwait(false);
        var staBase = StaBase(context);
        var value = sensors.Select(s => StaEntityMapper.MapSensor(s, staBase)).ToList();
        return Results.Json(
            new StaEntitySet<StaSensor> { Value = value, NextLink = NextLink(context, options, value.Count) },
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
        var properties = await store.ListObservedPropertiesAsync(options.Skip, options.Top, context.RequestAborted).ConfigureAwait(false);
        var staBase = StaBase(context);
        var value = properties.Select(p => StaEntityMapper.MapObservedProperty(p, staBase)).ToList();
        return Results.Json(
            new StaEntitySet<StaObservedProperty> { Value = value, NextLink = NextLink(context, options, value.Count) },
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
        var datastreams = await store.ListDatastreamsAsync(options.Skip, options.Top, ct).ConfigureAwait(false);
        var staBase = StaBase(context);

        var value = new List<StaDatastream>(datastreams.Count);
        foreach (var datastream in datastreams)
        {
            value.Add(StaEntityMapper.MapDatastream(
                datastream,
                staBase,
                await ExpandObservationsAsync(options, store, datastream.Id, staBase, ct).ConfigureAwait(false)));
        }

        return Results.Json(
            new StaEntitySet<StaDatastream> { Value = value, NextLink = NextLink(context, options, value.Count) },
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
            options.Top);

        var observations = await store.QueryObservationsAsync(query, context.RequestAborted).ConfigureAwait(false);
        var staBase = StaBase(context);
        var value = observations.Select(o => StaEntityMapper.MapObservation(o, staBase)).ToList();

        return Results.Json(
            new StaEntitySet<StaObservation>
            {
                Value = value,
                Count = options.Count ? value.Count : null,
                NextLink = NextLink(context, options, value.Count)
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
