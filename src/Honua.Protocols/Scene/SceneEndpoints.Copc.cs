// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Scene;
using Honua.Infrastructure.SharedCaching;
using Honua.Scene.Assets;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Honua.Protocols.Scene;

internal static partial class SceneEndpoints
{
    private static void MapCopcEndpoint(IEndpointRouteBuilder endpoints)
    {
        // Anonymous by design: public COPC scenes bypass auth, while protected scenes are
        // gated inline by AuthorizeSceneReadAsync (Bearer/API-key or signed access envelope).
        endpoints.MapMethods(
                "/scenes/{sceneId}/copc",
                ["GET", "HEAD"],
                HandleGetCopc)
            .WithName("GetSceneCopc")
            .WithDisplayName("Get Scene COPC Point Cloud")
            .WithSummary("Stream byte ranges of a Cloud Optimized Point Cloud (.copc.laz) scene")
            .WithDescription("Serves bounded HTTP 206 Partial Content byte ranges and HEAD metadata for a COPC scene.")
            .WithTags(ScenesTag)
            .AllowAnonymous()
            .CacheOutput(static policy => policy.NoCache())
            .WithSharedCacheClass(ResponseCacheClass.StaticObject)
            .Produces(StatusCodes.Status200OK, contentType: SceneContentTypes.CopcContentType)
            .Produces(StatusCodes.Status206PartialContent, contentType: SceneContentTypes.CopcContentType)
            .Produces(StatusCodes.Status304NotModified)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status416RangeNotSatisfiable);
    }

    private static async Task<IResult> HandleGetCopc(
        string sceneId,
        HttpContext context,
        [FromServices] ISceneDatasetRegistry registry,
        [FromServices] ISceneObjectOriginReader originReader,
        [FromServices] IOptions<OutputCacheTtlOptions> cacheOptions,
        [FromServices] IOptions<SceneObjectOriginOptions> originOptions,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Honua.Protocols.Scene.SceneEndpoints");
        if (string.IsNullOrWhiteSpace(sceneId))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Scene identifier is required.");
        }

        var scene = await registry.FindAsync(sceneId, cancellationToken).ConfigureAwait(false);
        if (scene is null || SceneServingProjection.ResolveServingFormat(scene) != SceneServingFormat.Copc)
        {
            return StandardErrorHelpers.CreateNotFound(context, "COPC scene was not found.");
        }

        var (denied, tokenTransport) = AuthorizeSceneRead(context, scene, logger);
        if (denied is not null)
        {
            return denied;
        }

        var metadata = await originReader
            .GetMetadataAsync(scene, scene.TilesetFileName, cancellationToken)
            .ConfigureAwait(false);
        if (metadata is null)
        {
            return StandardErrorHelpers.CreateNotFound(context, "COPC scene asset was not found.");
        }

        var etag = await ConfigurationResponseETag
            .ForResponseAsync(context, metadata.ETag, SceneContentTypes.CopcContentType)
            .ConfigureAwait(false);
        var isProtected = scene.AccessPolicy is not null;
        SetMetadataCacheHeaders(
            context,
            etag,
            metadata.LastModifiedUtc,
            cacheOptions.Value.SceneTileAsset,
            isProtected,
            scene.CachePolicy,
            tokenTransport);

        var ifNoneMatch = context.Request.Headers[HeaderNames.IfNoneMatch].ToString();
        if (!context.RequestServices.GetRequiredService<IETagService>().IsModified(ifNoneMatch, etag))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        if (HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.ContentType = SceneContentTypes.CopcContentType;
            context.Response.ContentLength = metadata.ContentLength;
            return Results.Empty;
        }

        return await WriteCopcBodyAsync(
            context,
            scene,
            metadata,
            originReader,
            originOptions.Value,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> WriteCopcBodyAsync(
        HttpContext context,
        SceneDataset scene,
        SceneObjectAssetMetadata metadata,
        ISceneObjectOriginReader originReader,
        SceneObjectOriginOptions originOptions,
        CancellationToken cancellationToken)
    {
        var rangeHeader = context.Request.Headers[HeaderNames.Range].ToString();
        if (!string.IsNullOrWhiteSpace(rangeHeader)
            && (!context.Request.Headers.TryGetValue(HeaderNames.IfRange, out var ifRange)
                || (EntityTagHeaderValue.TryParse(ifRange.ToString(), out var candidate)
                    && candidate.Tag != "*"
                    && EntityTagHeaderValue.TryParse(context.Response.Headers[HeaderNames.ETag].ToString(), out var current)
                    && candidate.Compare(current, useStrongComparison: true))))
        {
            return await WriteCopcRangeResponseAsync(
                context,
                scene,
                metadata,
                rangeHeader,
                originReader,
                originOptions,
                cancellationToken).ConfigureAwait(false);
        }

        var maxFullBytes = Math.Max(1, originOptions.MaxCopcFullResponseBytes);
        if (metadata.ContentLength > maxFullBytes)
        {
            return StandardErrorHelpers.CreatePayloadTooLarge(
                context,
                string.Create(CultureInfo.InvariantCulture,
                    $"COPC asset is {metadata.ContentLength} bytes, exceeding the {maxFullBytes}-byte full-response limit. Use HTTP Range requests (Accept-Ranges: bytes)."));
        }

        await using var fullRead = await originReader
            .OpenReadAsync(scene, scene.TilesetFileName, range: null, cancellationToken)
            .ConfigureAwait(false);
        return await StreamOriginReadResultAsync(
            context,
            fullRead,
            SceneContentTypes.CopcContentType,
            StatusCodes.Status200OK,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> WriteCopcRangeResponseAsync(
        HttpContext context,
        SceneDataset scene,
        SceneObjectAssetMetadata metadata,
        string rangeHeader,
        ISceneObjectOriginReader originReader,
        SceneObjectOriginOptions originOptions,
        CancellationToken cancellationToken)
    {
        if (!CopcRangeParser.TryParseSingleByteRange(rangeHeader, metadata.ContentLength, out var start, out var end))
        {
            context.Response.Headers[HeaderNames.ContentRange] =
                string.Create(CultureInfo.InvariantCulture, $"bytes */{metadata.ContentLength}");
            return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable);
        }

        var length = end - start + 1;
        var maxRange = Math.Max(1, originOptions.MaxRangeReadBytes);
        if (length > maxRange)
        {
            context.Response.Headers[HeaderNames.ContentRange] =
                string.Create(CultureInfo.InvariantCulture, $"bytes */{metadata.ContentLength}");
            return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable);
        }

        await using var rangeRead = await originReader
            .OpenReadAsync(scene, scene.TilesetFileName, new SceneByteRange(start, length), cancellationToken)
            .ConfigureAwait(false);

        if (rangeRead.Status == SceneObjectAssetReadStatus.Found)
        {
            context.Response.Headers[HeaderNames.ContentRange] =
                string.Create(CultureInfo.InvariantCulture, $"bytes {start}-{end}/{metadata.ContentLength}");
        }

        return await StreamOriginReadResultAsync(
            context,
            rangeRead,
            SceneContentTypes.CopcContentType,
            StatusCodes.Status206PartialContent,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> StreamOriginReadResultAsync(
        HttpContext context,
        SceneObjectAssetReadResult read,
        string contentType,
        int successStatusCode,
        CancellationToken cancellationToken)
    {
        switch (read.Status)
        {
            case SceneObjectAssetReadStatus.Found when read.Content is not null:
                var contentLength = read.RangeStart.HasValue && read.RangeEnd.HasValue
                    ? read.RangeEnd.Value - read.RangeStart.Value + 1
                    : read.Metadata?.ContentLength;
                context.Response.StatusCode = successStatusCode;
                context.Response.ContentType = contentType;
                if (contentLength.HasValue)
                {
                    context.Response.ContentLength = contentLength.Value;
                }

                await read.Content.CopyToAsync(context.Response.Body, cancellationToken).ConfigureAwait(false);
                return Results.Empty;

            case SceneObjectAssetReadStatus.RangeUnsatisfiable:
                var total = read.Metadata?.ContentLength ?? 0;
                context.Response.Headers[HeaderNames.ContentRange] =
                    string.Create(CultureInfo.InvariantCulture, $"bytes */{total}");
                return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable);

            case SceneObjectAssetReadStatus.ByteBudgetExceeded:
                return StandardErrorHelpers.CreatePayloadTooLarge(
                    context,
                    read.ErrorDetail ?? "Requested scene asset exceeds the direct-stream byte ceiling.");

            case SceneObjectAssetReadStatus.InvalidPath:
            case SceneObjectAssetReadStatus.OutsideRoot:
                return StandardErrorHelpers.CreateBadRequest(context, "Invalid scene asset path.");

            default:
                return StandardErrorHelpers.CreateNotFound(context, "Scene asset was not found.");
        }
    }
}
