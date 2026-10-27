// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.FileStorage;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Honua.Server.Features.Protocols.Tiles.PMTilesProxy;

/// <summary>
/// Range-proxy endpoint that streams byte ranges of a published PMTiles artifact
/// through the server for clients that cannot read object storage directly.
/// Each read checks the source layer's current export policy, matching the COG
/// range proxy, and a retired or rebound source is not found.
/// </summary>
internal static partial class PMTilesProxyEndpoints
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
            .WithDescription("Serves a published PMTiles artifact to callers authorized for its current source layer, with HTTP range support for MapLibre and PMTiles browser clients.")
            .WithTags("Tiles", "PMTiles")
            // Every read must evaluate the current source policy, including after a
            // previously public publication becomes restricted or is retired.
            .CacheOutput(policy => policy.NoCache())
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);

        return endpoints;
    }

    private static async Task<IResult> HandleProxy(
        string artifactId,
        HttpContext context,
        [FromServices] PMTilesProxyService service,
        [FromServices] IMetadataV2GraphProvider graphProvider,
        [FromServices] ILoggerFactory loggerFactory,
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
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var binding = service.BindSource(snapshot, metadata);
        if (!binding.IsServable)
        {
            var logger = loggerFactory.CreateLogger(typeof(PMTilesProxyEndpoints));
            if (binding.Kind == PMTilesSourceBindingKind.UnboundRefused)
            {
                LogUnboundArchiveRefused(logger, artifactId);
            }
            else
            {
                LogSourceNotResolved(logger, artifactId);
            }

            return TypedResults.NotFound();
        }

        if (binding.Source is { } source)
        {
            var accessError = await AccessPolicyHelpers.RequireResourceAccessAsync(
                context,
                source.Resource,
                AuthorizationOperation.Export,
                source.Service,
                cancellationToken).ConfigureAwait(false);
            if (accessError is not null)
            {
                return accessError;
            }
        }

        var response = context.Response;
        var conditions = ArtifactHttpConditions.Evaluate(context, metadata);
        if (conditions.StatusCode is { } statusCode)
        {
            return TypedResults.StatusCode(statusCode);
        }
        response.ContentType = string.IsNullOrWhiteSpace(metadata.ContentType)
            ? PMTilesContentType
            : metadata.ContentType;

        if (string.Equals(context.Request.Method, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            response.ContentLength = metadata.SizeBytes;
            return TypedResults.Empty;
        }

        var rangeHeader = conditions.RangeHeader;
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
                var fullStream = await service.OpenFullAsync(artifactId, metadata, cancellationToken).ConfigureAwait(false);
                if (fullStream is null)
                {
                    return TypedResults.NotFound();
                }
                return Results.Stream(fullStream, response.ContentType, enableRangeProcessing: false);
        }
    }

    [LoggerMessage(
        EventId = 6541,
        Level = LogLevel.Information,
        Message = "PMTiles archive {ArtifactId} has no source binding (no layerId/serviceId/publicationId metadata) and was not served. Set FileStorage:PMTilesPublish:AllowAnonymousUnboundArchives=true to serve operator-provisioned archives.")]
    private static partial void LogUnboundArchiveRefused(ILogger logger, string artifactId);

    [LoggerMessage(
        EventId = 6542,
        Level = LogLevel.Information,
        Message = "PMTiles archive {ArtifactId} names a source that does not resolve to one routable publication and was not served.")]
    private static partial void LogSourceNotResolved(ILogger logger, string artifactId);
}
