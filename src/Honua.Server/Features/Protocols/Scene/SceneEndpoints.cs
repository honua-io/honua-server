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

    // Comma-separated `Vary` value for credential-authorized protected scene
    // responses. `Authorization` covers Bearer and Basic-compat (used by
    // ApiKeyAuthenticationHandler) auth; `X-API-Key` covers the canonical
    // API-key transport. Both are required so a private cache cannot reuse a
    // response across requests with different credentials when only one of the
    // two headers carried the credential that authorized the body.
    private const string CredentialVaryHeaders = "Authorization, X-API-Key";

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
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status500InternalServerError);

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
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status500InternalServerError);

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
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status500InternalServerError);

        return endpoints;
    }

    private static async Task<IResult> HandleIssueAccessEnvelope(
        string sceneId,
        HttpContext context,
        [FromServices] ISceneDatasetRegistry registry,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Honua.Server.Features.Protocols.Scene.SceneEndpoints");

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
            // Resolve the envelope service lazily inside the try block.
            // SceneAccessEnvelopeService throws InvalidOperationException
            // from its constructor when any binding option is invalid
            // (SigningKey unset, TokenTtlMinutes or RefreshAfterFractionOfTtl
            // out of range); routing the resolve through [FromServices]
            // would surface that exception during parameter binding before
            // the catch could intercept it.
            var envelopeService = context.RequestServices
                .GetRequiredService<ISceneAccessEnvelopeService>();
            envelope = envelopeService.Issue(scene.Id);
        }
        catch (InvalidOperationException ex)
        {
            // Envelope options invalid (or hash unexpectedly failed). Log the
            // misconfiguration reason so the operator can see which option is
            // broken; return a structured 500 rather than leaking the internal
            // message. The exception message is constructed from well-known
            // option keys and bounded constants only — never from caller or
            // sensitive values — so it is safe to log verbatim.
            SceneAccessLog.OptionsMisconfigured(logger, scene.Id, ex.Message);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "Scene access envelope issuance is not configured.");
        }

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
        var tokenTransport = SceneAccessTokenTransport.None;
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
                var rawToken = BypassOutputCacheOnSceneAccessTokenPolicy.TryExtractToken(
                    context,
                    out var extractedTokenTransport);
                if (!string.IsNullOrEmpty(rawToken))
                {
                    EnvelopeValidationResult validation;
                    try
                    {
                        // SceneAccessEnvelopeService throws
                        // InvalidOperationException from its constructor
                        // when any binding option is invalid (signing key
                        // unset, TTL or refresh fraction out of range).
                        // Resolving inside this try block keeps the
                        // misconfiguration response structured (matches the
                        // issue endpoint) rather than surfacing as an
                        // unhandled 500.
                        var envelopeService = context.RequestServices
                            .GetRequiredService<ISceneAccessEnvelopeService>();
                        validation = envelopeService.Validate(rawToken, scene.Id);
                    }
                    catch (InvalidOperationException ex)
                    {
                        SceneAccessLog.OptionsMisconfigured(logger, scene.Id, ex.Message);
                        return StandardErrorHelpers.CreateInternalServerError(
                            context,
                            "Scene access envelope verification is not configured.");
                    }

                    switch (validation)
                    {
                        case EnvelopeValidationResult.Allowed:
                            tokenTransport = extractedTokenTransport;
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

                if (tokenTransport is SceneAccessTokenTransport.None)
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
        SetCacheHeaders(context, etag, resolved.File, cacheMaxAge, isProtected, scene.CachePolicy, tokenTransport);

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
        SceneAccessTokenTransport tokenTransport)
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
        // re-running the policy, so emit `private` and vary by the credential
        // transport that authorized the response. Credential-authorized
        // responses vary on both `Authorization` (Bearer / Basic-compat) and
        // `X-API-Key` because either header may have authorized the request;
        // omitting one risks a private cache reusing a response across
        // different credentials. Query-token responses vary by URL already;
        // header-token responses need `Vary: X-Honua-Token` so browser/private
        // caches do not reuse a tokenized response for a later request with a
        // different or missing header.
        if (noStore)
        {
            headers[HeaderNames.CacheControl] = "no-store";
            if (isProtected && tokenTransport is SceneAccessTokenTransport.None)
            {
                headers[HeaderNames.Vary] = CredentialVaryHeaders;
            }
            else if (isProtected && tokenTransport is SceneAccessTokenTransport.Header)
            {
                headers[HeaderNames.Vary] = BypassOutputCacheOnSceneAccessTokenPolicy.TokenHeader;
            }
        }
        else if (isProtected)
        {
            headers[HeaderNames.CacheControl] = $"private, max-age={maxAgeSeconds}";
            if (tokenTransport is SceneAccessTokenTransport.None)
            {
                headers[HeaderNames.Vary] = CredentialVaryHeaders;
            }
            else if (tokenTransport is SceneAccessTokenTransport.Header)
            {
                headers[HeaderNames.Vary] = BypassOutputCacheOnSceneAccessTokenPolicy.TokenHeader;
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

    // EventIds 8404 / 8405 reserved here. 8401-8403 are owned by
    // Honua.Core's MonitoredCacheLog; 8410-8415 are owned by SceneAccessLog.
    private static partial class Log
    {
        [LoggerMessage(EventId = 8404, Level = LogLevel.Warning,
            Message = "Rejected scene asset request for scene {SceneId}: {Reason} (path: {AssetPath})")]
        public static partial void RejectedAssetPath(ILogger logger, string sceneId, string reason, string assetPath);

        [LoggerMessage(EventId = 8405, Level = LogLevel.Debug,
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
