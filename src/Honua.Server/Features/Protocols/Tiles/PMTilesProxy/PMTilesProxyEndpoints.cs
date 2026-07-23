// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Honua.Server.Features.Protocols.Tiles.PMTilesProxy;

/// <summary>
/// Public range-proxy endpoint that streams byte ranges of a published PMTiles
/// artifact through the server. Used by deployments that cannot expose object
/// storage directly to MapLibre/PMTiles browser clients.
/// </summary>
internal static class PMTilesProxyEndpoints
{
    private const string PMTilesContentType = "application/vnd.pmtiles";

    public static IEndpointRouteBuilder MapPMTilesProxyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapMethods(
                "/api/v1/tiles/pmtiles/{*artifactId}",
                ["GET", "HEAD"],
                HandleProxy)
            .WithName("PMTilesProxy")
            .WithDisplayName("PMTiles Range Proxy")
            .WithSummary("Range-proxied access to a published PMTiles artifact")
            .WithDescription("Streams a published PMTiles artifact via HTTP range requests for MapLibre/PMTiles browser clients in private-bucket deployments.")
            .WithTags("Tiles", "PMTiles")
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);

        return endpoints;
    }

    private static async Task<IResult> HandleProxy(
        string artifactId,
        HttpContext context,
        [FromServices] PMTilesProxyService service,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artifactId))
        {
            return TypedResults.NotFound();
        }

        var resolution = await service.ResolveAsync(artifactId, cancellationToken).ConfigureAwait(false);
        if (!resolution.Exists || resolution.Metadata is null)
        {
            return TypedResults.NotFound();
        }

        var metadata = resolution.Metadata;
        var response = context.Response;
        response.Headers[HeaderNames.AcceptRanges] = "bytes";
        response.Headers[HeaderNames.ETag] = $"\"{metadata.SizeBytes:x}-{metadata.UploadedAt.ToUnixTimeSeconds():x}\"";
        response.Headers[HeaderNames.LastModified] = metadata.UploadedAt.ToString("R");
        response.ContentType = string.IsNullOrWhiteSpace(metadata.ContentType)
            ? PMTilesContentType
            : metadata.ContentType;

        if (string.Equals(context.Request.Method, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            response.ContentLength = metadata.SizeBytes;
            return TypedResults.Empty;
        }

        var rangeHeader = context.Request.Headers[HeaderNames.Range].ToString();
        var rangeResult = await service.ReadRangeAsync(metadata, rangeHeader, cancellationToken).ConfigureAwait(false);

        switch (rangeResult.Outcome)
        {
            case PMTilesRangeOutcome.Unsatisfiable:
                response.Headers[HeaderNames.ContentRange] = $"bytes */{rangeResult.TotalSize}";
                return TypedResults.StatusCode(StatusCodes.Status416RangeNotSatisfiable);

            case PMTilesRangeOutcome.NotFound:
                // Underlying object disappeared between metadata lookup and range
                // read. Match the no-Range GET fallback below by surfacing 404.
                return TypedResults.NotFound();

            case PMTilesRangeOutcome.TooLarge:
                // A no-Range GET against an artifact above the safe direct-stream
                // budget: streaming it whole would silently fail once this response
                // crosses the host's own payload ceiling (#2991) instead of a clean,
                // bounded error. Real PMTiles readers always fetch via byte ranges, so
                // only a "give me the whole file" caller (health checks, curl) hits
                // this path; point it at Range requests instead of attempting (and
                // failing) the full transfer.
                return StandardErrorHelpers.CreatePayloadTooLarge(
                    context,
                    $"Artifact '{artifactId}' is {rangeResult.TotalSize:N0} bytes, which exceeds the " +
                    $"{PMTilesProxyService.MaxDirectFullStreamBytes:N0}-byte limit for one proxy response. " +
                    "Request a smaller byte range (this endpoint advertises Accept-Ranges: bytes).");

            case PMTilesRangeOutcome.Partial:
                response.Headers[HeaderNames.ContentRange] = $"bytes {rangeResult.Start}-{rangeResult.End}/{rangeResult.TotalSize}";
                response.StatusCode = StatusCodes.Status206PartialContent;
                response.ContentLength = rangeResult.Payload?.LongLength ?? 0L;
                if (rangeResult.Payload is { Length: > 0 })
                {
                    await response.Body.WriteAsync(rangeResult.Payload, cancellationToken).ConfigureAwait(false);
                }
                return TypedResults.Empty;

            case PMTilesRangeOutcome.Full:
            default:
                var fullStream = await service.OpenFullAsync(artifactId, cancellationToken).ConfigureAwait(false);
                if (fullStream is null)
                {
                    return TypedResults.NotFound();
                }
                return Results.Stream(fullStream, response.ContentType, enableRangeProcessing: true);
        }
    }
}
