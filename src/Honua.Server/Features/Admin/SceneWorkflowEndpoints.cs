// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Exceptions;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Scene.Generation;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Scene;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Admin.Scene;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Admin;

internal static partial class SceneWorkflowEndpoints
{
    private const string TagAdmin = "Admin";
    private const string TagScenes = "Scene Workflows";

    public static IEndpointRouteBuilder MapSceneIngestEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var inspector = endpoints.ServiceProvider.GetService<IServiceProviderIsService>();
        if (inspector is not null && !inspector.IsService(typeof(SceneTilesetArchiveImporter)))
        {
            return endpoints;
        }

        var ingestGroup = endpoints.MapGroup("/api/v{version:apiVersion}/admin/scenes/ingest")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags(TagAdmin, TagScenes)
            .RequireAdminAuthorization()
            .RequirePlatformAdminAuthority();

        _ = ingestGroup.MapPost("/archive", HandleIngestArchive)
            .AddAgentApprovalFilter(AgentApprovalFilter.AccessGrants)
            .WithName("IngestArchiveScene")
            .WithSummary("Import a pre-tiled 3D Tiles ZIP or 3TZ archive into a hosted scene.")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<SceneArchiveIngestResponse>(StatusCodes.Status201Created)
            .Produces<SceneJobAcceptedResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .DisableAntiforgery();

        _ = ingestGroup.MapPost("/model", HandleIngestModel)
            .AddAgentApprovalFilter(AgentApprovalFilter.AccessGrants)
            .WithName("IngestModelScene")
            .WithSummary("Import a georeferenced GLB, embedded glTF, or OBJ 3D model into a hosted 3D Tiles 1.1 scene.")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<SceneModelIngestResponse>(StatusCodes.Status201Created)
            .Produces<SceneJobAcceptedResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .DisableAntiforgery();

        return endpoints;
    }

    public static IEndpointRouteBuilder MapSceneWorkflowEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var inspector = endpoints.ServiceProvider.GetService<IServiceProviderIsService>();
        if (inspector is not null && !inspector.IsService(typeof(SceneJobCoordinator)))
        {
            return endpoints;
        }

        var jobsGroup = endpoints.MapGroup("/api/v{version:apiVersion}/admin/scenes/jobs")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags(TagAdmin, TagScenes)
            .RequireAdminAuthorization()
            .RequirePlatformAdminAuthority();

        _ = jobsGroup.MapGet("/", HandleListJobs)
            .WithName("ListSceneJobs")
            .WithSummary("List asynchronous scene generation and ingest jobs.")
            .Produces<SceneJobStatusResponse[]>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        _ = jobsGroup.MapGet("/{jobId}", HandleGetJob)
            .WithName("GetSceneJob")
            .WithSummary("Get the status of an asynchronous scene generation or ingest job.")
            .Produces<SceneJobStatusResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status404NotFound);

        var assetsGroup = endpoints.MapGroup("/api/v{version:apiVersion}/admin/scenes/assets")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags(TagAdmin, TagScenes)
            .RequireAdminAuthorization()
            .RequirePlatformAdminAuthority();

        _ = assetsGroup.MapGet("/", HandleListAssets)
            .WithName("ListSceneAssets")
            .WithSummary("List unified 3D scene asset lifecycle views combining registry and job state.")
            .Produces<SceneAssetLifecycleResponse[]>(StatusCodes.Status200OK);

        _ = assetsGroup.MapGet("/{sceneId}", HandleGetAsset)
            .WithName("GetSceneAssetLifecycle")
            .WithSummary("Get the unified 3D scene asset lifecycle view for a scene.")
            .Produces<SceneAssetLifecycleResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> HandleIngestArchive(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<SceneWorkflowEndpointsLogCategory>>();
        if (!context.Request.HasFormContentType)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest,
                "Request must be multipart/form-data with a 'file' field carrying the ZIP/3TZ archive.");
        }

        var (form, formError) = await ReadUploadFormAsync(context, ImportPayloadSize.MaxSceneArchiveBytes, cancellationToken).ConfigureAwait(false);
        if (form is null)
        {
            return formError!;
        }
        var file = SceneIngestRequestHelpers.FindUpload(form);
        var asyncRequested = SceneIngestRequestHelpers.IsAsyncRequested(context, form);
        if (asyncRequested && !SceneIngestRequestHelpers.IsExperimentalAsyncEnabled(context))
        {
            return SceneIngestRequestHelpers.CreateAsyncDisabledResult(context);
        }

        var coordinator = asyncRequested
            ? context.RequestServices.GetRequiredService<SceneJobCoordinator>()
            : null;
        using var admission = coordinator?.TryReserve(file is not null && file.Length <= ImportPayloadSize.MaxSceneArchiveBytes ? file.Length : 0);
        if (coordinator is not null && admission is null)
        {
            return SceneIngestRequestHelpers.CreateBusyJobResult(context);
        }

        var (fileBytes, fileError) = await ReadUploadedBytesAsync(file, ImportPayloadSize.MaxSceneArchiveBytes, "3D Tiles archive", cancellationToken).ConfigureAwait(false);
        if (fileError is not null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, fileError);
        }

        if (!SceneIngestRequestHelpers.TryParseFormInt(form, "cacheMaxAgeSeconds", out var cacheMaxAge, out var cacheError))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, cacheError!);
        }

        if (!SceneIngestRequestHelpers.TryParseFormBool(form, "requiresAuth", out var requiresAuth, out var authError))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, authError!);
        }

        var request = new SceneTilesetArchiveImportRequest(
            ArchiveBytes: fileBytes!,
            SceneId: SceneIngestRequestHelpers.NullIfEmpty(form["sceneId"]),
            DisplayName: SceneIngestRequestHelpers.NullIfEmpty(form["displayName"]),
            Description: SceneIngestRequestHelpers.NullIfEmpty(form["description"]),
            TilesetFileName: SceneIngestRequestHelpers.NullIfEmpty(form["tilesetFileName"]),
            EditionGate: SceneIngestRequestHelpers.NullIfEmpty(form["editionGate"]),
            CacheMaxAgeSeconds: cacheMaxAge,
            RequiresAuth: requiresAuth ?? false,
            CreatedBy: context.User.Identity?.Name);

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        if (admission is not null)
        {
            var job = admission.Submit(
                sourceKind: "archive",
                requestedSceneId: request.SceneId,
                displayName: request.DisplayName,
                baseUrl: baseUrl,
                work: async (services, ct) =>
                {
                    var importer = services.GetRequiredService<SceneTilesetArchiveImporter>();
                    var outcome = await importer.ImportAsync(request, ct).ConfigureAwait(false);
                    return new SceneJobExecutionResult(
                        outcome.SceneId,
                        TileCount: null,
                        FileCount: outcome.FileCount,
                        ByteSize: outcome.TotalBytes,
                        Warnings: outcome.Warnings);
                });
            return SceneIngestRequestHelpers.CreateAcceptedJobResult(context, job);
        }

        var syncImporter = context.RequestServices.GetRequiredService<SceneTilesetArchiveImporter>();
        try
        {
            var outcome = await syncImporter.ImportAsync(request, cancellationToken).ConfigureAwait(false);
            await SceneIngestRequestHelpers.InvalidateCacheBestEffortAsync(
                context, outcome.SceneId, ex => SceneWorkflowLog.CacheInvalidationFailed(logger, outcome.SceneId, ex), cancellationToken).ConfigureAwait(false);

            var response = new SceneArchiveIngestResponse
            {
                SceneId = outcome.SceneId,
                TilesetUrl = SceneIngestRequestHelpers.BuildTilesetUrl(baseUrl, outcome.SceneId),
                I3sUrl = SceneIngestRequestHelpers.BuildI3sUrl(baseUrl, outcome.SceneId),
                TilesetFileName = outcome.TilesetFileName,
                TilesetVersion = outcome.TilesetVersion,
                FileCount = outcome.FileCount,
                TotalBytes = outcome.TotalBytes,
                Extent = SceneIngestRequestHelpers.ToExtentDto(outcome.Extent),
                Warnings = outcome.Warnings.ToArray(),
            };

            SceneWorkflowLog.ArchiveCompleted(logger, outcome.SceneId, outcome.FileCount, outcome.TotalBytes);
            return Results.Json(
                response,
                SceneWorkflowJsonContext.Default.SceneArchiveIngestResponse,
                statusCode: StatusCodes.Status201Created);
        }
        catch (ValidationException vex)
        {
            SceneWorkflowLog.RequestRejected(logger, "archive", vex.Message);
            var (status, detail) = SceneIngestRequestHelpers.MapValidationStatus(vex.Message);
            return ProblemDetailsHelpers.CreateAdminProblem(context, status, detail);
        }
        catch (SceneOutputUnwritableException ex)
        {
            SceneWorkflowLog.RequestFailed(logger, "archive", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError,
                ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        // Top-level endpoint boundary: log and return a sanitized problem response.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SceneWorkflowLog.RequestFailed(logger, "archive", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError,
                "Failed to import 3D Tiles archive.");
        }
    }

    private static async Task<IResult> HandleIngestModel(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<SceneWorkflowEndpointsLogCategory>>();
        if (!context.Request.HasFormContentType)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest,
                "Request must be multipart/form-data with a 'file' field carrying the GLB, embedded glTF, or OBJ model.");
        }

        var (form, formError) = await ReadUploadFormAsync(context, ImportPayloadSize.MaxSceneModelBytes, cancellationToken).ConfigureAwait(false);
        if (form is null)
        {
            return formError!;
        }
        var file = SceneIngestRequestHelpers.FindUpload(form);
        var asyncRequested = SceneIngestRequestHelpers.IsAsyncRequested(context, form);
        if (asyncRequested && !SceneIngestRequestHelpers.IsExperimentalAsyncEnabled(context))
        {
            return SceneIngestRequestHelpers.CreateAsyncDisabledResult(context);
        }

        var coordinator = asyncRequested
            ? context.RequestServices.GetRequiredService<SceneJobCoordinator>()
            : null;
        using var admission = coordinator?.TryReserve(file is not null && file.Length <= ImportPayloadSize.MaxSceneModelBytes ? file.Length : 0);
        if (coordinator is not null && admission is null)
        {
            return SceneIngestRequestHelpers.CreateBusyJobResult(context);
        }

        var (fileBytes, fileName, fileError) = await ReadUploadedModelAsync(file, ImportPayloadSize.MaxSceneModelBytes, cancellationToken).ConfigureAwait(false);
        if (fileError is not null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, fileError);
        }

        if (!TryParsePlacement(form, out var placement, out var placementError))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, placementError!);
        }

        if (!SceneIngestRequestHelpers.TryParseFormInt(form, "cacheMaxAgeSeconds", out var cacheMaxAge, out var cacheError))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, cacheError!);
        }

        if (!SceneIngestRequestHelpers.TryParseFormBool(form, "requiresAuth", out var requiresAuth, out var authError))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, authError!);
        }

        var request = new ModelSceneIngestRequest(
            ModelBytes: fileBytes!,
            FileName: fileName!,
            Placement: placement!,
            SceneId: SceneIngestRequestHelpers.NullIfEmpty(form["sceneId"]),
            DisplayName: SceneIngestRequestHelpers.NullIfEmpty(form["displayName"]),
            Description: SceneIngestRequestHelpers.NullIfEmpty(form["description"]),
            EditionGate: SceneIngestRequestHelpers.NullIfEmpty(form["editionGate"]),
            CacheMaxAgeSeconds: cacheMaxAge,
            RequiresAuth: requiresAuth ?? false,
            CreatedBy: context.User.Identity?.Name);

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        if (admission is not null)
        {
            var job = admission.Submit(
                sourceKind: "model",
                requestedSceneId: request.SceneId,
                displayName: request.DisplayName,
                baseUrl: baseUrl,
                work: async (services, ct) =>
                {
                    var executor = services.GetRequiredService<ModelScenePublishExecutor>();
                    var outcome = await executor.IngestAsync(request, ct).ConfigureAwait(false);
                    return new SceneJobExecutionResult(
                        outcome.SceneId,
                        TileCount: 1,
                        FileCount: 2,
                        ByteSize: outcome.ByteSize,
                        Warnings: outcome.Warnings);
                });
            return SceneIngestRequestHelpers.CreateAcceptedJobResult(context, job);
        }

        var syncExecutor = context.RequestServices.GetRequiredService<ModelScenePublishExecutor>();
        try
        {
            var outcome = await syncExecutor.IngestAsync(request, cancellationToken).ConfigureAwait(false);
            await SceneIngestRequestHelpers.InvalidateCacheBestEffortAsync(
                context, outcome.SceneId, ex => SceneWorkflowLog.CacheInvalidationFailed(logger, outcome.SceneId, ex), cancellationToken).ConfigureAwait(false);

            var response = new SceneModelIngestResponse
            {
                SceneId = outcome.SceneId,
                TilesetUrl = SceneIngestRequestHelpers.BuildTilesetUrl(baseUrl, outcome.SceneId),
                I3sUrl = SceneIngestRequestHelpers.BuildI3sUrl(baseUrl, outcome.SceneId),
                SourceFormat = outcome.SourceFormat,
                ByteSize = outcome.ByteSize,
                BoundingRegionDegrees = outcome.BoundsDegrees,
                MinHeightMeters = outcome.MinHeightMeters,
                MaxHeightMeters = outcome.MaxHeightMeters,
                Warnings = outcome.Warnings.ToArray(),
            };

            SceneWorkflowLog.ModelCompleted(logger, outcome.SceneId, outcome.SourceFormat, outcome.ByteSize);
            return Results.Json(
                response,
                SceneWorkflowJsonContext.Default.SceneModelIngestResponse,
                statusCode: StatusCodes.Status201Created);
        }
        catch (ValidationException vex)
        {
            SceneWorkflowLog.RequestRejected(logger, "model", vex.Message);
            var (status, detail) = SceneIngestRequestHelpers.MapValidationStatus(vex.Message);
            return ProblemDetailsHelpers.CreateAdminProblem(context, status, detail);
        }
        catch (SceneOutputUnwritableException ex)
        {
            SceneWorkflowLog.RequestFailed(logger, "model", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError,
                ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        // Top-level endpoint boundary: log and return a sanitized problem response.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SceneWorkflowLog.RequestFailed(logger, "model", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError,
                "Failed to ingest 3D model.");
        }
    }

    private static IResult HandleListJobs(
        HttpContext context,
        [FromServices] SceneJobCoordinator coordinator,
        [FromQuery] string? sceneId,
        [FromQuery] string? status)
    {
        if (!SceneIngestRequestHelpers.IsExperimentalAsyncEnabled(context))
        {
            return SceneIngestRequestHelpers.CreateAsyncDisabledResult(context);
        }

        var jobs = coordinator.ListJobs(sceneId, status)
            .Select(SceneIngestRequestHelpers.ToStatusResponse)
            .ToArray();
        return Results.Json(jobs, SceneWorkflowJsonContext.Default.SceneJobStatusResponseArray);
    }

    private static IResult HandleGetJob(
        string jobId,
        HttpContext context,
        [FromServices] SceneJobCoordinator coordinator)
    {
        if (!SceneIngestRequestHelpers.IsExperimentalAsyncEnabled(context))
        {
            return SceneIngestRequestHelpers.CreateAsyncDisabledResult(context);
        }

        var job = coordinator.GetJob(jobId);
        if (job is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound,
                "Scene job not found.");
        }

        return Results.Json(
            SceneIngestRequestHelpers.ToStatusResponse(job),
            SceneWorkflowJsonContext.Default.SceneJobStatusResponse);
    }

    private static async Task<IResult> HandleListAssets(
        HttpContext context,
        [FromServices] SceneJobCoordinator coordinator,
        [FromServices] ILogger<SceneWorkflowEndpointsLogCategory> logger,
        [FromQuery] bool? includeInactive,
        CancellationToken cancellationToken)
    {
        try
        {
            var baseUrl = BaseUrlResolver.GetBaseUrl(context);
            var registration = context.RequestServices.GetService<ISceneRegistrationService>();
            var records = registration is not null
                ? await registration.ListAsync(includeInactive: true, cancellationToken).ConfigureAwait(false)
                : [];

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var results = new List<SceneAssetLifecycleResponse>(records.Count);
            foreach (var record in records)
            {
                seen.Add(record.Id);
                if (includeInactive != true && record.Status != SceneDatasetStatus.Active)
                {
                    continue;
                }

                var latestJob = coordinator.GetLatestJobForScene(record.Id);
                results.Add(ToAssetResponse(SceneAssetLifecycleProjector.Project(record, latestJob, baseUrl)));
            }

            foreach (var job in coordinator.GetLatestJobsByScene())
            {
                if (!string.IsNullOrWhiteSpace(job.SceneId) && seen.Add(job.SceneId))
                {
                    results.Add(ToAssetResponse(SceneAssetLifecycleProjector.Project(null, job, baseUrl)));
                }
            }

            results.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
            return Results.Json(results.ToArray(), SceneWorkflowJsonContext.Default.SceneAssetLifecycleResponseArray);
        }
        // Top-level endpoint boundary: log and return a sanitized problem response.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SceneWorkflowLog.RequestFailed(logger, "assets.list", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError,
                "Failed to list scene asset lifecycle views.");
        }
    }

    private static async Task<IResult> HandleGetAsset(
        string sceneId,
        HttpContext context,
        [FromServices] SceneJobCoordinator coordinator,
        [FromServices] ILogger<SceneWorkflowEndpointsLogCategory> logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sceneId))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest,
                "sceneId is required.");
        }

        try
        {
            var registration = context.RequestServices.GetService<ISceneRegistrationService>();
            var record = registration is not null
                ? await registration.GetBySceneIdAsync(sceneId, cancellationToken).ConfigureAwait(false)
                : null;
            var latestJob = coordinator.GetLatestJobForScene(sceneId);

            if (record is null && latestJob is null)
            {
                return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound,
                    "Scene asset not found.");
            }

            var baseUrl = BaseUrlResolver.GetBaseUrl(context);
            var view = SceneAssetLifecycleProjector.Project(record, latestJob, baseUrl);
            return Results.Json(ToAssetResponse(view), SceneWorkflowJsonContext.Default.SceneAssetLifecycleResponse);
        }
        // Top-level endpoint boundary: log and return a sanitized problem response.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SceneWorkflowLog.RequestFailed(logger, "assets.get", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError,
                "Failed to retrieve scene asset lifecycle view.");
        }
    }

    private static async Task<(IFormCollection? Form, IResult? Error)> ReadUploadFormAsync(
        HttpContext context,
        long maxFileBytes,
        CancellationToken cancellationToken)
    {
        context.Features.Set<IFormFeature>(new FormFeature(context.Request, new FormOptions
        {
            MultipartBodyLengthLimit = maxFileBytes + ImportPayloadSize.SceneMultipartOverheadBytes,
        }));

        try
        {
            return (await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (ex is BadHttpRequestException or InvalidDataException or IOException)
        {
            var status = ex is BadHttpRequestException requestError
                ? requestError.StatusCode
                : StatusCodes.Status400BadRequest;
            return (null, ProblemDetailsHelpers.CreateAdminProblem(context, status,
                "The multipart form data could not be parsed. Check the boundary and upload size."));
        }
    }

    private static async Task<(byte[]? Bytes, string? Error)> ReadUploadedBytesAsync(
        IFormFile? file,
        long maxBytes,
        string label,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return (null, $"A non-empty 'file' field carrying the {label} is required.");
        }

        if (file.Length > maxBytes)
        {
            return (null, $"{label} exceeds the {maxBytes}-byte upload limit.");
        }

        var bytes = await SceneIngestRequestHelpers.ReadUploadAsync(file, cancellationToken).ConfigureAwait(false);
        return (bytes, null);
    }

    private static async Task<(byte[]? Bytes, string? FileName, string? Error)> ReadUploadedModelAsync(
        IFormFile? file,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return (null, null, "A non-empty 'file' field carrying the 3D model is required.");
        }

        if (file.Length > maxBytes)
        {
            return (null, null, $"3D model exceeds the {maxBytes}-byte upload limit.");
        }

        var bytes = await SceneIngestRequestHelpers.ReadUploadAsync(file, cancellationToken).ConfigureAwait(false);
        return (bytes, file.FileName ?? "model.glb", null);
    }

    private static bool TryParsePlacement(IFormCollection form, out ModelPlacement? placement, out string? error)
    {
        placement = null;
        if (!SceneIngestRequestHelpers.TryParseRequiredDouble(form, "longitude", out var lon, out error)
            || !SceneIngestRequestHelpers.TryParseRequiredDouble(form, "latitude", out var lat, out error)
            || !SceneIngestRequestHelpers.TryParseOptionalDouble(form, "height", 0.0, out var height, out error)
            || !SceneIngestRequestHelpers.TryParseOptionalDouble(form, "headingDegrees", 0.0, out var heading, out error)
            || !SceneIngestRequestHelpers.TryParseOptionalDouble(form, "pitchDegrees", 0.0, out var pitch, out error)
            || !SceneIngestRequestHelpers.TryParseOptionalDouble(form, "rollDegrees", 0.0, out var roll, out error)
            || !SceneIngestRequestHelpers.TryParseOptionalDouble(form, "scale", 1.0, out var uniformScale, out error)
            || !SceneIngestRequestHelpers.TryParseOptionalDouble(form, "scaleX", uniformScale, out var scaleX, out error)
            || !SceneIngestRequestHelpers.TryParseOptionalDouble(form, "scaleY", uniformScale, out var scaleY, out error)
            || !SceneIngestRequestHelpers.TryParseOptionalDouble(form, "scaleZ", uniformScale, out var scaleZ, out error))
        {
            return false;
        }

        placement = new ModelPlacement(lon, lat, height, heading, pitch, roll, scaleX, scaleY, scaleZ);
        return true;
    }

    private static SceneAssetLifecycleResponse ToAssetResponse(SceneAssetLifecycleSnapshot snapshot) => new()
    {
        SceneId = snapshot.SceneId,
        DatasetId = snapshot.DatasetId,
        Name = snapshot.Name,
        Description = snapshot.Description,
        SourceKind = snapshot.SourceKind,
        DatasetType = snapshot.DatasetType,
        LifecycleStatus = snapshot.LifecycleStatus,
        JobStatus = snapshot.JobStatus,
        LatestJobId = snapshot.LatestJobId,
        PublishedRevision = snapshot.PublishedRevision,
        TilesetUrl = snapshot.TilesetUrl,
        I3sUrl = snapshot.I3sUrl,
        ByteSize = snapshot.ByteSize,
        FileCount = snapshot.FileCount,
        Warnings = snapshot.Warnings,
        IsPublic = snapshot.IsPublic,
        RequiresAuth = snapshot.RequiresAuth,
        Crs = snapshot.Crs,
        Extent = SceneIngestRequestHelpers.ToExtentDto(snapshot.Extent),
        CreatedAt = snapshot.CreatedAt,
        UpdatedAt = snapshot.UpdatedAt,
    };

    internal sealed class SceneWorkflowEndpointsLogCategory;

    internal static partial class SceneWorkflowLog
    {
        [LoggerMessage(EventId = 8520, Level = LogLevel.Information,
            Message = "3D Tiles archive ingest completed: scene {SceneId}, files {FileCount}, bytes {TotalBytes}")]
        public static partial void ArchiveCompleted(ILogger logger, string sceneId, int fileCount, long totalBytes);

        [LoggerMessage(EventId = 8521, Level = LogLevel.Information,
            Message = "3D model ingest completed: scene {SceneId}, format {Format}, bytes {ByteSize}")]
        public static partial void ModelCompleted(ILogger logger, string sceneId, string format, long byteSize);

        [LoggerMessage(EventId = 8522, Level = LogLevel.Warning,
            Message = "Scene workflow request '{Operation}' rejected: {Reason}")]
        public static partial void RequestRejected(ILogger logger, string operation, string reason);

        [LoggerMessage(EventId = 8523, Level = LogLevel.Error,
            Message = "Scene workflow request '{Operation}' failed unexpectedly.")]
        public static partial void RequestFailed(ILogger logger, string operation, Exception exception);

        [LoggerMessage(EventId = 8524, Level = LogLevel.Warning,
            Message = "Scene cache invalidation failed for scene {SceneId}.")]
        public static partial void CacheInvalidationFailed(ILogger logger, string sceneId, Exception exception);
    }
}
