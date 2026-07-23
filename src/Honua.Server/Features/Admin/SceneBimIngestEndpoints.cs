// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Exceptions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Scene.Bim;
using Honua.Core.Features.Scene.Domain;
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
/// Admin endpoint for CityGML/BIM scene ingest (#1207). Parses an uploaded
/// CityGML document into a Building Scene Layer 3D Tiles tileset, registers it,
/// and returns the servable tileset URL. Enterprise-gated.
/// </summary>
/// <remarks>
/// The endpoint is mapped only when the CityGML ingest executor is registered
/// (Postgres profiles), mirroring the feature-layer scene generation surface.
/// Admin authentication is enforced by the group policy; the Enterprise
/// entitlement is enforced inside the handler so an authenticated non-Enterprise
/// operator receives a 402 with an upgrade message rather than a silent 404.
/// </remarks>
internal static partial class SceneBimIngestEndpoints
{
    private const string TagAdmin = "Admin";
    private const string TagScenes = "Scene Ingest";

    /// <summary>Upper bound on the uploaded CityGML document size (64 MiB) for the admin surface.</summary>
    private const long MaxUploadBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Maps the admin CityGML ingest endpoint. No-op when the executor is not
    /// registered (non-Postgres profiles).
    /// </summary>
    public static IEndpointRouteBuilder MapSceneBimIngestEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var inspector = endpoints.ServiceProvider.GetService<IServiceProviderIsService>();
        if (inspector is not null && !inspector.IsService(typeof(CityGmlScenePublishExecutor)))
        {
            return endpoints;
        }

        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/scenes/ingest")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags(TagAdmin, TagScenes)
            .RequireAdminAuthorization();

        _ = group.MapPost("/citygml", HandleIngestCityGml)
            .WithName("IngestCityGmlScene")
            .WithSummary("Ingest a CityGML document into a servable Building Scene Layer tileset (Enterprise).")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<CityGmlIngestResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status402PaymentRequired)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .DisableAntiforgery();

        return endpoints;
    }

    private static async Task<IResult> HandleIngestCityGml(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<SceneBimIngestEndpointsLogCategory>>();

        // Enterprise gate first: an authenticated non-Enterprise operator gets a
        // 402 with an upgrade message before any document is read.
        var gateError = LicenseGate.RequireEntitlement(
            context, FeatureCatalog.SceneBimIngestKey, "CityGML/BIM Scene Ingest", logger);
        if (gateError is not null)
        {
            return gateError;
        }

        if (!context.Request.HasFormContentType)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest,
                "Request must be multipart/form-data with a 'file' field carrying the CityGML document.");
        }

        var form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        var file = form.Files["file"] ?? (form.Files.Count > 0 ? form.Files[0] : null);
        if (file is null || file.Length == 0)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest,
                "A non-empty 'file' field carrying the CityGML document is required.");
        }
        if (file.Length > MaxUploadBytes)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest,
                $"CityGML document exceeds the {MaxUploadBytes}-byte upload limit.");
        }

        if (!TryParseOptionalInt(form, "cacheMaxAgeSeconds", out var cacheMaxAge, out var cacheError))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, cacheError!);
        }
        if (!TryParseOptionalBool(form, "requiresAuth", out var requiresAuth, out var authError))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, authError!);
        }

        byte[] document;
        await using (var stream = file.OpenReadStream())
        {
            using var buffer = new MemoryStream(file.Length > 0 ? (int)file.Length : 0);
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            document = buffer.ToArray();
        }

        var request = new CityGmlSceneIngestRequest(
            Document: document,
            SceneId: NullIfBlank(form["sceneId"]),
            DisplayName: NullIfBlank(form["displayName"]),
            Description: NullIfBlank(form["description"]),
            EditionGate: NullIfBlank(form["editionGate"]),
            CacheMaxAgeSeconds: cacheMaxAge,
            RequiresAuth: requiresAuth ?? false,
            CreatedBy: context.User.Identity?.Name);

        var executor = context.RequestServices.GetRequiredService<CityGmlScenePublishExecutor>();

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

            var response = new CityGmlIngestResponse
            {
                SceneId = outcome.SceneId,
                TilesetUrl = tilesetUrl,
                BuildingCount = outcome.BuildingCount,
                SurfaceCount = outcome.SurfaceCount,
                TileCount = outcome.TileCount,
                Disciplines = outcome.Disciplines.ToArray(),
                BoundingRegionDegrees = outcome.BoundsDegrees,
                Warnings = outcome.Warnings.ToArray()
            };

            SceneBimIngestLog.IngestCompleted(
                logger, outcome.SceneId, outcome.BuildingCount, outcome.SurfaceCount);

            return Results.Json(
                response,
                SceneBimIngestJsonContext.Default.CityGmlIngestResponse,
                statusCode: StatusCodes.Status201Created);
        }
        catch (CityGmlFormatException fex)
        {
            SceneBimIngestLog.IngestRejected(logger, fex.Code, fex.Message);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest,
                $"{fex.Code}: {fex.Message}");
        }
        catch (ValidationException vex)
        {
            SceneBimIngestLog.IngestRejected(logger, "VALIDATION", vex.Message);
            var (status, detail) = ClassifyValidationError(vex.Message);
            return ProblemDetailsHelpers.CreateAdminProblem(context, status, detail);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        // Intentional catch-all request-handling boundary: this is the CityGML/BIM
        // ingest endpoint; the failure is logged and mapped to a generic error
        // response below.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SceneBimIngestLog.IngestFailed(logger, ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError,
                "Failed to ingest CityGML document.");
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
        // Intentional catch-all: cache invalidation is a best-effort side effect
        // of the ingest; a failure here must not fail the ingest request that
        // already succeeded.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SceneBimIngestLog.CacheInvalidationFailed(logger, sceneId, ex);
        }
    }

    /// <summary>
    /// Log category marker used by <see cref="ILogger{TCategoryName}"/> bindings.
    /// </summary>
    internal sealed class SceneBimIngestEndpointsLogCategory;

    internal static partial class SceneBimIngestLog
    {
        [LoggerMessage(EventId = 8460, Level = LogLevel.Information,
            Message = "CityGML ingest request completed: scene {SceneId}, buildings {BuildingCount}, surfaces {SurfaceCount}")]
        public static partial void IngestCompleted(ILogger logger, string sceneId, int buildingCount, int surfaceCount);

        [LoggerMessage(EventId = 8461, Level = LogLevel.Warning,
            Message = "CityGML ingest request rejected: code {Code}, reason {Reason}")]
        public static partial void IngestRejected(ILogger logger, string code, string reason);

        [LoggerMessage(EventId = 8462, Level = LogLevel.Error,
            Message = "CityGML ingest request failed unexpectedly.")]
        public static partial void IngestFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 8463, Level = LogLevel.Warning,
            Message = "CityGML ingest scene cache invalidation failed for scene {SceneId}; subsequent reads may serve stale content until cache expires.")]
        public static partial void CacheInvalidationFailed(ILogger logger, string sceneId, Exception exception);
    }
}
