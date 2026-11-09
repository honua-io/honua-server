// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

internal static partial class SensorThingsIngestEndpoints
{
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
