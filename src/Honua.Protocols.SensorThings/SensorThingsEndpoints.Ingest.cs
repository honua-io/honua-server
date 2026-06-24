// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.IO;
using Honua.Core.Features.SensorThings.Abstractions;
using Honua.Core.Features.SensorThings.Domain;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Protocols.SensorThings.Models;
using Honua.Protocols.SensorThings.Services;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Protocols.SensorThings;

/// <summary>
/// OGC SensorThings API (STA v1.1) Phase 2 ingest surface (#1747): REST + bulk
/// observation creation and Datastream creation. These map to the unified
/// <c>sensor.ingest</c> and <c>datastream.create</c> operations so the console and
/// AI toolset reach ingest identically. New observations are published to the
/// real-time observation stream (Phase 3) through
/// <see cref="IObservationChangeEventPublisher"/>.
/// </summary>
internal static partial class SensorThingsIngestEndpoints
{
    /// <summary>Maps the SensorThings Phase 2 ingest endpoints.</summary>
    public static IEndpointRouteBuilder MapSensorThingsIngestEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/sta/v1.1/Observations", HandleCreateObservations)
            .WithDisplayName("STA Create Observations")
            .WithName("StaCreateObservations")
            .WithSummary("Ingest one or more Observations (sensor.ingest)")
            .WithTags("SensorThings")
            .Accepts<StaObservationCreate>("application/json")
            .Produces<StaObservation>(201, "application/json")
            .Produces<StaObservationBulkResult>(201, "application/json")
            .Produces(400)
            .Produces(404);

        endpoints.MapPost("/sta/v1.1/Datastreams({id:long})/Observations", HandleCreateDatastreamObservation)
            .WithDisplayName("STA Create Datastream Observation")
            .WithName("StaCreateDatastreamObservation")
            .WithSummary("Ingest an Observation into a Datastream (sensor.ingest)")
            .WithTags("SensorThings")
            .Accepts<StaObservationCreate>("application/json")
            .Produces<StaObservation>(201, "application/json")
            .Produces(400)
            .Produces(404);

        endpoints.MapPost("/sta/v1.1/Datastreams", HandleCreateDatastream)
            .WithDisplayName("STA Create Datastream")
            .WithName("StaCreateDatastream")
            .WithSummary("Create a Datastream (datastream.create)")
            .WithTags("SensorThings")
            .Accepts<StaDatastreamCreate>("application/json")
            .Produces<StaDatastream>(201, "application/json")
            .Produces(400);

        return endpoints;
    }

    private static string StaBase(HttpContext context) =>
        $"{BaseUrlResolver.GetBaseUrl(context)}{SensorThingsEndpoints.BasePath}";

    // ---- Observation ingest ----

    private static async Task<IResult> HandleCreateObservations(
        HttpContext context,
        [FromServices] IObservationStore store,
        [FromServices] IObservationChangeEventPublisher publisher)
    {
        var ct = context.RequestAborted;

        // Read the body once; a bulk envelope ({ "value": [...] }) and a single observation
        // are distinguished by inspecting for a top-level "value" array member.
        string body;
        using (var bodyReader = new StreamReader(context.Request.Body, System.Text.Encoding.UTF8))
        {
            body = await bodyReader.ReadToEndAsync(ct).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Request body is required.");
        }

        bool isBulk;
        try
        {
            using var probe = System.Text.Json.JsonDocument.Parse(body);
            isBulk = probe.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                probe.RootElement.TryGetProperty("value", out var valueElement) &&
                valueElement.ValueKind == System.Text.Json.JsonValueKind.Array;
        }
        catch (System.Text.Json.JsonException)
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Request body must be valid JSON.");
        }

        if (isBulk)
        {
            var bulk = System.Text.Json.JsonSerializer.Deserialize(
                body, SensorThingsJsonContext.Default.StaObservationBulkCreate);
            if (bulk is null or { Value.Count: 0 })
            {
                return StandardErrorHelpers.CreateBadRequest(context, "Bulk ingest requires a non-empty 'value' array.");
            }

            return await IngestBulkAsync(context, store, publisher, bulk, ct).ConfigureAwait(false);
        }

        StaObservationCreate? single;
        try
        {
            single = System.Text.Json.JsonSerializer.Deserialize(
                body, SensorThingsJsonContext.Default.StaObservationCreate);
        }
        catch (System.Text.Json.JsonException)
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Request body must be a valid Observation or { \"value\": [...] } envelope.");
        }

        if (single is null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Request body must be a valid Observation.");
        }

        var datastreamId = single.Datastream?.IotId ?? 0;
        if (datastreamId <= 0)
        {
            return StandardErrorHelpers.CreateBadRequest(context, "An Observation must reference a Datastream by @iot.id.");
        }

        return await IngestSingleAsync(context, store, publisher, datastreamId, single, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleCreateDatastreamObservation(
        long id,
        HttpContext context,
        [FromServices] IObservationStore store,
        [FromServices] IObservationChangeEventPublisher publisher)
    {
        var ct = context.RequestAborted;
        StaObservationCreate? single;
        try
        {
            single = await context.Request.ReadFromJsonAsync(
                SensorThingsJsonContext.Default.StaObservationCreate, ct).ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException)
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Request body must be a valid Observation.");
        }

        if (single is null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Request body must be a valid Observation.");
        }

        return await IngestSingleAsync(context, store, publisher, id, single, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> IngestSingleAsync(
        HttpContext context,
        IObservationStore store,
        IObservationChangeEventPublisher publisher,
        long datastreamId,
        StaObservationCreate body,
        CancellationToken ct)
    {
        if (await store.GetDatastreamAsync(datastreamId, ct).ConfigureAwait(false) is null)
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Datastream({datastreamId}) not found.");
        }

        if (!TryBuildRow(datastreamId, body, out var row, out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(context, error);
        }

        var persisted = await store.IngestObservationsAsync(new[] { row }, ct).ConfigureAwait(false);
        publisher.PublishObservations(persisted);

        var staBase = StaBase(context);
        var observation = StaEntityMapper.MapObservation(persisted[0], staBase);
        context.Response.Headers.Location = observation.IotSelfLink;
        return Results.Json(observation, SensorThingsJsonContext.Default.StaObservation, statusCode: 201);
    }

    private static async Task<IResult> IngestBulkAsync(
        HttpContext context,
        IObservationStore store,
        IObservationChangeEventPublisher publisher,
        StaObservationBulkCreate bulk,
        CancellationToken ct)
    {
        var rows = new List<ObservationIngestRow>(bulk.Value.Count);
        foreach (var item in bulk.Value)
        {
            var datastreamId = item.Datastream?.IotId ?? 0;
            if (datastreamId <= 0)
            {
                return StandardErrorHelpers.CreateBadRequest(context, "Each Observation must reference a Datastream by @iot.id.");
            }

            if (!TryBuildRow(datastreamId, item, out var row, out var error))
            {
                return StandardErrorHelpers.CreateBadRequest(context, error);
            }

            rows.Add(row);
        }

        // Validate referenced datastreams exist (deduplicated).
        foreach (var datastreamId in rows.Select(r => r.DatastreamId).Distinct())
        {
            if (await store.GetDatastreamAsync(datastreamId, ct).ConfigureAwait(false) is null)
            {
                return StandardErrorHelpers.CreateNotFound(context, $"Datastream({datastreamId}) not found.");
            }
        }

        var persisted = await store.IngestObservationsAsync(rows, ct).ConfigureAwait(false);
        publisher.PublishObservations(persisted);

        return Results.Json(
            new StaObservationBulkResult { Count = persisted.Count, Value = persisted.Select(o => o.Id).ToList() },
            SensorThingsJsonContext.Default.StaObservationBulkResult,
            statusCode: 201);
    }

    private static bool TryBuildRow(
        long datastreamId,
        StaObservationCreate body,
        out ObservationIngestRow row,
        out string error)
    {
        row = default;
        error = string.Empty;

        var phenomenonTime = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(body.PhenomenonTime))
        {
            if (!TryParseInstant(body.PhenomenonTime, out phenomenonTime))
            {
                error = $"phenomenonTime '{body.PhenomenonTime}' is not a valid ISO-8601 instant.";
                return false;
            }
        }

        DateTimeOffset? resultTime = null;
        if (!string.IsNullOrWhiteSpace(body.ResultTime))
        {
            if (!TryParseInstant(body.ResultTime, out var rt))
            {
                error = $"resultTime '{body.ResultTime}' is not a valid ISO-8601 instant.";
                return false;
            }

            resultTime = rt;
        }

        row = new ObservationIngestRow(datastreamId, phenomenonTime, resultTime, body.Result, FeatureOfInterestId: null);
        return true;
    }

    private static bool TryParseInstant(string value, out DateTimeOffset instant) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out instant);

    // ---- Datastream create ----

    private static async Task<IResult> HandleCreateDatastream(
        HttpContext context,
        [FromServices] IObservationStore store)
    {
        var ct = context.RequestAborted;
        StaDatastreamCreate? body;
        try
        {
            body = await context.Request.ReadFromJsonAsync(
                SensorThingsJsonContext.Default.StaDatastreamCreate, ct).ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException)
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Request body must be a valid Datastream.");
        }

        if (body is null || string.IsNullOrWhiteSpace(body.Name))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "A Datastream requires a name, description, and unitOfMeasurement.");
        }

        var request = new CreateDatastreamRequest
        {
            Name = body.Name,
            Description = body.Description,
            ObservationType = string.IsNullOrWhiteSpace(body.ObservationType)
                ? "http://www.opengis.net/def/observationType/OGC-OM/2.0/OM_Measurement"
                : body.ObservationType,
            UnitName = body.UnitOfMeasurement.Name,
            UnitSymbol = body.UnitOfMeasurement.Symbol,
            UnitDefinition = body.UnitOfMeasurement.Definition,
            Thing = new RelatedEntityRef(body.Thing.IotId, body.Thing.Name, body.Thing.Description),
            Sensor = new RelatedEntityRef(body.Sensor.IotId, body.Sensor.Name, body.Sensor.Description),
            ObservedProperty = new RelatedEntityRef(
                body.ObservedProperty.IotId, body.ObservedProperty.Name, body.ObservedProperty.Description)
        };

        var created = await store.CreateDatastreamAsync(request, ct).ConfigureAwait(false);
        var staBase = StaBase(context);
        var datastream = StaEntityMapper.MapDatastream(created, staBase, expandedObservations: null);
        context.Response.Headers.Location = datastream.IotSelfLink;
        return Results.Json(datastream, SensorThingsJsonContext.Default.StaDatastream, statusCode: 201);
    }
}
