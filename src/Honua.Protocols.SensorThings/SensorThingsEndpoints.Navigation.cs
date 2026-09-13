// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.SensorThings.Abstractions;
using Honua.Infrastructure.Models;
using Honua.Protocols.SensorThings.Services;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Protocols.SensorThings;

internal static partial class SensorThingsEndpoints
{
    private enum DatastreamRelation { Thing, Sensor, ObservedProperty }

    private static void ConfigureNavigation(RouteHandlerBuilder builder, string name) =>
        builder.WithDisplayName($"STA {name} Navigation")
            .WithName($"Sta{name}Navigation")
            .WithSummary("Get related SensorThings entities")
            .WithTags("SensorThings")
            .Produces(200, contentType: "application/json")
            .Produces(400)
            .Produces(404)
            .Produces(501);

    private static Task<IResult> HandleThingDatastreams(
        long id, HttpContext context, [FromServices] IObservationStore store,
        [FromServices] StaFilterTranslator filterTranslator) =>
        HandleRelatedDatastreams(id, DatastreamRelation.Thing, context, store, filterTranslator);

    private static Task<IResult> HandleSensorDatastreams(
        long id, HttpContext context, [FromServices] IObservationStore store,
        [FromServices] StaFilterTranslator filterTranslator) =>
        HandleRelatedDatastreams(id, DatastreamRelation.Sensor, context, store, filterTranslator);

    private static Task<IResult> HandleObservedPropertyDatastreams(
        long id, HttpContext context, [FromServices] IObservationStore store,
        [FromServices] StaFilterTranslator filterTranslator) =>
        HandleRelatedDatastreams(id, DatastreamRelation.ObservedProperty, context, store, filterTranslator);

    private static async Task<IResult> HandleRelatedDatastreams(
        long id, DatastreamRelation relation, HttpContext context,
        IObservationStore store, StaFilterTranslator filterTranslator)
    {
        if (!TryPlanCollection(context, StaEntitySchema.Datastreams, filterTranslator, out var plan, out var failure))
        {
            return failure;
        }

        var ct = context.RequestAborted;
        var exists = relation switch
        {
            DatastreamRelation.Thing => await store.GetThingAsync(id, ct).ConfigureAwait(false) is not null,
            DatastreamRelation.Sensor => await store.GetSensorAsync(id, ct).ConfigureAwait(false) is not null,
            DatastreamRelation.ObservedProperty => await store.GetObservedPropertyAsync(id, ct).ConfigureAwait(false) is not null,
            _ => false
        };
        if (!exists)
        {
            return StandardErrorHelpers.CreateNotFound(context, $"{relation}({id}) not found.");
        }

        // Carry the relationship in the canonical query; the provider owns its
        // SQL predicate and applies it before count/order/paging.
        var query = relation switch
        {
            DatastreamRelation.Thing => plan.CatalogQuery with { DatastreamThingId = id },
            DatastreamRelation.Sensor => plan.CatalogQuery with { DatastreamSensorId = id },
            DatastreamRelation.ObservedProperty => plan.CatalogQuery with { DatastreamObservedPropertyId = id },
            _ => throw new ArgumentOutOfRangeException(nameof(relation))
        };
        return await QueryDatastreamsAsync(context, store, plan, query).ConfigureAwait(false);
    }

    private static Task<IResult> HandleDatastreamThing(
        long id, HttpContext context, [FromServices] IObservationStore store,
        [FromServices] StaFilterTranslator filterTranslator) =>
        HandleDatastreamRelation(id, DatastreamRelation.Thing, context, store, filterTranslator);

    private static Task<IResult> HandleDatastreamSensor(
        long id, HttpContext context, [FromServices] IObservationStore store,
        [FromServices] StaFilterTranslator filterTranslator) =>
        HandleDatastreamRelation(id, DatastreamRelation.Sensor, context, store, filterTranslator);

    private static Task<IResult> HandleDatastreamObservedProperty(
        long id, HttpContext context, [FromServices] IObservationStore store,
        [FromServices] StaFilterTranslator filterTranslator) =>
        HandleDatastreamRelation(id, DatastreamRelation.ObservedProperty, context, store, filterTranslator);

    private static async Task<IResult> HandleDatastreamRelation(
        long id, DatastreamRelation relation, HttpContext context,
        IObservationStore store, StaFilterTranslator filterTranslator)
    {
        var schema = relation switch
        {
            DatastreamRelation.Thing => StaEntitySchema.Things,
            DatastreamRelation.Sensor => StaEntitySchema.Sensors,
            DatastreamRelation.ObservedProperty => StaEntitySchema.ObservedProperties,
            _ => throw new ArgumentOutOfRangeException(nameof(relation))
        };
        if (!TryPlanEntity(context, schema, filterTranslator, out var plan, out var failure))
        {
            return failure;
        }

        var datastream = await store.GetDatastreamAsync(id, context.RequestAborted).ConfigureAwait(false);
        if (datastream is null)
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Datastream({id}) not found.");
        }

        // Reuse the target's validated plan and result mapping without reparsing options.
        return relation switch
        {
            DatastreamRelation.Thing => await GetThingResultAsync(datastream.ThingId, context, store, plan).ConfigureAwait(false),
            DatastreamRelation.Sensor => await GetSensorResultAsync(datastream.SensorId, context, store, plan).ConfigureAwait(false),
            DatastreamRelation.ObservedProperty => await GetObservedPropertyResultAsync(datastream.ObservedPropertyId, context, store, plan).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(relation))
        };
    }

    private static async Task<IResult> HandleObservationDatastream(
        long id, HttpContext context, [FromServices] IObservationStore store,
        [FromServices] StaFilterTranslator filterTranslator)
    {
        if (!TryPlanEntity(context, StaEntitySchema.Datastreams, filterTranslator, out var plan, out var failure))
        {
            return failure;
        }

        var observation = await store.GetObservationAsync(id, context.RequestAborted).ConfigureAwait(false);
        return observation is null
            ? StandardErrorHelpers.CreateNotFound(context, $"Observation({id}) not found.")
            : await GetDatastreamResultAsync(observation.DatastreamId, context, store, plan).ConfigureAwait(false);
    }
}
