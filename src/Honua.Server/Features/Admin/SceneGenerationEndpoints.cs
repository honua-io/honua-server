// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Exceptions;
using Honua.Core.Features.Publishing.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Scene;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for the v1 3D Tiles generation pipeline (#842).
/// </summary>
/// <remarks>
/// The endpoint creates an in-memory <see cref="PublishIntent"/>, dispatches
/// it through <see cref="SceneTilesPublishExecutor"/>, and returns the
/// generated scene's discoverable URL once the synchronous generation job
/// completes. Asynchronous job tracking via a durable
/// <c>IPublishIntentStore</c> is intentionally deferred — small/medium
/// datasets are well within an admin-request timeout budget.
/// </remarks>
internal static partial class SceneGenerationEndpoints
{
    private const string TagAdmin = "Admin";
    private const string TagScenes = "Scene Generation";

    /// <summary>
    /// Maps the admin scene-generation endpoints into the request pipeline.
    /// No-op when the executor is not registered (non-Postgres profiles).
    /// </summary>
    public static IEndpointRouteBuilder MapSceneGenerationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var inspector = endpoints.ServiceProvider.GetService<IServiceProviderIsService>();
        if (inspector is not null && !inspector.IsService(typeof(SceneTilesPublishExecutor)))
        {
            return endpoints;
        }

        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/scenes/generate")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags(TagAdmin, TagScenes)
            .RequireAdminAuthorization();

        _ = group.MapPost("/", HandleGenerateScene)
            .WithName("GenerateScene")
            .WithSummary("Generate a 3D Tiles tileset from a feature layer.")
            .Accepts<GenerateSceneRequest>("application/json")
            .Produces<GenerateSceneResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return endpoints;
    }

    private static async Task<IResult> HandleGenerateScene(
        HttpContext context,
        [FromBody] GenerateSceneRequest request,
        [FromServices] SceneTilesPublishExecutor executor,
        [FromServices] ILogger<SceneGenerationEndpointsLogCategory> logger,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest,
                "Request body is required.");
        }

        if (request.LayerId <= 0)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest,
                "layerId must be a positive integer.");
        }

        var intent = BuildIntent(request, context);

        try
        {
            var outcome = await executor.RunDirectAsync(intent, cancellationToken).ConfigureAwait(false);
            await InvalidateSceneCacheAsync(context, outcome.Result.SceneId, logger, cancellationToken).ConfigureAwait(false);

            var baseUrl = BaseUrlResolver.GetBaseUrl(context);
            var tilesetUrl = string.Concat(
                baseUrl.AsSpan().TrimEnd('/'),
                "/scenes/".AsSpan(),
                outcome.Result.SceneId.AsSpan(),
                "/tileset.json".AsSpan());

            var response = new GenerateSceneResponse
            {
                SceneId = outcome.Result.SceneId,
                IntentId = intent.IntentId,
                ServiceId = $"scene:{outcome.Result.SceneId}",
                TilesetUrl = tilesetUrl,
                FeatureCount = outcome.Result.Summary.FeatureCount,
                TileCount = outcome.Result.Summary.TileCount,
                BoundingRegionDegrees = outcome.Result.Summary.BoundingRegionDegrees,
                GeometricError = outcome.Result.Summary.GeometricError,
                Warnings = outcome.Result.Warnings.ToArray()
            };

            SceneGenerationEndpointsLog.GenerationCompleted(
                logger, intent.IntentId, request.LayerId, outcome.Result.SceneId,
                outcome.Result.Summary.FeatureCount);

            return Results.Json(
                response,
                SceneGenerationJsonContext.Default.GenerateSceneResponse,
                statusCode: StatusCodes.Status201Created);
        }
        catch (ValidationException vex)
        {
            SceneGenerationEndpointsLog.ValidationFailed(logger, intent.IntentId, vex.Message);
            var (status, detail) = ClassifyValidationError(vex.Message);
            return ProblemDetailsHelpers.CreateAdminProblem(context, status, detail);
        }
        catch (ServiceUnavailableException sue)
        {
            SceneGenerationEndpointsLog.ServiceUnavailable(logger, intent.IntentId, sue.Message);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status503ServiceUnavailable,
                "Scene generation is temporarily unavailable; please retry.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            SceneGenerationEndpointsLog.OperationFailed(logger, intent.IntentId, ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError,
                "Failed to generate scene.");
        }
    }

    private static PublishIntent BuildIntent(GenerateSceneRequest request, HttpContext context)
    {
        var config = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(request.SceneId))
        {
            config[SceneTilesPublishExecutor.TargetConfigSceneId] = request.SceneId;
        }
        if (!string.IsNullOrEmpty(request.DisplayName))
        {
            config[SceneTilesPublishExecutor.TargetConfigDisplayName] = request.DisplayName;
        }
        if (!string.IsNullOrEmpty(request.Description))
        {
            config[SceneTilesPublishExecutor.TargetConfigDescription] = request.Description;
        }
        if (request.IncludeAttributes is { Length: > 0 })
        {
            config[SceneTilesPublishExecutor.TargetConfigIncludeAttributes] =
                string.Join(',', request.IncludeAttributes);
        }
        if (request.MaxFeatureCount is { } max)
        {
            // Forward the value verbatim (even when non-positive). The executor
            // is the canonical authority for SCENE_OPTIONS_INVALID, and silently
            // dropping a non-positive request value here would let the caller
            // observe the server default instead of the documented 400.
            config[SceneTilesPublishExecutor.TargetConfigMaxFeatureCount] =
                max.ToString(CultureInfo.InvariantCulture);
        }
        if (request.CacheMaxAgeSeconds is { } cache)
        {
            // Same forwarding rule as maxFeatureCount: a negative value must
            // surface as SCENE_OPTIONS_INVALID via the executor rather than
            // being silently coerced to the 3600-second default.
            config[SceneTilesPublishExecutor.TargetConfigCacheMaxAge] =
                cache.ToString(CultureInfo.InvariantCulture);
        }
        if (!string.IsNullOrEmpty(request.EditionGate))
        {
            config[SceneTilesPublishExecutor.TargetConfigEditionGate] = request.EditionGate;
        }
        var createdBy = context.User.Identity?.Name;
        if (!string.IsNullOrEmpty(createdBy))
        {
            config[SceneTilesPublishExecutor.TargetConfigCreatedBy] = createdBy;
        }

        return PublishIntent.CreateDraft(
            intentId: Guid.NewGuid().ToString("N"),
            sourceKind: PublishSourceKind.FeatureLayer,
            sourceId: request.LayerId.ToString(CultureInfo.InvariantCulture),
            targetKind: PublishTargetKind.SceneService,
            targetConfig: config);
    }

    private static (int Status, string Detail) ClassifyValidationError(string message)
    {
        if (message.StartsWith($"{Honua.Core.Features.Scene.Domain.SceneGenerationErrorCodes.LayerNotFound}:", StringComparison.Ordinal))
        {
            return (StatusCodes.Status404NotFound, message);
        }
        if (message.StartsWith($"{Honua.Core.Features.Scene.Domain.SceneGenerationErrorCodes.SceneRegistrationConflict}:", StringComparison.Ordinal))
        {
            return (StatusCodes.Status409Conflict, message);
        }
        return (StatusCodes.Status400BadRequest, message);
    }

    private static async Task InvalidateSceneCacheAsync(
        HttpContext context,
        string sceneId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var invalidator = context.RequestServices.GetService<OutputCacheInvalidationService>();
        if (invalidator is null)
        {
            return;
        }
        try
        {
            await invalidator.InvalidateSceneAsync(sceneId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Cache invalidation failures must not surface to the caller — the
            // generated scene is still valid, the next request will simply
            // serve from disk via the registry lookup. We log so that systematic
            // failures are visible rather than silently producing stale reads.
            SceneGenerationEndpointsLog.CacheInvalidationFailed(logger, sceneId, ex);
        }
    }

    /// <summary>
    /// Log category marker used by <see cref="ILogger{TCategoryName}"/> bindings.
    /// </summary>
    internal sealed class SceneGenerationEndpointsLogCategory;

    internal static partial class SceneGenerationEndpointsLog
    {
        [LoggerMessage(EventId = 8420, Level = LogLevel.Information,
            Message = "Scene generation request completed: intent {IntentId}, layer {LayerId}, scene {SceneId}, features {FeatureCount}")]
        public static partial void GenerationCompleted(ILogger logger, string intentId, int layerId, string sceneId, int featureCount);

        [LoggerMessage(EventId = 8421, Level = LogLevel.Warning,
            Message = "Scene generation request validation failed: intent {IntentId}, reason {Reason}")]
        public static partial void ValidationFailed(ILogger logger, string intentId, string reason);

        [LoggerMessage(EventId = 8422, Level = LogLevel.Warning,
            Message = "Scene generation temporarily unavailable: intent {IntentId}, reason {Reason}")]
        public static partial void ServiceUnavailable(ILogger logger, string intentId, string reason);

        [LoggerMessage(EventId = 8423, Level = LogLevel.Error,
            Message = "Scene generation request failed: intent {IntentId}")]
        public static partial void OperationFailed(ILogger logger, string intentId, Exception exception);

        [LoggerMessage(EventId = 8424, Level = LogLevel.Warning,
            Message = "Scene cache invalidation failed for scene {SceneId}; subsequent reads may serve stale content until cache expires.")]
        public static partial void CacheInvalidationFailed(ILogger logger, string sceneId, Exception exception);
    }
}
