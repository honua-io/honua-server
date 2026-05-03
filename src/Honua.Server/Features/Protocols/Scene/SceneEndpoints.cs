// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Protocols.Scene.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Honua.Server.Features.Protocols.Scene;

/// <summary>
/// HTTP endpoints that serve hosted OGC 3D Tiles tilesets so CesiumJS clients
/// can load <c>tileset.json</c> and resolve relative content URIs without any
/// URL rewriting on the client side.
/// </summary>
internal static partial class SceneEndpoints
{
    private const string ScenesTag = "Scenes";

    /// <summary>
    /// Query parameter the asset endpoints accept for browser-safe access
    /// envelope tokens. Mirrors
    /// <see cref="BypassOutputCacheOnSceneAccessTokenPolicy.TokenQueryParameter"/>
    /// — keep in lockstep so cache bypass and verification agree.
    /// </summary>
    private const string TokenQueryParameter = BypassOutputCacheOnSceneAccessTokenPolicy.TokenQueryParameter;

    /// <summary>
    /// Header the asset endpoints accept for native-client access envelope
    /// tokens.
    /// </summary>
    private const string TokenHeader = BypassOutputCacheOnSceneAccessTokenPolicy.TokenHeader;

    public static IEndpointRouteBuilder MapSceneEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(
                "/scenes/{sceneId}/access-envelope",
                HandleIssueAccessEnvelope)
            .WithName("IssueSceneAccessEnvelope")
            .WithDisplayName("Issue Scene Access Envelope")
            .WithSummary("Issue a short-lived signed envelope for a protected scene")
            .WithDescription(
                "Returns a short-lived HMAC-signed token that authorizes CesiumJS-style "
                + "nested tile/glTF/texture requests under the scene's asset prefix. The "
                + "caller must already hold a valid bearer credential for the scene.")
            .WithTags(ScenesTag)
            .Produces<SceneAccessEnvelope>(StatusCodes.Status200OK, contentType: "application/json")
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        endpoints.MapMethods(
                "/scenes/{sceneId}/tileset.json",
                ["GET", "HEAD"],
                HandleGetTilesetRoot)
            .WithName("GetSceneTileset")
            .WithDisplayName("Get Scene Root Tileset")
            .WithSummary("Get the root 3D Tiles tileset.json document for a hosted scene")
            .WithDescription("Returns the OGC 3D Tiles root document. Cesium and other 3D Tiles clients resolve nested tile content URIs relative to this URL.")
            .WithTags(ScenesTag)
            .CacheOutput("SceneTilesetMetadata")
            .Produces(StatusCodes.Status200OK, contentType: SceneContentTypes.JsonContentType)
            .Produces(StatusCodes.Status206PartialContent, contentType: SceneContentTypes.JsonContentType)
            .Produces(StatusCodes.Status304NotModified)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        endpoints.MapMethods(
                "/scenes/{sceneId}/{*assetPath}",
                ["GET", "HEAD"],
                HandleGetSceneAsset)
            .WithName("GetSceneAsset")
            .WithDisplayName("Get Scene Asset")
            .WithSummary("Get a 3D Tiles tile, glTF, texture, or nested tileset asset")
            .WithDescription("Serves binary tile (b3dm/i3dm/pnts/cmpt), glTF/GLB, texture, JSON, or related payloads under a hosted scene's asset prefix.")
            .WithTags(ScenesTag)
            .CacheOutput("SceneTileAsset")
            .Produces(StatusCodes.Status200OK, contentType: SceneContentTypes.DefaultContentType)
            .Produces(StatusCodes.Status200OK, contentType: SceneContentTypes.JsonContentType)
            .Produces(StatusCodes.Status200OK, contentType: "model/gltf-binary")
            .Produces(StatusCodes.Status200OK, contentType: "model/gltf+json")
            .Produces(StatusCodes.Status200OK, contentType: "image/png")
            .Produces(StatusCodes.Status200OK, contentType: "image/jpeg")
            .Produces(StatusCodes.Status200OK, contentType: "image/webp")
            .Produces(StatusCodes.Status200OK, contentType: "image/ktx")
            .Produces(StatusCodes.Status200OK, contentType: "image/ktx2")
            .Produces(StatusCodes.Status200OK, contentType: "image/basis")
            .Produces(StatusCodes.Status206PartialContent, contentType: SceneContentTypes.DefaultContentType)
            .Produces(StatusCodes.Status304NotModified)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> HandleIssueAccessEnvelope(
        string sceneId,
        HttpContext context,
        [FromServices] ISceneDatasetRegistry registry,
        [FromServices] ISceneAccessEnvelopeService envelopeService,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sceneId))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Scene identifier is required.");
        }

        var scene = await registry.FindAsync(sceneId, cancellationToken).ConfigureAwait(false);
        if (scene is null)
        {
            return StandardErrorHelpers.CreateNotFound(context, "Scene was not found.");
        }

        if (scene.Metadata?.AccessPolicy is not { } accessPolicy)
        {
            // Public scenes do not need an envelope; returning one would
            // grant an opaque token whose signature carries no incremental
            // authorization. Make the contract explicit.
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "Access envelopes are not issued for public scenes.");
        }

        var deniedResult = AccessPolicyHelpers.RequireAccess(
            context,
            layerPolicy: accessPolicy,
            servicePolicy: null,
            scope: AccessScope.Read);
        if (deniedResult is not null)
        {
            return deniedResult;
        }

        SceneAccessEnvelope envelope;
        try
        {
            envelope = envelopeService.Issue(scene.Id);
        }
        catch (InvalidOperationException)
        {
            // Signing key not configured; fail closed and surface a generic
            // problem to the client. The InvalidOperationException details
            // would leak that signing is misconfigured — already logged via
            // standard ASP.NET error pipeline.
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "Scene access envelope issuance is not configured.");
        }

        var logger = loggerFactory.CreateLogger("Honua.Server.Features.Protocols.Scene.SceneEndpoints");
        SceneAccessLog.EnvelopeIssued(logger, scene.Id, envelope.ExpiresAt);

        // Tokens are short-lived credentials; never store, never share.
        context.Response.Headers[HeaderNames.CacheControl] = "no-store";
        return Results.Json(envelope, SceneAccessEnvelopeJsonContext.Default.SceneAccessEnvelope);
    }

    private static Task<IResult> HandleGetTilesetRoot(
        string sceneId,
        HttpContext context,
        [FromServices] ISceneDatasetRegistry registry,
        [FromServices] IOptions<OutputCacheTtlOptions> cacheOptions,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
        => HandleAssetRequestAsync(
            sceneId,
            assetPath: null,
            context,
            registry,
            cacheOptions.Value.SceneTilesetMetadata,
            loggerFactory.CreateLogger("Honua.Server.Features.Protocols.Scene.SceneEndpoints"),
            cancellationToken);

    private static Task<IResult> HandleGetSceneAsset(
        string sceneId,
        string assetPath,
        HttpContext context,
        [FromServices] ISceneDatasetRegistry registry,
        [FromServices] IOptions<OutputCacheTtlOptions> cacheOptions,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
        => HandleAssetRequestAsync(
            sceneId,
            assetPath,
            context,
            registry,
            cacheOptions.Value.SceneTileAsset,
            loggerFactory.CreateLogger("Honua.Server.Features.Protocols.Scene.SceneEndpoints"),
            cancellationToken);

    private static async Task<IResult> HandleAssetRequestAsync(
        string sceneId,
        string? assetPath,
        HttpContext context,
        ISceneDatasetRegistry registry,
        TimeSpan cacheMaxAge,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sceneId))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Scene identifier is required.");
        }

        var scene = await registry.FindAsync(sceneId, cancellationToken).ConfigureAwait(false);
        if (scene is null)
        {
            return StandardErrorHelpers.CreateNotFound(context, "Scene was not found.");
        }

        // A scene with no access policy is public. The shared
        // AccessPolicyEvaluator treats "both policies null" as
        // require-authentication, which is the correct safe default for
        // catalog layers but wrong for hosted scene assets — Cesium browser
        // clients cannot attach credentials to nested asset URLs (#849
        // delivers signed-URL handoff). Skip the auth check when the scene
        // explicitly opted out by leaving its access policy unset.
        var isProtected = scene.Metadata?.AccessPolicy is not null;
        var authorizedByToken = false;
        if (scene.Metadata?.AccessPolicy is { } accessPolicy)
        {
            var policyDecision = AccessPolicyHelpers.EvaluateAccess(
                context,
                layerPolicy: accessPolicy,
                servicePolicy: null,
                scope: AccessScope.Read);

            if (!policyDecision.IsAllowed)
            {
                // Bearer/API-key auth missing or insufficient. Try the
                // signed-envelope path before failing — Cesium browser
                // clients cannot attach Authorization headers to nested
                // asset fetches and rely entirely on the envelope token.
                var rawToken = ExtractAccessToken(context);
                if (!string.IsNullOrEmpty(rawToken))
                {
                    var envelopeService = context.RequestServices
                        .GetRequiredService<ISceneAccessEnvelopeService>();
                    var validation = envelopeService.Validate(rawToken, scene.Id);
                    switch (validation)
                    {
                        case EnvelopeValidationResult.Allowed:
                            authorizedByToken = true;
                            break;
                        case EnvelopeValidationResult.Expired:
                            SceneAccessLog.TokenExpired(logger, scene.Id);
                            return StandardErrorHelpers.CreateUnauthorized(
                                context,
                                "Scene access envelope has expired.");
                        case EnvelopeValidationResult.Tampered:
                            SceneAccessLog.TokenTampered(logger, scene.Id);
                            return StandardErrorHelpers.CreateUnauthorized(
                                context,
                                "Scene access envelope is invalid.");
                        case EnvelopeValidationResult.WrongScene:
                            SceneAccessLog.TokenWrongScene(logger, scene.Id);
                            return StandardErrorHelpers.CreateForbidden(
                                context,
                                "Scene access envelope is bound to a different scene.");
                    }
                }

                if (!authorizedByToken)
                {
                    SceneAccessLog.TokenMissing(logger, scene.Id);
                    return AccessPolicyHelpers.CreateAccessDeniedResult(context, policyDecision)
                        ?? StandardErrorHelpers.CreateUnauthorized(context, AccessPolicyHelpers.AuthRequiredMessage);
                }
            }
        }

        var resolvedAssetPath = assetPath ?? scene.TilesetFileName;

        using var activity = HonuaTelemetry.StartActivity(HonuaTelemetry.Activities.TileGeneration);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, SceneTelemetry.Protocol);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, assetPath is null
            ? SceneTelemetry.OperationTileset
            : SceneTelemetry.OperationAsset);
        activity?.SetTag(SceneTelemetry.SceneIdTag, scene.Id);
        activity?.SetTag(SceneTelemetry.AssetPathTag, resolvedAssetPath);

        if (!SceneAssetResolver.TryResolve(scene, resolvedAssetPath, out var resolved, out var error))
        {
            return MapResolutionError(context, scene, resolvedAssetPath, error, activity, logger);
        }

        var etag = ComputeETag(resolved.File);
        SetCacheHeaders(context, etag, resolved.File, cacheMaxAge, isProtected, scene.CachePolicy, authorizedByToken);

        var ifNoneMatch = context.Request.Headers[HeaderNames.IfNoneMatch].ToString();
        if (!string.IsNullOrEmpty(ifNoneMatch) && IfNoneMatchMatches(ifNoneMatch, etag))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        if (HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.ContentType = resolved.ContentType;
            context.Response.ContentLength = resolved.File.Length;
            return Results.Empty;
        }

        Log.ServingAsset(logger, scene.Id, resolvedAssetPath, resolved.ContentType, resolved.File.Length);

        return Results.File(
            resolved.File.FullName,
            resolved.ContentType,
            lastModified: resolved.File.LastWriteTimeUtc,
            entityTag: null,
            enableRangeProcessing: true);
    }

    private static string? ExtractAccessToken(HttpContext context)
    {
        // Query parameter first — primary, browser-safe transport.
        if (context.Request.Query.TryGetValue(TokenQueryParameter, out var queryValues))
        {
            foreach (var value in queryValues)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
        }

        // Header transport for native clients that can attach headers to
        // nested asset fetches and prefer to keep tokens out of URLs.
        if (context.Request.Headers.TryGetValue(TokenHeader, out var headerValues))
        {
            foreach (var value in headerValues)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static IResult MapResolutionError(
        HttpContext context,
        SceneDataset scene,
        string assetPath,
        SceneAssetResolutionError error,
        Activity? activity,
        ILogger logger)
    {
        switch (error)
        {
            case SceneAssetResolutionError.InvalidPath:
            case SceneAssetResolutionError.OutsideRoot:
                activity?.SetTag(HonuaTelemetry.Tags.Error, true);
                activity?.SetTag(HonuaTelemetry.Tags.ErrorMessage, error.ToString());
                Log.RejectedAssetPath(logger, scene.Id, error.ToString(), assetPath);
                return StandardErrorHelpers.CreateBadRequest(context, "Invalid scene asset path.");

            case SceneAssetResolutionError.NotFound:
            default:
                return StandardErrorHelpers.CreateNotFound(context, "Scene asset was not found.");
        }
    }

    private static string ComputeETag(FileInfo file)
        => $"\"{file.LastWriteTimeUtc.Ticks:X16}-{file.Length:X16}\"";

    private static void SetCacheHeaders(
        HttpContext context,
        string etag,
        FileInfo file,
        TimeSpan maxAge,
        bool isProtected,
        SceneCachePolicy? scenePolicy,
        bool authorizedByToken)
    {
        var headers = context.Response.Headers;
        headers[HeaderNames.ETag] = etag;
        headers[HeaderNames.LastModified] = file.LastWriteTimeUtc.ToString("R");

        // A registered scene may pin its own cache policy (e.g. shorter
        // max-age for previews, no-store for rotated debug datasets); honor
        // that over the global default. Otherwise fall back to the configured
        // OutputCacheTtlOptions value.
        var maxAgeSeconds = scenePolicy is { } policy
            ? Math.Clamp(policy.MaxAgeSeconds, 0, int.MaxValue)
            : (int)Math.Max(0, maxAge.TotalSeconds);
        var noStore = scenePolicy?.NoStore == true;

        // Protected scenes go through the dataset access policy on every
        // request. `Cache-Control: public` would let a shared cache (CDN,
        // forward proxy) store and re-serve the body to other clients without
        // re-running the policy, so emit `private` plus `Vary: Authorization`
        // and keep the same max-age semantics. The output cache layer also
        // disables storage for authenticated requests via
        // `AnonymousOnlyOutputCachePolicy`, but the response headers must be
        // correct for downstream caches that Honua does not control. When
        // the principal is authorized only via a signed envelope token (no
        // Authorization header present), `Vary: Authorization` would let a
        // shared cache reuse the response across distinct token values —
        // skip that header on token-only access since the cache key shape
        // already differs by URL+token.
        if (noStore)
        {
            headers[HeaderNames.CacheControl] = "no-store";
            if (isProtected && !authorizedByToken)
            {
                headers[HeaderNames.Vary] = "Authorization";
            }
        }
        else if (isProtected)
        {
            headers[HeaderNames.CacheControl] = $"private, max-age={maxAgeSeconds}";
            if (!authorizedByToken)
            {
                headers[HeaderNames.Vary] = "Authorization";
            }
        }
        else
        {
            headers[HeaderNames.CacheControl] = $"public, max-age={maxAgeSeconds}";
        }

        headers[HeaderNames.AcceptRanges] = "bytes";
    }

    private static bool IfNoneMatchMatches(string headerValue, string currentETag)
    {
        var trimmed = headerValue.AsSpan().Trim();
        if (trimmed.SequenceEqual("*"))
        {
            return true;
        }

        foreach (var rangeChunk in headerValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = rangeChunk;
            if (candidate.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
            {
                candidate = candidate[2..];
            }

            if (string.Equals(candidate, currentETag, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 8401, Level = LogLevel.Warning,
            Message = "Rejected scene asset request for scene {SceneId}: {Reason} (path: {AssetPath})")]
        public static partial void RejectedAssetPath(ILogger logger, string sceneId, string reason, string assetPath);

        [LoggerMessage(EventId = 8402, Level = LogLevel.Debug,
            Message = "Serving scene asset {SceneId}/{AssetPath} as {ContentType} ({Bytes} bytes)")]
        public static partial void ServingAsset(ILogger logger, string sceneId, string assetPath, string contentType, long bytes);
    }
}

/// <summary>
/// Telemetry constants for scene endpoints. Kept local because the protocol
/// name is server-side only and not useful in <see cref="HonuaTelemetry"/>.
/// </summary>
internal static class SceneTelemetry
{
    public const string Protocol = "Scene-3DTiles";
    public const string SceneIdTag = "honua.scene.id";
    public const string AssetPathTag = "honua.scene.asset_path";
    public const string OperationTileset = "scene.tileset";
    public const string OperationAsset = "scene.asset";
}
