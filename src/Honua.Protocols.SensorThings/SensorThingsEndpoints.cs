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
    private static readonly string[] _collectionOnlyQueryOptions = ["$filter", "$orderby", "$top", "$skip", "$count"];

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
            .Produces(400)
            .Produces(404)
            .Produces(501);

        ConfigureNavigation(endpoints.MapGet("/sta/v1.1/Things({id:long})/Datastreams", HandleThingDatastreams), "ThingsDatastreams");
        ConfigureNavigation(endpoints.MapGet("/sta/v1.1/Sensors({id:long})/Datastreams", HandleSensorDatastreams), "SensorsDatastreams");
        ConfigureNavigation(endpoints.MapGet("/sta/v1.1/ObservedProperties({id:long})/Datastreams", HandleObservedPropertyDatastreams), "ObservedPropertiesDatastreams");
        ConfigureNavigation(endpoints.MapGet("/sta/v1.1/Datastreams({id:long})/Thing", HandleDatastreamThing), "DatastreamsThing");
        ConfigureNavigation(endpoints.MapGet("/sta/v1.1/Datastreams({id:long})/Sensor", HandleDatastreamSensor), "DatastreamsSensor");
        ConfigureNavigation(endpoints.MapGet("/sta/v1.1/Datastreams({id:long})/ObservedProperty", HandleDatastreamObservedProperty), "DatastreamsObservedProperty");
        ConfigureNavigation(endpoints.MapGet("/sta/v1.1/Observations({id:long})/Datastream", HandleObservationDatastream), "ObservationsDatastream");

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

    // ---- Query-option plumbing ----

    /// <summary>
    /// Builds the plan for a collection request, or the error response the unsupported
    /// option earns.
    /// </summary>
    private static bool TryPlanCollection(
        HttpContext context,
        StaEntitySchema schema,
        StaFilterTranslator filterTranslator,
        out StaQueryPlan plan,
        out IResult failure)
    {
        var result = StaQueryPlan.Create(context.Request, schema, filterTranslator);
        if (!result.IsSuccess)
        {
            plan = null!;
            failure = PlanFailure(context, result);
            return false;
        }

        plan = result.Plan!;
        failure = null!;
        return true;
    }

    /// <summary>
    /// Builds the plan for a single-entity request. Collection filtering, ordering,
    /// paging and counting cannot apply to one entity, so naming those options is a
    /// client error rather than something to quietly drop.
    /// </summary>
    private static bool TryPlanEntity(
        HttpContext context,
        StaEntitySchema schema,
        StaFilterTranslator filterTranslator,
        out StaQueryPlan plan,
        out IResult failure)
    {
        if (!TryPlanCollection(context, schema, filterTranslator, out plan, out failure))
        {
            return false;
        }

        var rejected = _collectionOnlyQueryOptions.FirstOrDefault(context.Request.Query.ContainsKey);
        if (rejected is not null)
        {
            failure = StandardErrorHelpers.CreateBadRequest(
                context, $"{rejected} cannot be applied to a single {schema.EntitySet} entity.");
            return false;
        }

        return true;
    }

    private static IResult PlanFailure(HttpContext context, StaQueryPlanResult result) =>
        result.StatusCode == 501
            ? StandardErrorHelpers.CreateNotImplemented(context, result.Error!)
            : StandardErrorHelpers.CreateBadRequest(context, result.Error!);

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

    /// <summary>
    /// Serializes an entity-set envelope, applying the <c>$select</c> projection when the
    /// request asked for one. The projection runs over the source-generated JSON, so an
    /// unprojected response takes the same path it always did.
    /// </summary>
    private static IResult JsonEntitySet<T>(
        StaEntitySet<T> entitySet,
        JsonTypeInfo<StaEntitySet<T>> typeInfo,
        StaQueryPlan plan)
    {
        if (!plan.HasProjection)
        {
            return Results.Json(entitySet, typeInfo);
        }

        var node = JsonSerializer.SerializeToNode(entitySet, typeInfo)!.AsObject();
        return ProjectedJson(StaProjection.ProjectEntitySet(node, plan.ProjectedMembers));
    }

    private static IResult JsonEntity<T>(T entity, JsonTypeInfo<T> typeInfo, StaQueryPlan plan)
    {
        if (!plan.HasProjection)
        {
            return Results.Json(entity, typeInfo);
        }

        var node = JsonSerializer.SerializeToNode(entity, typeInfo)!.AsObject();
        return ProjectedJson(StaProjection.ProjectEntity(node, plan.ProjectedMembers));
    }

    // JsonNode writes itself; serializing the node with the source-generated context is
    // not possible (the projected shape has no DTO), and reflection-based serialization is
    // not available under AOT.
    private static IResult ProjectedJson(JsonObject node) =>
        Results.Text(node.ToJsonString(), "application/json", System.Text.Encoding.UTF8);

    // ---- Things ----

    private static async Task<IResult> HandleListThings(
        HttpContext context,
        [FromServices] IObservationStore store,
        [FromServices] StaFilterTranslator filterTranslator)
    {
        if (!TryPlanCollection(context, StaEntitySchema.Things, filterTranslator, out var plan, out var failure))
        {
            return failure;
        }

        var ct = context.RequestAborted;
        var things = await store.ListThingsAsync(plan.CatalogQuery, ct).ConfigureAwait(false);
        var staBase = StaBase(context);
        var value = things.Take(plan.Options.Top).Select(t => StaEntityMapper.MapThing(t, staBase)).ToList();
        return JsonEntitySet(
            new StaEntitySet<StaThing>
            {
                Value = value,
                Count = plan.Options.Count ? await store.CountThingsAsync(plan.CatalogQuery, ct).ConfigureAwait(false) : null,
                NextLink = NextLink(context, plan.Options, things.Count)
            },
            SensorThingsJsonContext.Default.StaEntitySetStaThing,
            plan);
    }

    private static async Task<IResult> HandleGetThing(
        long id,
        HttpContext context,
        [FromServices] IObservationStore store,
        [FromServices] StaFilterTranslator filterTranslator)
    {
        if (!TryPlanEntity(context, StaEntitySchema.Things, filterTranslator, out var plan, out var failure))
        {
            return failure;
        }

        return await GetThingResultAsync(id, context, store, plan).ConfigureAwait(false);
    }

    private static async Task<IResult> GetThingResultAsync(
        long id, HttpContext context, IObservationStore store, StaQueryPlan plan)
    {
        var thing = await store.GetThingAsync(id, context.RequestAborted).ConfigureAwait(false);
        return thing is null
            ? StandardErrorHelpers.CreateNotFound(context, $"Thing({id}) not found.")
            : JsonEntity(
                StaEntityMapper.MapThing(thing, StaBase(context)),
                SensorThingsJsonContext.Default.StaThing,
                plan);
    }

    // ---- Sensors ----

    private static async Task<IResult> HandleListSensors(
        HttpContext context,
        [FromServices] IObservationStore store,
        [FromServices] StaFilterTranslator filterTranslator)
    {
        if (!TryPlanCollection(context, StaEntitySchema.Sensors, filterTranslator, out var plan, out var failure))
        {
            return failure;
        }

        var ct = context.RequestAborted;
        var sensors = await store.ListSensorsAsync(plan.CatalogQuery, ct).ConfigureAwait(false);
        var staBase = StaBase(context);
        var value = sensors.Take(plan.Options.Top).Select(s => StaEntityMapper.MapSensor(s, staBase)).ToList();
        return JsonEntitySet(
            new StaEntitySet<StaSensor>
            {
                Value = value,
                Count = plan.Options.Count ? await store.CountSensorsAsync(plan.CatalogQuery, ct).ConfigureAwait(false) : null,
                NextLink = NextLink(context, plan.Options, sensors.Count)
            },
            SensorThingsJsonContext.Default.StaEntitySetStaSensor,
            plan);
    }

    private static async Task<IResult> HandleGetSensor(
        long id,
        HttpContext context,
        [FromServices] IObservationStore store,
        [FromServices] StaFilterTranslator filterTranslator)
    {
        if (!TryPlanEntity(context, StaEntitySchema.Sensors, filterTranslator, out var plan, out var failure))
        {
            return failure;
        }

        return await GetSensorResultAsync(id, context, store, plan).ConfigureAwait(false);
    }

    private static async Task<IResult> GetSensorResultAsync(
        long id, HttpContext context, IObservationStore store, StaQueryPlan plan)
    {
        var sensor = await store.GetSensorAsync(id, context.RequestAborted).ConfigureAwait(false);
        return sensor is null
            ? StandardErrorHelpers.CreateNotFound(context, $"Sensor({id}) not found.")
            : JsonEntity(
                StaEntityMapper.MapSensor(sensor, StaBase(context)),
                SensorThingsJsonContext.Default.StaSensor,
                plan);
    }

    // ---- ObservedProperties ----

    private static async Task<IResult> HandleListObservedProperties(
        HttpContext context,
        [FromServices] IObservationStore store,
        [FromServices] StaFilterTranslator filterTranslator)
    {
        if (!TryPlanCollection(context, StaEntitySchema.ObservedProperties, filterTranslator, out var plan, out var failure))
        {
            return failure;
        }

        var ct = context.RequestAborted;
        var properties = await store.ListObservedPropertiesAsync(plan.CatalogQuery, ct).ConfigureAwait(false);
        var staBase = StaBase(context);
        var value = properties.Take(plan.Options.Top).Select(p => StaEntityMapper.MapObservedProperty(p, staBase)).ToList();
        return JsonEntitySet(
            new StaEntitySet<StaObservedProperty>
            {
                Value = value,
                Count = plan.Options.Count ? await store.CountObservedPropertiesAsync(plan.CatalogQuery, ct).ConfigureAwait(false) : null,
                NextLink = NextLink(context, plan.Options, properties.Count)
            },
            SensorThingsJsonContext.Default.StaEntitySetStaObservedProperty,
            plan);
    }

    private static async Task<IResult> HandleGetObservedProperty(
        long id,
        HttpContext context,
        [FromServices] IObservationStore store,
        [FromServices] StaFilterTranslator filterTranslator)
    {
        if (!TryPlanEntity(context, StaEntitySchema.ObservedProperties, filterTranslator, out var plan, out var failure))
        {
            return failure;
        }

        return await GetObservedPropertyResultAsync(id, context, store, plan).ConfigureAwait(false);
    }

    private static async Task<IResult> GetObservedPropertyResultAsync(
        long id, HttpContext context, IObservationStore store, StaQueryPlan plan)
    {
        var property = await store.GetObservedPropertyAsync(id, context.RequestAborted).ConfigureAwait(false);
        return property is null
            ? StandardErrorHelpers.CreateNotFound(context, $"ObservedProperty({id}) not found.")
            : JsonEntity(
                StaEntityMapper.MapObservedProperty(property, StaBase(context)),
                SensorThingsJsonContext.Default.StaObservedProperty,
                plan);
    }

    // ---- Datastreams ----

    private static async Task<IResult> HandleListDatastreams(
        HttpContext context,
        [FromServices] IObservationStore store,
        [FromServices] StaFilterTranslator filterTranslator)
    {
        if (!TryPlanCollection(context, StaEntitySchema.Datastreams, filterTranslator, out var plan, out var failure))
        {
            return failure;
        }

        return await QueryDatastreamsAsync(context, store, plan, plan.CatalogQuery).ConfigureAwait(false);
    }

    private static async Task<IResult> QueryDatastreamsAsync(
        HttpContext context, IObservationStore store, StaQueryPlan plan, CatalogQuery query)
    {
        var ct = context.RequestAborted;
        var datastreams = await store.ListDatastreamsAsync(query, ct).ConfigureAwait(false);
        var staBase = StaBase(context);

        var expander = new StaDatastreamExpander(store, plan, staBase);
        var value = new List<StaDatastream>(datastreams.Count);
        foreach (var datastream in datastreams.Take(plan.Options.Top))
        {
            value.Add(await expander.MapAsync(datastream, ct).ConfigureAwait(false));
        }

        return JsonEntitySet(
            new StaEntitySet<StaDatastream>
            {
                Value = value,
                Count = plan.Options.Count ? await store.CountDatastreamsAsync(query, ct).ConfigureAwait(false) : null,
                NextLink = NextLink(context, plan.Options, datastreams.Count)
            },
            SensorThingsJsonContext.Default.StaEntitySetStaDatastream,
            plan);
    }

    private static async Task<IResult> HandleGetDatastream(
        long id,
        HttpContext context,
        [FromServices] IObservationStore store,
        [FromServices] StaFilterTranslator filterTranslator)
    {
        if (!TryPlanEntity(context, StaEntitySchema.Datastreams, filterTranslator, out var plan, out var failure))
        {
            return failure;
        }

        return await GetDatastreamResultAsync(id, context, store, plan).ConfigureAwait(false);
    }

    private static async Task<IResult> GetDatastreamResultAsync(
        long id, HttpContext context, IObservationStore store, StaQueryPlan plan)
    {
        var ct = context.RequestAborted;
        var datastream = await store.GetDatastreamAsync(id, ct).ConfigureAwait(false);
        if (datastream is null)
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Datastream({id}) not found.");
        }

        var staBase = StaBase(context);
        var expander = new StaDatastreamExpander(store, plan, staBase);
        return JsonEntity(
            await expander.MapAsync(datastream, ct).ConfigureAwait(false),
            SensorThingsJsonContext.Default.StaDatastream,
            plan);
    }

    // ---- Observations ----

    private static Task<IResult> HandleListObservations(
        HttpContext context,
        [FromServices] IObservationStore store,
        [FromServices] StaFilterTranslator filterTranslator) =>
        QueryObservationsAsync(context, store, filterTranslator, datastreamId: null);

    private static async Task<IResult> HandleDatastreamObservations(
        long id,
        HttpContext context,
        [FromServices] IObservationStore store,
        [FromServices] StaFilterTranslator filterTranslator)
    {
        if (!TryPlanCollection(context, StaEntitySchema.Observations, filterTranslator, out var plan, out var failure))
        {
            return failure;
        }

        var datastream = await store.GetDatastreamAsync(id, context.RequestAborted).ConfigureAwait(false);
        if (datastream is null)
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Datastream({id}) not found.");
        }

        return await QueryObservationsAsync(context, store, plan, datastreamId: id).ConfigureAwait(false);
    }

    private static async Task<IResult> QueryObservationsAsync(
        HttpContext context,
        IObservationStore store,
        StaFilterTranslator filterTranslator,
        long? datastreamId)
    {
        if (!TryPlanCollection(context, StaEntitySchema.Observations, filterTranslator, out var plan, out var failure))
        {
            return failure;
        }

        return await QueryObservationsAsync(context, store, plan, datastreamId).ConfigureAwait(false);
    }

    private static async Task<IResult> QueryObservationsAsync(
        HttpContext context, IObservationStore store, StaQueryPlan plan, long? datastreamId)
    {
        var query = plan.ObservationQuery(datastreamId);
        var ct = context.RequestAborted;
        var observations = await store.QueryObservationsAsync(query, ct).ConfigureAwait(false);
        var staBase = StaBase(context);
        var value = observations.Take(plan.Options.Top).Select(o => StaEntityMapper.MapObservation(o, staBase)).ToList();

        return JsonEntitySet(
            new StaEntitySet<StaObservation>
            {
                Value = value,
                Count = plan.Options.Count ? await store.CountObservationsAsync(query, ct).ConfigureAwait(false) : null,
                NextLink = NextLink(context, plan.Options, observations.Count)
            },
            SensorThingsJsonContext.Default.StaEntitySetStaObservation,
            plan);
    }

    private static async Task<IResult> HandleGetObservation(
        long id,
        HttpContext context,
        [FromServices] IObservationStore store,
        [FromServices] StaFilterTranslator filterTranslator)
    {
        if (!TryPlanEntity(context, StaEntitySchema.Observations, filterTranslator, out var plan, out var failure))
        {
            return failure;
        }

        var observation = await store.GetObservationAsync(id, context.RequestAborted).ConfigureAwait(false);
        return observation is null
            ? StandardErrorHelpers.CreateNotFound(context, $"Observation({id}) not found.")
            : JsonEntity(
                StaEntityMapper.MapObservation(observation, StaBase(context)),
                SensorThingsJsonContext.Default.StaObservation,
                plan);
    }

    /// <summary>
    /// Materialises the <c>$expand</c> items of a Datastream. Related catalog entities are
    /// memoised for the request: a page of datastreams usually shares a handful of Things,
    /// Sensors and ObservedProperties, and expanding is not a reason to issue one lookup
    /// per row per navigation.
    /// </summary>
    private sealed class StaDatastreamExpander(IObservationStore store, StaQueryPlan plan, string staBase)
    {
        private readonly Dictionary<long, StaThing?> _things = [];
        private readonly Dictionary<long, StaSensor?> _sensors = [];
        private readonly Dictionary<long, StaObservedProperty?> _observedProperties = [];

        public async Task<StaDatastream> MapAsync(SensorThingsDatastream datastream, CancellationToken ct)
        {
            return StaEntityMapper.MapDatastream(
                datastream,
                staBase,
                await ExpandObservationsAsync(datastream.Id, ct).ConfigureAwait(false),
                await ExpandThingAsync(datastream.ThingId, ct).ConfigureAwait(false),
                await ExpandSensorAsync(datastream.SensorId, ct).ConfigureAwait(false),
                await ExpandObservedPropertyAsync(datastream.ObservedPropertyId, ct).ConfigureAwait(false));
        }

        private async Task<IReadOnlyList<StaObservation>?> ExpandObservationsAsync(long datastreamId, CancellationToken ct)
        {
            if (plan.Expansion("Observations") is not { } expansion)
            {
                return null;
            }

            var observations = await store.QueryObservationsAsync(
                new ObservationQuery(
                    datastreamId,
                    expansion.WhereSql,
                    expansion.WhereParameters,
                    expansion.OrderBySql,
                    expansion.Skip,
                    expansion.Top),
                ct).ConfigureAwait(false);
            return observations.Select(o => StaEntityMapper.MapObservation(o, staBase)).ToList();
        }

        private async Task<StaThing?> ExpandThingAsync(long thingId, CancellationToken ct)
        {
            if (plan.Expansion("Thing") is null)
            {
                return null;
            }

            if (!_things.TryGetValue(thingId, out var thing))
            {
                var entity = await store.GetThingAsync(thingId, ct).ConfigureAwait(false);
                thing = entity is null ? null : StaEntityMapper.MapThing(entity, staBase);
                _things[thingId] = thing;
            }

            return thing;
        }

        private async Task<StaSensor?> ExpandSensorAsync(long sensorId, CancellationToken ct)
        {
            if (plan.Expansion("Sensor") is null)
            {
                return null;
            }

            if (!_sensors.TryGetValue(sensorId, out var sensor))
            {
                var entity = await store.GetSensorAsync(sensorId, ct).ConfigureAwait(false);
                sensor = entity is null ? null : StaEntityMapper.MapSensor(entity, staBase);
                _sensors[sensorId] = sensor;
            }

            return sensor;
        }

        private async Task<StaObservedProperty?> ExpandObservedPropertyAsync(long observedPropertyId, CancellationToken ct)
        {
            if (plan.Expansion("ObservedProperty") is null)
            {
                return null;
            }

            if (!_observedProperties.TryGetValue(observedPropertyId, out var property))
            {
                var entity = await store.GetObservedPropertyAsync(observedPropertyId, ct).ConfigureAwait(false);
                property = entity is null ? null : StaEntityMapper.MapObservedProperty(entity, staBase);
                _observedProperties[observedPropertyId] = property;
            }

            return property;
        }
    }
}
