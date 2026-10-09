// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.SensorThings.Abstractions;
using Honua.Core.Features.SensorThings.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Protocols.SensorThings.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Honua.ServiceDefaults;

namespace Honua.Protocols.SensorThings;

internal static partial class SensorThingsEndpoints
{
    private sealed record Resource(string Set, long? Id, string? ParentSet = null, long? ParentId = null, string? Navigation = null, string? Property = null, bool Raw = false, bool Reference = false);

    private static void MapCoreEntityEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapSensorThingsIngestEndpoints();
        ConfigureCoreWrite(endpoints.MapPost("/sta/v1.1/Things", HandleCoreWrite), endpoints);
        ConfigureCoreWrite(endpoints.MapPost("/sta/v1.1/Locations", HandleCoreWrite), endpoints);
        ConfigureCoreWrite(endpoints.MapPost("/sta/v1.1/HistoricalLocations", HandleCoreWrite), endpoints);
        ConfigureCoreWrite(endpoints.MapPost("/sta/v1.1/Sensors", HandleCoreWrite), endpoints);
        ConfigureCoreWrite(endpoints.MapPost("/sta/v1.1/ObservedProperties", HandleCoreWrite), endpoints);
        ConfigureCoreWrite(endpoints.MapPost("/sta/v1.1/FeaturesOfInterest", HandleCoreWrite), endpoints);
        ConfigureCoreWrite(endpoints.MapPatch("/sta/v1.1/Things({id:long})", HandleCoreWrite), endpoints);
        ConfigureCoreWrite(endpoints.MapDelete("/sta/v1.1/Things({id:long})", HandleCoreWrite), endpoints);
        ConfigureCoreWrite(endpoints.MapPatch("/sta/v1.1/Locations({id:long})", HandleCoreWrite), endpoints);
        ConfigureCoreWrite(endpoints.MapDelete("/sta/v1.1/Locations({id:long})", HandleCoreWrite), endpoints);
        ConfigureCoreWrite(endpoints.MapPatch("/sta/v1.1/HistoricalLocations({id:long})", HandleCoreWrite), endpoints);
        ConfigureCoreWrite(endpoints.MapDelete("/sta/v1.1/HistoricalLocations({id:long})", HandleCoreWrite), endpoints);
        ConfigureCoreWrite(endpoints.MapPatch("/sta/v1.1/Datastreams({id:long})", HandleCoreWrite), endpoints);
        ConfigureCoreWrite(endpoints.MapDelete("/sta/v1.1/Datastreams({id:long})", HandleCoreWrite), endpoints);
        ConfigureCoreWrite(endpoints.MapPatch("/sta/v1.1/Sensors({id:long})", HandleCoreWrite), endpoints);
        ConfigureCoreWrite(endpoints.MapDelete("/sta/v1.1/Sensors({id:long})", HandleCoreWrite), endpoints);
        ConfigureCoreWrite(endpoints.MapPatch("/sta/v1.1/ObservedProperties({id:long})", HandleCoreWrite), endpoints);
        ConfigureCoreWrite(endpoints.MapDelete("/sta/v1.1/ObservedProperties({id:long})", HandleCoreWrite), endpoints);
        ConfigureCoreWrite(endpoints.MapPatch("/sta/v1.1/Observations({id:long})", HandleCoreWrite), endpoints);
        ConfigureCoreWrite(endpoints.MapDelete("/sta/v1.1/Observations({id:long})", HandleCoreWrite), endpoints);
        ConfigureCoreWrite(endpoints.MapPatch("/sta/v1.1/FeaturesOfInterest({id:long})", HandleCoreWrite), endpoints);
        ConfigureCoreWrite(endpoints.MapDelete("/sta/v1.1/FeaturesOfInterest({id:long})", HandleCoreWrite), endpoints);
        ConfigureCollection(endpoints.MapGet("/sta/v1.1/Locations", HandleCoreRead), "Locations");
        ConfigureById(endpoints.MapGet("/sta/v1.1/Locations({id:long})", HandleCoreRead), "Locations");
        ConfigureCollection(endpoints.MapGet("/sta/v1.1/HistoricalLocations", HandleCoreRead), "HistoricalLocations");
        ConfigureById(endpoints.MapGet("/sta/v1.1/HistoricalLocations({id:long})", HandleCoreRead), "HistoricalLocations");
        ConfigureCollection(endpoints.MapGet("/sta/v1.1/FeaturesOfInterest", HandleCoreRead), "FeaturesOfInterest");
        ConfigureById(endpoints.MapGet("/sta/v1.1/FeaturesOfInterest({id:long})", HandleCoreRead), "FeaturesOfInterest");
        ConfigureNavigation(endpoints.MapGet("/sta/v1.1/Things({id:long})/Locations", HandleCoreRead), "ThingsLocations");
        ConfigureNavigation(endpoints.MapGet("/sta/v1.1/Things({id:long})/HistoricalLocations", HandleCoreRead), "ThingsHistoricalLocations");
        ConfigureNavigation(endpoints.MapGet("/sta/v1.1/Locations({id:long})/Things", HandleCoreRead), "LocationsThings");
        ConfigureNavigation(endpoints.MapGet("/sta/v1.1/Locations({id:long})/HistoricalLocations", HandleCoreRead), "LocationsHistoricalLocations");
        ConfigureNavigation(endpoints.MapGet("/sta/v1.1/HistoricalLocations({id:long})/Thing", HandleCoreRead), "HistoricalLocationsThing");
        ConfigureNavigation(endpoints.MapGet("/sta/v1.1/HistoricalLocations({id:long})/Locations", HandleCoreRead), "HistoricalLocationsLocations");
        ConfigureNavigation(endpoints.MapGet("/sta/v1.1/Observations({id:long})/FeatureOfInterest", HandleCoreRead), "ObservationsFeatureOfInterest");
        ConfigureNavigation(endpoints.MapGet("/sta/v1.1/FeaturesOfInterest({id:long})/Observations", HandleCoreRead), "FeaturesOfInterestObservations");
        endpoints.MapGet("/sta/v1.1/{**resourcePath}", HandleCoreRead).WithTags("SensorThings");
        var writes = endpoints.MapMethods("/sta/v1.1/{**resourcePath}", ["POST", "PATCH", "DELETE", "PUT"], HandleCoreWrite).WithTags("SensorThings");
        if (endpoints.ServiceProvider.GetRequiredService<IConfiguration>().GetValue<bool>(SensorThingsOptions.AllowAnonymousWritesDangerouslyPath)) writes.AllowAnonymous();
        else writes.RequireAdminAuthorization();
    }

    private static void ConfigureCoreWrite(RouteHandlerBuilder route, IEndpointRouteBuilder endpoints)
    {
        route.WithTags("SensorThings").Produces(201).Produces(204).Produces(400).Produces(404);
        if (endpoints.ServiceProvider.GetRequiredService<IConfiguration>().GetValue<bool>(SensorThingsOptions.AllowAnonymousWritesDangerouslyPath)) route.AllowAnonymous();
        else route.RequireAdminAuthorization();
    }

    private static async Task<Resource> ResolveResourceAsync(HttpContext context, ISensorThingsEntityStore store)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var segments = path[BasePath.Length..].Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) throw new SensorThingsValidationException("An entity resource path is required.");
        var (set, id) = ParseEntitySegment(segments[0]);
        if (!SensorThingsRelationships.EntitySets.Contains(set)) throw new SensorThingsValidationException("Unknown sensing entity set.");
        var resource = new Resource(set, id);
        for (var index = 1; index < segments.Length; index++)
        {
            var segment = segments[index];
            if (segment is "$value" or "$ref")
            {
                if (index != segments.Length - 1) throw new SensorThingsValidationException("$value and $ref must terminate a resource path.");
                if (segment == "$value" && resource.Property is null) throw new SensorThingsValidationException("$value requires an entity property path.");
                if (segment == "$ref" && resource.Property is not null) throw new SensorThingsValidationException("$ref requires an entity or navigation resource.");
                resource = resource with { Raw = segment == "$value", Reference = segment == "$ref" }; continue;
            }
            if (resource.Id is null) throw new SensorThingsValidationException("A navigation or property path requires an entity identifier.");
            if (resource.Property is { } propertyPath)
            {
                resource = resource with { Property = propertyPath + "/" + segment };
                continue;
            }
            var (navigation, relatedId) = ParseEntitySegment(segment);
            if (!SensorThingsRelationships.For(resource.Set).TryGetValue(navigation, out var relationship))
            {
                resource = resource with { Property = segment }; continue;
            }
            if (!relationship.Many && relatedId is not null) throw new SensorThingsValidationException("A singleton navigation property cannot carry an entity identifier.");
            if (await store.GetEntityAsync(resource.Set, resource.Id.Value, context.RequestAborted).ConfigureAwait(false) is null) throw new ResourceNotFoundException();
            var parentSet = resource.Set; var parentId = resource.Id;
            resource = new Resource(relationship.Target, relatedId, parentSet, parentId, navigation);
            if (!relationship.Many)
            {
                var page = await store.QueryEntitiesAsync(relationship.Target, new(null, [], "id ASC", 0, 1, parentSet, parentId, navigation), context.RequestAborted).ConfigureAwait(false);
                if (page.Entities.Count == 0) throw new ResourceNotFoundException();
                resource = resource with { Id = page.Entities[0].GetProperty("@iot.id").GetInt64() };
            }
            else if (relatedId is { } target)
            {
                var page = await store.QueryEntitiesAsync(relationship.Target, new("d.id=@p0", [target], "id ASC", 0, 1, parentSet, parentId, navigation), context.RequestAborted).ConfigureAwait(false);
                if (page.Entities.Count == 0) throw new ResourceNotFoundException();
            }
        }
        return resource;
    }

    private static (string Set, long? Id) ParseEntitySegment(string segment)
    {
        var open = segment.IndexOf('(', StringComparison.Ordinal);
        if (open < 0) return (segment, null);
        if (!segment.EndsWith(')') || !long.TryParse(segment.AsSpan(open + 1, segment.Length - open - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            throw new SensorThingsValidationException("Entity identifiers must be positive integers in parentheses.");
        return (segment[..open], id);
    }

    internal static async Task<IResult> HandleCoreRead(HttpContext context, ISensorThingsEntityStore store, StaFilterTranslator translator)
    {
        try
        {
            if (context.RequestServices.GetRequiredService<IConfiguration>().GetValue<bool>("SensorThings:ConformantProfile")
                && await store.CountUnresolvedFeaturesAsync(context.RequestAborted).ConfigureAwait(false) > 0)
                return StandardErrorHelpers.CreateServiceUnavailable(context, "The conformant sensing profile requires explicit reconciliation of legacy FeatureOfInterest associations.");
            var resource = await ResolveResourceAsync(context, store).ConfigureAwait(false);
            ValidateCoreQuery(context.Request, resource.Id is null);
            var planResult = StaQueryPlan.Create(context.Request, StaEntitySchema.For(resource.Set), translator);
            if (!planResult.IsSuccess) return planResult.StatusCode == 501 ? StandardErrorHelpers.CreateNotImplemented(context, planResult.Error ?? "Unsupported query.") : StandardErrorHelpers.CreateBadRequest(context, planResult.Error ?? "Invalid query.");
            var plan = planResult.Plan!;
            if (resource.Id is { } id)
            {
                var entity = await store.GetEntityAsync(resource.Set, id, context.RequestAborted).ConfigureAwait(false);
                if (entity is null) return StandardErrorHelpers.CreateNotFound(context, "Sensing entity was not found.");
                var shaped = await ShapeEntityAsync(context, store, translator, resource.Set, entity.Value, plan, 0).ConfigureAwait(false);
                if (resource.Property is { } property)
                {
                    JsonNode? value = shaped;
                    var members = property.Split('/');
                    foreach (var member in members)
                    {
                        if (value is null) return Results.NoContent();
                        if (value is not JsonObject complex || !complex.TryGetPropertyValue(member, out value))
                            return StandardErrorHelpers.CreateNotFound(context, "Entity property was not found.");
                    }
                    if (value is null) return Results.NoContent();
                    return resource.Raw ? Results.Text(value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : value?.ToJsonString() ?? "null", "text/plain")
                        : JsonResponse(new JsonObject { [members[^1]] = value?.DeepClone() });
                }
                if (resource.Reference) return JsonResponse(new JsonObject { ["value"] = new JsonArray(new JsonObject { ["@iot.selfLink"] = $"{StaBase(context)}/{resource.Set}({id.ToString(CultureInfo.InvariantCulture)})" }) });
                return JsonResponse(shaped);
            }
            var page = await store.QueryEntitiesAsync(resource.Set, new(plan.WhereSql, plan.WhereParameters, plan.OrderBySql, plan.Options.Skip, plan.Options.FetchTop,
                resource.ParentSet, resource.ParentId, resource.Navigation), context.RequestAborted).ConfigureAwait(false);
            var valueArray = new JsonArray();
            foreach (var entity in page.Entities.Take(plan.Options.Top))
            {
                var shaped = await ShapeEntityAsync(context, store, translator, resource.Set, entity, plan, 0).ConfigureAwait(false);
                valueArray.Add(resource.Reference ? new JsonObject { ["@iot.selfLink"] = $"{StaBase(context)}/{resource.Set}({entity.GetProperty("@iot.id").GetInt64().ToString(CultureInfo.InvariantCulture)})" } : shaped);
            }
            var result = new JsonObject { ["value"] = valueArray };
            if (plan.Options.Count) result["@iot.count"] = page.Count;
            if (page.Entities.Count > plan.Options.Top && plan.Options.Top > 0 && plan.Options.NextSkip is { } skip) result["@iot.nextLink"] = Continuation(context, skip);
            return JsonResponse(result);
        }
        catch (ResourceNotFoundException) { return StandardErrorHelpers.CreateNotFound(context, "Sensing resource was not found."); }
        catch (UnsupportedQueryOptionException ex) { return StandardErrorHelpers.CreateNotImplemented(context, ex.Message); }
        catch (SensorThingsValidationException ex) { return StandardErrorHelpers.CreateBadRequest(context, ex.Message); }
    }

    private static void ValidateCoreQuery(HttpRequest request, bool collection)
    {
        foreach (var option in request.Query)
        {
            if (!option.Key.StartsWith('$')) continue;
            if (option.Key is not ("$select" or "$expand" or "$filter" or "$orderby" or "$top" or "$skip" or "$count")) throw new UnsupportedQueryOptionException($"Query option {option.Key} is not supported.");
            if (!collection && option.Key is "$filter" or "$orderby" or "$top" or "$skip" or "$count") throw new SensorThingsValidationException($"{option.Key} requires a collection.");
            if (option.Key is "$top" or "$skip" && (!int.TryParse(option.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 0)) throw new SensorThingsValidationException($"{option.Key} requires a non-negative integer no greater than {int.MaxValue}.");
            if (option.Key == "$count" && !bool.TryParse(option.Value, out _)) throw new SensorThingsValidationException("$count requires true or false.");
        }
    }

    private static async Task<JsonObject> ShapeEntityAsync(HttpContext context, ISensorThingsEntityStore store, StaFilterTranslator translator, string set, JsonElement entity, StaQueryPlan plan, int depth)
    {
        if (depth > 10) throw new SensorThingsValidationException("$expand exceeds the maximum depth.");
        var result = JsonNode.Parse(entity.GetRawText())!.AsObject();
        var id = entity.GetProperty("@iot.id").GetInt64();
        var self = $"{StaBase(context)}/{set}({id.ToString(CultureInfo.InvariantCulture)})";
        result["@iot.selfLink"] = self;
        foreach (var relationship in SensorThingsRelationships.For(set)) result[$"{relationship.Key}@iot.navigationLink"] = $"{self}/{relationship.Key}";
        foreach (var expansion in plan.Expansions)
        {
            var relationship = SensorThingsRelationships.For(set)[expansion.Navigation];
            var nestedRequest = new DefaultHttpContext().Request;
            var nestedQuery = ExpansionQuery(expansion);
            nestedRequest.QueryString = new QueryString(QueryHelpers.AddQueryString(string.Empty, nestedQuery));
            var nestedResult = StaQueryPlan.Create(nestedRequest, StaEntitySchema.For(relationship.Target), translator);
            if (!nestedResult.IsSuccess) throw new SensorThingsValidationException(nestedResult.Error ?? "Invalid nested query.");
            var nestedPlan = nestedResult.Plan!;
            var page = await store.QueryEntitiesAsync(relationship.Target, new(expansion.WhereSql, expansion.WhereParameters, expansion.OrderBySql,
                expansion.Skip, expansion.Top == 0 ? 0 : expansion.Top + 1, set, id, expansion.Navigation), context.RequestAborted).ConfigureAwait(false);
            if (!relationship.Many)
                result[expansion.Navigation] = page.Entities.Count == 0 ? null : await ShapeEntityAsync(context, store, translator, relationship.Target, page.Entities[0], nestedPlan, depth + 1).ConfigureAwait(false);
            else
            {
                var nested = new JsonArray();
                foreach (var related in page.Entities.Take(expansion.Top)) nested.Add(await ShapeEntityAsync(context, store, translator, relationship.Target, related, nestedPlan, depth + 1).ConfigureAwait(false));
                result[expansion.Navigation] = nested;
                if (expansion.Count) result[$"{expansion.Navigation}@iot.count"] = page.Count;
                if (page.Entities.Count > expansion.Top && expansion.Top > 0)
                {
                    var query = ExpansionQuery(expansion);
                    query["$skip"] = ((long)expansion.Skip + expansion.Top).ToString(CultureInfo.InvariantCulture);
                    result[$"{expansion.Navigation}@iot.nextLink"] = QueryHelpers.AddQueryString($"{self}/{expansion.Navigation}", query);
                }
            }
        }
        if (plan.HasProjection)
        {
            var members = plan.ProjectedMembers.Concat(plan.Expansions.SelectMany(e => new[] { $"{e.Navigation}@iot.nextLink", $"{e.Navigation}@iot.count" })).ToArray();
            result = StaProjection.ProjectEntity(result, members);
        }
        return result;
    }

    private static Dictionary<string, string?> ExpansionQuery(StaExpansion expansion) => new()
    {
        ["$top"] = expansion.Top.ToString(CultureInfo.InvariantCulture), ["$skip"] = expansion.Skip.ToString(CultureInfo.InvariantCulture),
        ["$select"] = expansion.Select, ["$expand"] = expansion.Expand, ["$filter"] = expansion.Filter,
        ["$orderby"] = expansion.OrderBy, ["$count"] = expansion.Count ? "true" : null
    };

    private static string Continuation(HttpContext context, int skip)
    {
        var query = context.Request.Query.ToDictionary(p => p.Key, p => (string?)p.Value.ToString());
        query["$skip"] = skip.ToString(CultureInfo.InvariantCulture);
        return QueryHelpers.AddQueryString($"{BaseUrlResolver.GetBaseUrl(context)}{context.Request.Path}", query);
    }

    internal static async Task<IResult> HandleCoreWrite(HttpContext context, ISensorThingsEntityStore store, ILogger<SensorThingsEndpointsLog> logger)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("sensorthings.mutate", ActivityKind.Internal);
        try
        {
            var resource = await ResolveResourceAsync(context, store).ConfigureAwait(false);
            if (resource.Reference)
                return StandardErrorHelpers.CreateNotImplemented(context, "Reference mutation is not supported; use entity navigation bindings.");
            if (resource.Raw) throw new SensorThingsValidationException("Mutation requires an entity or navigation resource.");
            if (context.Request.Method == "PUT" || context.Request.ContentType?.StartsWith("application/json-patch+json", StringComparison.OrdinalIgnoreCase) == true)
                return StandardErrorHelpers.CreateNotImplemented(context, "PUT and JSON Patch are not supported; use application/json PATCH.");
            if (resource.Property is not null) throw new SensorThingsValidationException("Mutation requires an entity or navigation resource.");
            if (context.Request.Method == "DELETE")
            {
                if (resource.Id is null) throw new SensorThingsValidationException("DELETE requires an entity identifier.");
                return await store.DeleteEntityAsync(resource.Set, resource.Id.Value, context.RequestAborted).ConfigureAwait(false) ? Results.NoContent() : StandardErrorHelpers.CreateNotFound(context, "Sensing entity was not found.");
            }
            if (!context.Request.HasJsonContentType()) return StandardErrorHelpers.CreateUnsupportedMediaType(context, "Entity mutation requires application/json.");
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(false);
            var body = document.RootElement;
            if (body.ValueKind != JsonValueKind.Object) throw new SensorThingsValidationException("An entity must be a JSON object.");
            if (context.Request.Method == "POST" && resource.Set == "Observations" && resource.ParentSet is null
                && resource.Id is null && !resource.Raw && !resource.Reference
                && body.ValueKind == JsonValueKind.Object && body.TryGetProperty("value", out var bulk))
            {
                if (bulk.ValueKind != JsonValueKind.Array) throw new SensorThingsValidationException("Bulk value must be an array.");
                var ids = await store.CreateEntityBatchAsync("Observations", bulk.EnumerateArray().Select(e => e.Clone()).ToArray(), context.RequestAborted).ConfigureAwait(false);
                await PublishCreatedObservationsAsync(context, ids).ConfigureAwait(false);
                var result = new JsonObject { ["@iot.count"] = ids.Count, ["value"] = new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()) };
                return JsonResponse(result, 201);
            }
            if (context.Request.Method == "PATCH")
            {
                if (resource.Id is null) throw new SensorThingsValidationException("PATCH requires an entity identifier.");
                return await store.PatchEntityAsync(resource.Set, resource.Id.Value, body, context.RequestAborted).ConfigureAwait(false) ? Results.NoContent() : StandardErrorHelpers.CreateNotFound(context, "Sensing entity was not found.");
            }
            if (resource.Id is not null) throw new SensorThingsValidationException("POST requires an entity collection.");
            if (resource.ParentSet is not null)
            {
                var inverse = SensorThingsRelationships.For(resource.Set).Single(r => r.Value.Target == resource.ParentSet);
                var nested = JsonNode.Parse(body.GetRawText())!.AsObject();
                var reference = new JsonObject { ["@iot.id"] = resource.ParentId };
                nested[inverse.Key] = inverse.Value.Many ? new JsonArray(reference) : reference;
                body = JsonSerializer.SerializeToElement(nested, SensorThingsJsonContext.Default.JsonObject);
            }
            var id = await store.CreateEntityAsync(resource.Set, body, context.RequestAborted).ConfigureAwait(false);
            if (resource.Set == "Observations") await PublishCreatedObservationsAsync(context, [id]).ConfigureAwait(false);
            var entity = await store.GetEntityAsync(resource.Set, id, context.RequestAborted).ConfigureAwait(false);
            var plan = StaQueryPlan.Create(new DefaultHttpContext().Request, StaEntitySchema.For(resource.Set), context.RequestServices.GetRequiredService<StaFilterTranslator>()).Plan!;
            var shaped = await ShapeEntityAsync(context, store, context.RequestServices.GetRequiredService<StaFilterTranslator>(), resource.Set, entity!.Value, plan, 0).ConfigureAwait(false);
            context.Response.Headers.Location = $"{StaBase(context)}/{resource.Set}({id.ToString(CultureInfo.InvariantCulture)})";
            CoreMutationLog.Completed(logger, context.Request.Method, resource.Set, id);
            return JsonResponse(shaped, 201);
        }
        catch (ResourceNotFoundException) { return StandardErrorHelpers.CreateNotFound(context, "Sensing resource was not found."); }
        catch (JsonException) { return StandardErrorHelpers.CreateBadRequest(context, "Request body must be valid JSON."); }
        catch (SensorThingsValidationException ex) { return StandardErrorHelpers.CreateBadRequest(context, ex.Message); }
    }

    private static async Task PublishCreatedObservationsAsync(HttpContext context, IReadOnlyList<long> ids)
    {
        var observations = new List<SensorThingsObservation>();
        var observationStore = context.RequestServices.GetRequiredService<IObservationStore>();
        foreach (var id in ids)
        {
            if (await observationStore.GetObservationAsync(id, context.RequestAborted).ConfigureAwait(false) is { } observation) observations.Add(observation);
        }
        context.RequestServices.GetRequiredService<IObservationChangeEventPublisher>().PublishObservations(observations);
    }

    private static IResult JsonResponse(JsonNode value, int status = 200) => Results.Json(JsonSerializer.SerializeToElement(value, SensorThingsJsonContext.Default.JsonNode), SensorThingsJsonContext.Default.JsonElement, statusCode: status);
    private sealed class ResourceNotFoundException : Exception;
    private sealed class UnsupportedQueryOptionException(string message) : Exception(message);
}

internal static partial class CoreMutationLog
{
    [LoggerMessage(EventId = 5110, Level = LogLevel.Information, Message = "SensorThings {Method} completed for {EntitySet}({EntityId}).")]
    public static partial void Completed(ILogger logger, string method, string entitySet, long entityId);
}
