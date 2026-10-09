// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.IO;
using Honua.Core.Features.SensorThings.Abstractions;
using Honua.Core.Features.SensorThings.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Protocols.SensorThings.Models;
using Honua.Protocols.SensorThings.Services;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

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
        var allowAnonymousWrites = endpoints.ServiceProvider
            .GetRequiredService<IConfiguration>()
            .GetValue<bool>(SensorThingsOptions.AllowAnonymousWritesDangerouslyPath, false);

        ConfigureWriteAuthorization(endpoints.MapPost("/sta/v1.1/Observations", SensorThingsEndpoints.HandleCoreWrite)
            .WithDisplayName("STA Create Observations")
            .WithName("StaCreateObservations")
            .WithSummary("Ingest one or more Observations (sensor.ingest)")
            .WithTags("SensorThings")
            .Accepts<StaObservationCreate>("application/json")
            .Produces<StaObservation>(201, "application/json")
            .Produces<StaObservationBulkResult>(201, "application/json")
            .Produces(400)
            .Produces(404), allowAnonymousWrites);

        ConfigureWriteAuthorization(endpoints.MapPost("/sta/v1.1/Datastreams({id:long})/Observations", SensorThingsEndpoints.HandleCoreWrite)
            .WithDisplayName("STA Create Datastream Observation")
            .WithName("StaCreateDatastreamObservation")
            .WithSummary("Ingest an Observation into a Datastream (sensor.ingest)")
            .WithTags("SensorThings")
            .Accepts<StaObservationCreate>("application/json")
            .Produces<StaObservation>(201, "application/json")
            .Produces(400)
            .Produces(404), allowAnonymousWrites);

        ConfigureWriteAuthorization(endpoints.MapPost("/sta/v1.1/Datastreams", SensorThingsEndpoints.HandleCoreWrite)
            .WithDisplayName("STA Create Datastream")
            .WithName("StaCreateDatastream")
            .WithSummary("Create a Datastream (datastream.create)")
            .WithTags("SensorThings")
            .Accepts<StaDatastreamCreate>("application/json")
            .Produces<StaDatastream>(201, "application/json")
            .Produces(400), allowAnonymousWrites);

        return endpoints;
    }

    private static RouteHandlerBuilder ConfigureWriteAuthorization(
        RouteHandlerBuilder builder,
        bool allowAnonymousWrites)
    {
        return allowAnonymousWrites
            ? builder.AllowAnonymous()
            : builder.RequireAdminAuthorization();
    }

}
