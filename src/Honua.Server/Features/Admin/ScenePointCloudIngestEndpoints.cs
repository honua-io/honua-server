// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Exceptions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Scene.PointCloud;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Licensing;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Scene;
using Honua.Server.Features.Admin.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoint for LAS/LAZ/COPC point-cloud scene ingest (#1201). Decodes an
/// uploaded LAS point cloud into a deterministic 3D Tiles point tileset (a
/// quadtree of <c>.pnts</c> tiles + <c>tileset.json</c>), registers it, and
/// returns the servable tileset URL. Enterprise-gated.
/// </summary>
/// <remarks>
/// The endpoint is mapped only when the point-cloud ingest executor is
/// registered (Postgres profiles), mirroring the CityGML/BIM and feature-layer
/// scene generation surfaces. Admin authentication is enforced by the group
/// policy; the Enterprise entitlement is enforced inside the handler so an
/// authenticated non-Enterprise operator receives a 402 with an upgrade message
/// rather than a silent 404. The heavy LAS → 3D Tiles conversion is pure-managed
/// (no native PDAL/py3dtiles dependency), so the tileset is materialised inline;
/// compressed LAZ and Cloud-Optimized Point Cloud (COPC) inputs are rejected with
/// a 400 problem-detail and are a documented follow-up.
/// </remarks>
internal static partial class ScenePointCloudIngestEndpoints
{
    private const string TagAdmin = "Admin";
    private const string TagScenes = "Scene Ingest";

    /// <summary>Upper bound on the uploaded LAS document size (256 MiB) for the admin surface.</summary>
    private const long MaxUploadBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Maps the admin point-cloud ingest endpoint. No-op when the executor is not
    /// registered (non-Postgres profiles).
    /// </summary>
    public static IEndpointRouteBuilder MapScenePointCloudIngestEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var inspector = endpoints.ServiceProvider.GetService<IServiceProviderIsService>();
        if (inspector is not null && !inspector.IsService(typeof(PointCloudScenePublishExecutor)))
        {
            return endpoints;
        }

        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/scenes/ingest")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags(TagAdmin, TagScenes)
            .RequireAdminAuthorization();

        _ = group.MapPost("/pointcloud", HandleIngestPointCloud)
            .WithName("IngestPointCloudScene")
            .WithSummary("Ingest a LAS point cloud into a servable 3D Tiles point tileset (Enterprise).")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<PointCloudIngestResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status402PaymentRequired)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .DisableAntiforgery();

        return endpoints;
    }

    private static async Task<IResult> HandleIngestPointCloud(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<ScenePointCloudIngestEndpointsLogCategory>>();

        // Enterprise gate first: an authenticated non-Enterprise operator gets a
        // 402 with an upgrade message before any document is read.
        var gateError = LicenseGate.RequireEntitlement(
            context, FeatureCatalog.ScenePointCloudIngestKey, "Point Cloud Scene Ingest", logger);
        if (gateError is not null)
        {
            return gateError;
        }

        if (!context.Request.HasFormContentType)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest,
                "Request must be multipart/form-data with a 'file' field carrying the LAS point cloud.");
        }

        var form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        var file = form.Files["file"] ?? (form.Files.Count > 0 ? form.Files[0] : null);
        if (file is null || file.Length == 0)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest,
                "A non-empty 'file' field carrying the LAS point cloud is required.");
        }
        if (file.Length > MaxUploadBytes)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest,
                $"LAS point cloud exceeds the {MaxUploadBytes}-byte upload limit.");
        }

        if (!TryParseOptionalInt(form, "cacheMaxAgeSeconds", out var cacheMaxAge, out var cacheError))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, cacheError!);
        }
        if (!TryParseOptionalBool(form, "requiresAuth", out var requiresAuth, out var authError))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, authError!);
        }
        if (!TryParseSourceCrs(form, out var sourceCrs, out var crsError))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, crsError!);
        }

        byte[] document;
        await using (var stream = file.OpenReadStream())
        {
            using var buffer = new MemoryStream(file.Length > 0 ? (int)file.Length : 0);
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            document = buffer.ToArray();
        }

        var request = new PointCloudSceneIngestRequest(
            Source: document,
            SourceCrs: sourceCrs,
            SceneId: NullIfBlank(form["sceneId"]),
            DisplayName: NullIfBlank(form["displayName"]),
            Description: NullIfBlank(form["description"]),
            EditionGate: NullIfBlank(form["editionGate"]),
            CacheMaxAgeSeconds: cacheMaxAge,
            RequiresAuth: requiresAuth ?? false,
            CreatedBy: context.User.Identity?.Name);

        var executor = context.RequestServices.GetRequiredService<PointCloudScenePublishExecutor>();

        try
        {
            var outcome = await executor.IngestAsync(request, cancellationToken).ConfigureAwait(false);
            await InvalidateSceneCacheAsync(context, outcome.SceneId, logger, cancellationToken).ConfigureAwait(false);

            var baseUrl = BaseUrlResolver.GetBaseUrl(context);
            var tilesetUrl = string.Concat(
                baseUrl.AsSpan().TrimEnd('/'),
                "/scenes/".AsSpan(),
                outcome.SceneId.AsSpan(),
                "/tileset.json".AsSpan());

            var response = new PointCloudIngestResponse
            {
                SceneId = outcome.SceneId,
                TilesetUrl = tilesetUrl,
                PointCount = outcome.PointCount,
                TileCount = outcome.TileCount,
                HasColor = outcome.HasColor,
                BoundingRegionDegrees = outcome.BoundsDegrees
            };

            ScenePointCloudIngestLog.IngestCompleted(
                logger, outcome.SceneId, outcome.PointCount, outcome.TileCount);

            return Results.Json(
                response,
                ScenePointCloudIngestJsonContext.Default.PointCloudIngestResponse,
                statusCode: StatusCodes.Status201Created);
        }
        catch (LasFormatException fex)
        {
            ScenePointCloudIngestLog.IngestRejected(logger, fex.Code, fex.Message);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest,
                $"{fex.Code}: {fex.Message}");
        }
        catch (ValidationException vex)
        {
            ScenePointCloudIngestLog.IngestRejected(logger, "VALIDATION", vex.Message);
            var (status, detail) = ClassifyValidationError(vex.Message);
            return ProblemDetailsHelpers.CreateAdminProblem(context, status, detail);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            ScenePointCloudIngestLog.IngestFailed(logger, ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError,
                "Failed to ingest LAS point cloud.");
        }
    }

    private static (int Status, string Detail) ClassifyValidationError(string message)
    {
        if (message.StartsWith($"{SceneGenerationErrorCodes.SceneRegistrationConflict}:", StringComparison.Ordinal))
        {
            return (StatusCodes.Status409Conflict, message);
        }
        return (StatusCodes.Status400BadRequest, message);
    }

    private static bool TryParseSourceCrs(IFormCollection form, out PointCloudSourceCrs value, out string? error)
    {
        // Default to longitude/latitude axis order when unspecified, matching the
        // request record default. The accepted values mirror the geographic axis
        // orders the v1 executor supports.
        value = PointCloudSourceCrs.GeographicLonLat;
        error = null;
        var raw = form["sourceCrsAxisOrder"];
        if (raw.Count == 0 || string.IsNullOrWhiteSpace(raw[0]))
        {
            return true;
        }
        switch (raw[0]!.Trim().ToLowerInvariant())
        {
            case "lonlat":
            case "longlat":
            case "xy":
                value = PointCloudSourceCrs.GeographicLonLat;
                return true;
            case "latlon":
            case "latlong":
            case "yx":
                value = PointCloudSourceCrs.GeographicLatLon;
                return true;
            default:
                error = "sourceCrsAxisOrder must be 'lonlat' or 'latlon'.";
                return false;
        }
    }

    private static bool TryParseOptionalInt(
        IFormCollection form, string key, out int? value, out string? error)
    {
        value = null;
        error = null;
        var raw = form[key];
        if (raw.Count == 0 || string.IsNullOrWhiteSpace(raw[0]))
        {
            return true;
        }
        if (!int.TryParse(raw[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            error = $"{key} must be an integer.";
            return false;
        }
        value = parsed;
        return true;
    }

    private static bool TryParseOptionalBool(
        IFormCollection form, string key, out bool? value, out string? error)
    {
        value = null;
        error = null;
        var raw = form[key];
        if (raw.Count == 0 || string.IsNullOrWhiteSpace(raw[0]))
        {
            return true;
        }
        if (!bool.TryParse(raw[0], out var parsed))
        {
            error = $"{key} must be 'true' or 'false'.";
            return false;
        }
        value = parsed;
        return true;
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

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
            ScenePointCloudIngestLog.CacheInvalidationFailed(logger, sceneId, ex);
        }
    }

    /// <summary>
    /// Log category marker used by <see cref="ILogger{TCategoryName}"/> bindings.
    /// </summary>
    internal sealed class ScenePointCloudIngestEndpointsLogCategory;

    internal static partial class ScenePointCloudIngestLog
    {
        [LoggerMessage(EventId = 8480, Level = LogLevel.Information,
            Message = "Point-cloud ingest request completed: scene {SceneId}, points {PointCount}, tiles {TileCount}")]
        public static partial void IngestCompleted(ILogger logger, string sceneId, int pointCount, int tileCount);

        [LoggerMessage(EventId = 8481, Level = LogLevel.Warning,
            Message = "Point-cloud ingest request rejected: code {Code}, reason {Reason}")]
        public static partial void IngestRejected(ILogger logger, string code, string reason);

        [LoggerMessage(EventId = 8482, Level = LogLevel.Error,
            Message = "Point-cloud ingest request failed unexpectedly.")]
        public static partial void IngestFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 8483, Level = LogLevel.Warning,
            Message = "Point-cloud ingest scene cache invalidation failed for scene {SceneId}; subsequent reads may serve stale content until cache expires.")]
        public static partial void CacheInvalidationFailed(ILogger logger, string sceneId, Exception exception);
    }
}
