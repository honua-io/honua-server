// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Tiles;
using Microsoft.AspNetCore.Http;

namespace Honua.Protocols.GeoServices;

/// <summary>
/// Shared mapping from tile-export lifecycle exceptions to sanitized GeoServices results, so the
/// MapServer and ImageServer async exportTiles adapters surface identical error semantics:
/// validation as 400, owner/resource isolation as an indistinguishable not-found, admission
/// throttling as 429 and saturated backpressure as 503, each carrying <c>Retry-After</c>.
/// </summary>
internal static class TileExportAdapterResults
{
    /// <summary>
    /// Maps a known tile-export lifecycle exception to its sanitized result. Returns <c>null</c>
    /// when the exception is not a recognized lifecycle exception, so callers rethrow it.
    /// </summary>
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
            StandardErrorHelpers.CreateServiceUnavailable(context, storeUnavailable.Message),
        _ => null
    };

    private static IResult MapAdmission(HttpContext context, TileExportAdmissionException admission)
        => admission.Outcome == ExecutionAdmissionOutcome.Throttled
            ? StandardErrorHelpers.CreateTooManyRequests(context, admission.Message, admission.RetryAfterSeconds)
            : StandardErrorHelpers.CreateServiceUnavailable(context, admission.Message, admission.RetryAfterSeconds);
}
