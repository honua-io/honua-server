// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.FileStorage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Honua.Server.Features.Protocols.Rasters.CogArtifacts;

/// <summary>
/// Admin publish of a layer's raster as a Cloud Optimized GeoTIFF, and the source-policy-aware
/// byte-range proxy desktop clients read it through (<c>GET</c>/<c>HEAD</c> with <c>Range</c>).
/// </summary>
internal static class CogArtifactEndpoints
{
    public static IEndpointRouteBuilder MapCogArtifactEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var admin = endpoints.MapGroup("/api/v{version:apiVersion}/admin/raster-artifacts")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Raster Artifacts")
            .RequireAdminAuthorization();

        admin.MapPost("/cog", HandlePublish)
            .WithName("PublishCogArtifact")
            .WithSummary("Publish a layer's primary raster as a Cloud Optimized GeoTIFF")
            .WithDescription("Exports the primary raster of the layer as a COG into file storage under a deterministic key and returns its source-policy-aware range-proxy URL");

        endpoints.MapMethods(
                "/api/v1/rasters/cog/{*artifactId}",
                ["GET", "HEAD"],
                HandleProxy)
            .WithName("CogArtifactProxy")
            .WithDisplayName("COG Range Proxy")
            .WithSummary("Range-proxied access to a published Cloud Optimized GeoTIFF")
            .WithDescription("Serves a published COG artifact to callers authorized for its current source, with HTTP range support for GDAL /vsicurl, ArcGIS Pro and browsers")
            .WithTags("Rasters", "COG")
            // Every read must evaluate the current source policy, including after a
            // previously public publication becomes restricted or is retired.
            .CacheOutput(policy => policy.NoCache())
            .Produces(StatusCodes.Status304NotModified)
            .Produces(StatusCodes.Status412PreconditionFailed)
            .Produces(StatusCodes.Status501NotImplemented)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);

        return endpoints;
    }

    private static async Task<IResult> HandlePublish(
        CogArtifactPublishRequest request,
        HttpContext context,
        [FromServices] CogArtifactService service,
        CancellationToken cancellationToken)
    {
        if (request is null || request.LayerId < 0)
        {
            return StandardErrorHelpers.CreateBadRequest(context, "layerId must be a non-negative publication layer index.");
        }

        var source = await service.ResolveSourceAsync(request.LayerId, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return TypedResults.NotFound();
        }

        var accessError = await AccessPolicyHelpers.RequireResourceAccessAsync(
            context, source.Resource, AuthorizationOperation.Export, source.Service, cancellationToken).ConfigureAwait(false);
        if (accessError is not null)
        {
            return accessError;
        }

        var outcome = await service.PublishAsync(source, cancellationToken).ConfigureAwait(false);
        return outcome.Status switch
        {
            CogArtifactPublishStatus.Published => Results.Json(
                outcome.Descriptor,
                CogArtifactJsonContext.Default.CogArtifactDescriptor,
                statusCode: StatusCodes.Status201Created),
            CogArtifactPublishStatus.LayerNotFound => TypedResults.NotFound(),
            CogArtifactPublishStatus.NoRaster => StandardErrorHelpers.CreateBadRequest(context, outcome.Error ?? "The layer has no raster."),
            _ => StandardErrorHelpers.CreateInternalServerError(context, outcome.Error ?? "COG publish failed."),
        };
    }

    private static async Task<IResult> HandleProxy(
        string artifactId,
        HttpContext context,
        [FromServices] CogArtifactService service,
        [FromServices] ArtifactByteReader rangeProxy,
        CancellationToken cancellationToken)
    {
        var target = await service.ResolvePublishedAsync(artifactId, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            return TypedResults.NotFound();
        }

        var accessError = await AccessPolicyHelpers.RequireResourceAccessAsync(
            context, target.Source.Resource, AuthorizationOperation.Export, target.Source.Service, cancellationToken).ConfigureAwait(false);
        if (accessError is not null)
        {
            return accessError;
        }

        var metadata = target.File;

        var response = context.Response;
        var conditions = ArtifactHttpConditions.Evaluate(context, metadata);
        if (conditions.StatusCode is { } statusCode)
        {
            return TypedResults.StatusCode(statusCode);
        }
        response.ContentType = CogArtifactService.ContentType;

        if (HttpMethods.IsHead(context.Request.Method))
        {
            response.ContentLength = metadata.SizeBytes;
            return TypedResults.Empty;
        }

        var rangeHeader = conditions.RangeHeader;
        var rangeResult = await rangeProxy.ReadRangeAsync(metadata, rangeHeader, cancellationToken).ConfigureAwait(false);

        switch (rangeResult.Outcome)
        {
            case ArtifactRangeOutcome.RevisionChanged:
            case ArtifactRangeOutcome.Unsupported:
                return ArtifactHttpConditions.ReadFailure(context, rangeResult.Outcome);

            case ArtifactRangeOutcome.Unsatisfiable:
                response.Headers[HeaderNames.ContentRange] = $"bytes */{rangeResult.TotalSize}";
                return TypedResults.StatusCode(StatusCodes.Status416RangeNotSatisfiable);

            case ArtifactRangeOutcome.NotFound:
                return ArtifactHttpConditions.ReadFailure(context, rangeResult.Outcome);

            case ArtifactRangeOutcome.TooLarge:
                return StandardErrorHelpers.CreatePayloadTooLarge(
                    context,
                    $"Artifact '{artifactId}' is {rangeResult.TotalSize:N0} bytes, which exceeds the " +
                    $"{ArtifactByteReader.MaxDirectFullStreamBytes:N0}-byte limit for one proxy response. " +
                    "Request a smaller byte range (this endpoint advertises Accept-Ranges: bytes).");

            case ArtifactRangeOutcome.Partial:
                response.Headers[HeaderNames.ContentRange] = $"bytes {rangeResult.Start}-{rangeResult.End}/{rangeResult.TotalSize}";
                response.StatusCode = StatusCodes.Status206PartialContent;
                response.ContentLength = rangeResult.Payload?.LongLength ?? 0L;
                if (rangeResult.Payload is { Length: > 0 })
                {
                    await response.Body.WriteAsync(rangeResult.Payload, cancellationToken).ConfigureAwait(false);
                }
                return TypedResults.Empty;

            case ArtifactRangeOutcome.Full:
            default:
                var fullResult = await rangeProxy.ReadFullAsync(metadata, cancellationToken).ConfigureAwait(false);
                if (fullResult.Outcome != ArtifactRangeOutcome.Full)
                {
                    return ArtifactHttpConditions.ReadFailure(context, fullResult.Outcome);
                }
                return Results.Stream(fullResult.Stream!, response.ContentType, enableRangeProcessing: false);
        }
    }
}
