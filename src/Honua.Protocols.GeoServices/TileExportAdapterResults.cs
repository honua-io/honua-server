// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Tiles;
using Microsoft.AspNetCore.Http;

namespace Honua.Protocols.GeoServices;

// Map and image exportTiles share one error mapping: validation is 400, an owner or resource miss is an indistinguishable not-found, admission is 429, and saturated backpressure is 503 with Retry-After.
internal static class TileExportAdapterResults
{
    public static IResult? TryMap(HttpContext context, Exception exception) => exception switch
    {
        TileExportValidationException validation =>
            StandardErrorHelpers.CreateBadRequest(context, validation.Message),
        TileExportNotFoundException notFound =>
            StandardErrorHelpers.CreateNotFound(context, notFound.Message),
        TileExportIdempotencyConflictException conflict =>
            StandardErrorHelpers.CreateBadRequest(context, conflict.Message),
        TileExportPreconditionFailedException precondition =>
            StandardErrorHelpers.CreateBadRequest(context, precondition.Message),
        TileExportAdmissionException admission =>
            MapAdmission(context, admission),
        TileExportStoreUnavailableException storeUnavailable =>
            StandardErrorHelpers.CreateServiceUnavailable(
                context,
                storeUnavailable.Message,
                retryable: true),
        _ => null
    };

    private static IResult MapAdmission(HttpContext context, TileExportAdmissionException admission)
        => admission.Outcome == ExecutionAdmissionOutcome.Throttled
            ? StandardErrorHelpers.CreateTooManyRequests(context, admission.Message, admission.RetryAfterSeconds)
            : StandardErrorHelpers.CreateServiceUnavailable(
                context,
                admission.Message,
                admission.RetryAfterSeconds);
}
