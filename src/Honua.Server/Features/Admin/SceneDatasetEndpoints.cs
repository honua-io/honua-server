// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Scene;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for scene dataset registration and tileset configuration.
/// All operations are admin-only and use shared problem-details on validation
/// failure.
/// </summary>
internal static partial class SceneDatasetEndpoints
{
    private const string ScenesAdminTagAdmin = "Admin";
    private const string ScenesAdminTagScenes = "Scene Datasets";
    private const string DefaultTilesetFileName = "tileset.json";

    /// <summary>
    /// Maps the admin scene dataset endpoints into the request pipeline.
    /// </summary>
    /// <remarks>
    /// No-op when <see cref="ISceneRegistrationService"/> is not registered,
    /// which is the case for non-Postgres data-source profiles (#844). The
    /// configuration-backed scene serving path remains available; only the
    /// admin CRUD surface is suppressed. Registration is checked through
    /// <see cref="IServiceProviderIsService"/> so that the scoped registration
    /// service is not constructed from the root provider during mapping.
    /// </remarks>
    public static IEndpointRouteBuilder MapSceneDatasetEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var serviceInspector = endpoints.ServiceProvider.GetService<IServiceProviderIsService>();
        if (serviceInspector is not null && !serviceInspector.IsService(typeof(ISceneRegistrationService)))
        {
            return endpoints;
        }

        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/scenes")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags(ScenesAdminTagAdmin, ScenesAdminTagScenes)
            .RequireAdminAuthorization();

        _ = group.MapGet("/", HandleListScenes)
            .WithName("ListSceneDatasets")
            .WithSummary("List registered scene datasets.")
            .Produces<SceneDatasetSummary[]>(StatusCodes.Status200OK);

        _ = group.MapPost("/", HandleRegisterScene)
            .WithName("RegisterSceneDataset")
            .WithSummary("Register a new scene dataset.")
            .Accepts<RegisterSceneDatasetRequest>("application/json")
            .Produces<SceneDatasetDetail>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        _ = group.MapGet("/{id:guid}", HandleGetScene)
            .WithName("GetSceneDataset")
            .WithSummary("Get the full record of a scene dataset.")
            .Produces<SceneDatasetDetail>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        _ = group.MapPut("/{id:guid}", HandleUpdateScene)
            .WithName("UpdateSceneDataset")
            .WithSummary("Update mutable fields of a scene dataset.")
            .Accepts<UpdateSceneDatasetRequest>("application/json")
            .Produces<SceneDatasetDetail>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        _ = group.MapDelete("/{id:guid}", HandleDeactivateScene)
            .WithName("DeactivateSceneDataset")
            .WithSummary("Deactivate a scene dataset (soft delete).")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        _ = group.MapGet("/{id:guid}/resolve", HandleResolveScene)
            .WithName("ResolveSceneDataset")
            .WithSummary("Resolve a scene dataset to embed snippets and serving metadata.")
            .Produces<SceneDatasetResolveResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> HandleListScenes(
        HttpContext context,
        [FromServices] ISceneRegistrationService service,
        [FromServices] ILogger<SceneDatasetEndpointsLogCategory> logger,
        [FromQuery] bool? includeInactive,
        CancellationToken cancellationToken)
    {
        try
        {
            var include = includeInactive ?? false;
            var records = await service.ListAsync(include, cancellationToken).ConfigureAwait(false);

            var summaries = new SceneDatasetSummary[records.Count];
            for (var i = 0; i < records.Count; i++)
            {
                summaries[i] = ToSummary(records[i]);
            }

            SceneDatasetEndpointsLog.ListedScenes(logger, summaries.Length, include);
            return Results.Json(summaries, SceneDatasetJsonContext.Default.SceneDatasetSummaryArray);
        }
        catch (Exception ex)
        {
            SceneDatasetEndpointsLog.OperationFailed(logger, "list", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError,
                "Failed to list scene datasets.");
        }
    }

    private static async Task<IResult> HandleRegisterScene(
        HttpContext context,
        [FromBody] RegisterSceneDatasetRequest request,
        [FromServices] ISceneRegistrationService service,
        [FromServices] ILogger<SceneDatasetEndpointsLogCategory> logger,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest,
                "Request body is required.");
        }

        if (!TryBuildRecord(request, context, out var record, out var validationError))
        {
            SceneDatasetEndpointsLog.RegisterValidationFailed(logger, request.Id ?? "<unknown>", validationError);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, validationError);
        }

        try
        {
            var saved = await service.RegisterAsync(record, cancellationToken).ConfigureAwait(false);
            await InvalidateSceneCacheAsync(context, saved.Id, cancellationToken).ConfigureAwait(false);
            var detail = ToDetail(saved);

            SceneDatasetEndpointsLog.SceneRegistered(logger, saved.Id, saved.DatasetId);

            return Results.Json(
                detail,
                SceneDatasetJsonContext.Default.SceneDatasetDetail,
                statusCode: StatusCodes.Status201Created);
        }
        catch (SceneDatasetAlreadyExistsException ex)
        {
            SceneDatasetEndpointsLog.SceneRegistrationConflict(logger, request.Id, ex.Message);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status409Conflict,
                "A scene dataset with this id or name is already registered.");
        }
        catch (Exception ex)
        {
            SceneDatasetEndpointsLog.OperationFailed(logger, "register", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError,
                "Failed to register scene dataset.");
        }
    }

    private static async Task<IResult> HandleGetScene(
        Guid id,
        HttpContext context,
        [FromServices] ISceneRegistrationService service,
        [FromServices] ILogger<SceneDatasetEndpointsLogCategory> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var record = await service.GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound,
                    "Scene dataset not found.");
            }

            return Results.Json(ToDetail(record), SceneDatasetJsonContext.Default.SceneDatasetDetail);
        }
        catch (Exception ex)
        {
            SceneDatasetEndpointsLog.OperationFailed(logger, "get", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError,
                "Failed to retrieve scene dataset.");
        }
    }

    private static async Task<IResult> HandleUpdateScene(
        Guid id,
        HttpContext context,
        [FromBody] UpdateSceneDatasetRequest request,
        [FromServices] ISceneRegistrationService service,
        [FromServices] ILogger<SceneDatasetEndpointsLogCategory> logger,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest,
                "Request body is required.");
        }

        SceneDatasetRecord? existing;
        try
        {
            existing = await service.GetAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SceneDatasetEndpointsLog.OperationFailed(logger, "update.lookup", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError,
                "Failed to load scene dataset for update.");
        }

        if (existing is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound,
                "Scene dataset not found.");
        }

        if (!TryApplyUpdate(existing, request, out var updated, out var validationError))
        {
            SceneDatasetEndpointsLog.UpdateValidationFailed(logger, existing.Id, validationError);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, validationError);
        }

        try
        {
            var saved = await service.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);
            await InvalidateSceneCacheAsync(context, saved.Id, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(existing.Id, saved.Id, StringComparison.OrdinalIgnoreCase))
            {
                await InvalidateSceneCacheAsync(context, existing.Id, cancellationToken).ConfigureAwait(false);
            }
            SceneDatasetEndpointsLog.SceneUpdated(logger, saved.Id, saved.DatasetId, saved.Revision);
            return Results.Json(ToDetail(saved), SceneDatasetJsonContext.Default.SceneDatasetDetail);
        }
        catch (SceneDatasetAlreadyExistsException ex)
        {
            SceneDatasetEndpointsLog.SceneUpdateConflict(logger, existing.Id, ex.Message);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status409Conflict,
                "Another scene dataset with this name already exists.");
        }
        catch (InvalidOperationException ex)
        {
            SceneDatasetEndpointsLog.OperationFailed(logger, "update", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound,
                "Scene dataset not found.");
        }
        catch (Exception ex)
        {
            SceneDatasetEndpointsLog.OperationFailed(logger, "update", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError,
                "Failed to update scene dataset.");
        }
    }

    private static async Task<IResult> HandleDeactivateScene(
        Guid id,
        HttpContext context,
        [FromServices] ISceneRegistrationService service,
        [FromServices] ILogger<SceneDatasetEndpointsLogCategory> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            // Resolve the slug before deactivation so we can target the
            // per-scene cache tag; soft-delete returns only a boolean.
            var existing = await service.GetAsync(id, cancellationToken).ConfigureAwait(false);
            var deactivated = await service.DeactivateAsync(id, cancellationToken).ConfigureAwait(false);
            if (!deactivated)
            {
                return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound,
                    "Scene dataset not found.");
            }

            await InvalidateSceneCacheAsync(context, existing?.Id, cancellationToken).ConfigureAwait(false);
            SceneDatasetEndpointsLog.SceneDeactivated(logger, id);
            return Results.NoContent();
        }
        catch (Exception ex)
        {
            SceneDatasetEndpointsLog.OperationFailed(logger, "deactivate", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError,
                "Failed to deactivate scene dataset.");
        }
    }

    private static async Task InvalidateSceneCacheAsync(HttpContext context, string? sceneId, CancellationToken cancellationToken)
    {
        var invalidator = context.RequestServices.GetService<OutputCacheInvalidationService>();
        if (invalidator is null)
        {
            return;
        }

        await invalidator.InvalidateSceneAsync(sceneId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleResolveScene(
        Guid id,
        HttpContext context,
        [FromServices] ISceneRegistrationService service,
        [FromServices] ILogger<SceneDatasetEndpointsLogCategory> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var record = await service.GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (record is null || record.Status != SceneDatasetStatus.Active)
            {
                return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound,
                    "Scene dataset not found or not active.");
            }

            var baseUrl = BaseUrlResolver.GetBaseUrl(context);
            var tilesetUrl = string.Concat(
                baseUrl.AsSpan().TrimEnd('/'),
                "/scenes/".AsSpan(),
                record.Id.AsSpan(),
                "/tileset.json".AsSpan());

            var response = new SceneDatasetResolveResponse
            {
                DatasetId = record.DatasetId,
                Id = record.Id,
                Name = record.Name,
                TilesetUrl = tilesetUrl,
                Extent = ToDto(record.Extent),
                Crs = record.Crs,
                CachePolicy = ToDto(record.CachePolicy),
                IsPublic = record.IsPublic,
                RequiresAuth = record.RequiresAuth,
                Status = StatusToWire(record.Status),
                CesiumJsSnippet = SceneSnippetGenerator.BuildCesiumJs(tilesetUrl),
                HonuaSceneSnippet = SceneSnippetGenerator.BuildHonuaSceneTag(tilesetUrl)
            };

            return Results.Json(response, SceneDatasetJsonContext.Default.SceneDatasetResolveResponse);
        }
        catch (Exception ex)
        {
            SceneDatasetEndpointsLog.OperationFailed(logger, "resolve", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError,
                "Failed to resolve scene dataset.");
        }
    }

    private static bool TryBuildRecord(
        RegisterSceneDatasetRequest request,
        HttpContext context,
        out SceneDatasetRecord record,
        out string error)
    {
        record = null!;

        if (!SceneDatasetValidator.TryValidateSceneId(request.Id, out var idError))
        {
            error = idError;
            return false;
        }

        if (!SceneDatasetValidator.TryValidateName(request.Name, out var nameError))
        {
            error = nameError;
            return false;
        }

        if (!SceneDatasetValidator.TryValidateAssetRoot(request.AssetRoot, out var assetRootError))
        {
            error = assetRootError;
            return false;
        }

        if (!SceneDatasetValidator.TryValidateTilesetFileName(request.TilesetFileName, out var tilesetFileNameError))
        {
            error = tilesetFileNameError;
            return false;
        }

        if (!SceneDatasetValidator.TryValidateCrs(request.Crs, out var crsError))
        {
            error = crsError;
            return false;
        }

        var cachePolicy = FromDto(request.CachePolicy) ?? SceneCachePolicy.Default;
        if (!SceneDatasetValidator.TryValidateCachePolicy(cachePolicy, out var cacheError))
        {
            error = cacheError;
            return false;
        }

        if (!SceneDatasetValidator.TryValidateEditionGate(request.EditionGate, out var editionError))
        {
            error = editionError;
            return false;
        }

        if (!TryFromExtentDto(request.Extent, out var extent, out var extentDtoError))
        {
            error = extentDtoError;
            return false;
        }

        if (!SceneDatasetValidator.TryValidateExtent(extent, out var extentError))
        {
            error = extentError;
            return false;
        }

        if (!SceneDatasetValidator.TryValidateAccessFlags(request.IsPublic, request.RequiresAuth, out var flagsError))
        {
            error = flagsError;
            return false;
        }

        if (!TryParseDatasetType(request.DatasetType, out var datasetType, out var datasetTypeError))
        {
            error = datasetTypeError;
            return false;
        }

        record = new SceneDatasetRecord
        {
            DatasetId = Guid.NewGuid(),
            Id = request.Id,
            Name = request.Name,
            Description = NormalizeOptional(request.Description),
            AssetRoot = request.AssetRoot,
            TilesetFileName = string.IsNullOrWhiteSpace(request.TilesetFileName)
                ? DefaultTilesetFileName
                : request.TilesetFileName!,
            DatasetType = datasetType,
            Extent = extent,
            Crs = NormalizeOptional(request.Crs),
            CachePolicy = cachePolicy,
            EditionGate = NormalizeOptional(request.EditionGate),
            RequiresAuth = request.RequiresAuth,
            IsPublic = request.IsPublic,
            AllowedRoles = request.AllowedRoles?.Length > 0 ? request.AllowedRoles : null,
            Status = SceneDatasetStatus.Active,
            ValidationMessage = null,
            Revision = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = ResolveCreatedBy(context),
            UpdatedAt = null
        };
        error = string.Empty;
        return true;
    }

    private static bool TryApplyUpdate(
        SceneDatasetRecord existing,
        UpdateSceneDatasetRequest request,
        out SceneDatasetRecord updated,
        out string error)
    {
        updated = existing;

        var name = request.Name ?? existing.Name;
        if (!SceneDatasetValidator.TryValidateName(name, out var nameError))
        {
            error = nameError;
            return false;
        }

        var assetRoot = request.AssetRoot ?? existing.AssetRoot;
        if (!SceneDatasetValidator.TryValidateAssetRoot(assetRoot, out var assetRootError))
        {
            error = assetRootError;
            return false;
        }

        if (!SceneDatasetValidator.TryValidateTilesetFileName(request.TilesetFileName, out var tilesetFileNameError))
        {
            error = tilesetFileNameError;
            return false;
        }

        var crs = request.ClearCrs == true ? null : (request.Crs ?? existing.Crs);
        if (!SceneDatasetValidator.TryValidateCrs(crs, out var crsError))
        {
            error = crsError;
            return false;
        }

        var cachePolicy = FromDto(request.CachePolicy) ?? existing.CachePolicy ?? SceneCachePolicy.Default;
        if (!SceneDatasetValidator.TryValidateCachePolicy(cachePolicy, out var cacheError))
        {
            error = cacheError;
            return false;
        }

        var editionGate = request.ClearEditionGate == true
            ? null
            : (request.EditionGate ?? existing.EditionGate);
        if (!SceneDatasetValidator.TryValidateEditionGate(editionGate, out var editionError))
        {
            error = editionError;
            return false;
        }

        SceneExtent? extent;
        if (request.ClearExtent == true)
        {
            extent = null;
        }
        else if (request.Extent is not null)
        {
            if (!TryFromExtentDto(request.Extent, out extent, out var extentDtoError))
            {
                error = extentDtoError;
                return false;
            }
        }
        else
        {
            extent = existing.Extent;
        }

        if (!SceneDatasetValidator.TryValidateExtent(extent, out var extentError))
        {
            error = extentError;
            return false;
        }

        var isPublic = request.IsPublic ?? existing.IsPublic;
        var requiresAuth = request.RequiresAuth ?? existing.RequiresAuth;
        if (!SceneDatasetValidator.TryValidateAccessFlags(isPublic, requiresAuth, out var flagsError))
        {
            error = flagsError;
            return false;
        }

        var datasetType = existing.DatasetType;
        if (!string.IsNullOrWhiteSpace(request.DatasetType))
        {
            if (!TryParseDatasetType(request.DatasetType, out datasetType, out var datasetTypeError))
            {
                error = datasetTypeError;
                return false;
            }
        }

        IReadOnlyList<string>? allowedRoles;
        if (request.ClearAllowedRoles == true)
        {
            allowedRoles = null;
        }
        else if (request.AllowedRoles is not null)
        {
            allowedRoles = request.AllowedRoles.Length > 0 ? request.AllowedRoles : null;
        }
        else
        {
            allowedRoles = existing.AllowedRoles;
        }

        updated = existing with
        {
            Name = name,
            Description = request.Description ?? existing.Description,
            AssetRoot = assetRoot,
            TilesetFileName = string.IsNullOrWhiteSpace(request.TilesetFileName)
                ? existing.TilesetFileName
                : request.TilesetFileName!,
            DatasetType = datasetType,
            Extent = extent,
            Crs = crs,
            CachePolicy = cachePolicy,
            EditionGate = editionGate,
            RequiresAuth = requiresAuth,
            IsPublic = isPublic,
            AllowedRoles = allowedRoles
        };
        error = string.Empty;
        return true;
    }

    private static bool TryParseDatasetType(string? value, out SceneDatasetType type, out string error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            type = SceneDatasetType.HostedTiles;
            error = string.Empty;
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "hosted_tiles":
            case "hostedtiles":
                type = SceneDatasetType.HostedTiles;
                error = string.Empty;
                return true;
            case "terrain":
                type = SceneDatasetType.Terrain;
                error = string.Empty;
                return true;
            default:
                type = SceneDatasetType.HostedTiles;
                error = $"Unknown dataset type '{value}'. Allowed: hosted_tiles, terrain.";
                return false;
        }
    }

    private static bool TryFromExtentDto(
        SceneExtentDto? dto,
        out SceneExtent? extent,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        extent = null;
        error = null;

        if (dto is null)
        {
            return true;
        }

        if (dto.XMin is null || dto.YMin is null || dto.XMax is null || dto.YMax is null)
        {
            error = "Extent requires all four bounds (xMin, yMin, xMax, yMax).";
            return false;
        }

        extent = new SceneExtent(dto.XMin.Value, dto.YMin.Value, dto.XMax.Value, dto.YMax.Value);
        return true;
    }

    private static SceneCachePolicy? FromDto(SceneCachePolicyDto? dto)
        => dto is null ? null : new SceneCachePolicy(dto.MaxAgeSeconds, dto.NoStore);

    private static SceneExtentDto? ToDto(SceneExtent? extent)
        => extent is null ? null : new SceneExtentDto
        {
            XMin = extent.XMin,
            YMin = extent.YMin,
            XMax = extent.XMax,
            YMax = extent.YMax
        };

    private static SceneCachePolicyDto ToDto(SceneCachePolicy? policy)
    {
        var resolved = policy ?? SceneCachePolicy.Default;
        return new SceneCachePolicyDto
        {
            MaxAgeSeconds = resolved.MaxAgeSeconds,
            NoStore = resolved.NoStore
        };
    }

    private static string DatasetTypeToWire(SceneDatasetType type) => type switch
    {
        SceneDatasetType.HostedTiles => "hosted_tiles",
        SceneDatasetType.Terrain => "terrain",
        _ => "hosted_tiles"
    };

    private static string StatusToWire(SceneDatasetStatus status) => status switch
    {
        SceneDatasetStatus.Active => "active",
        SceneDatasetStatus.Inactive => "inactive",
        SceneDatasetStatus.ValidationFailed => "validation_failed",
        _ => "active"
    };

    private static SceneDatasetSummary ToSummary(SceneDatasetRecord record) => new()
    {
        DatasetId = record.DatasetId,
        Id = record.Id,
        Name = record.Name,
        Description = record.Description,
        DatasetType = DatasetTypeToWire(record.DatasetType),
        IsPublic = record.IsPublic,
        RequiresAuth = record.RequiresAuth,
        Status = StatusToWire(record.Status),
        Revision = record.Revision,
        CreatedAt = record.CreatedAt,
        UpdatedAt = record.UpdatedAt
    };

    private static SceneDatasetDetail ToDetail(SceneDatasetRecord record) => new()
    {
        DatasetId = record.DatasetId,
        Id = record.Id,
        Name = record.Name,
        Description = record.Description,
        DatasetType = DatasetTypeToWire(record.DatasetType),
        AssetRoot = record.AssetRoot,
        TilesetFileName = record.TilesetFileName,
        Extent = ToDto(record.Extent),
        Crs = record.Crs,
        CachePolicy = ToDto(record.CachePolicy),
        EditionGate = record.EditionGate,
        RequiresAuth = record.RequiresAuth,
        IsPublic = record.IsPublic,
        AllowedRoles = record.AllowedRoles?.ToArray(),
        Status = StatusToWire(record.Status),
        ValidationMessage = record.ValidationMessage,
        Revision = record.Revision,
        CreatedAt = record.CreatedAt,
        CreatedBy = record.CreatedBy,
        UpdatedAt = record.UpdatedAt
    };

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string ResolveCreatedBy(HttpContext context)
        => context.User.Identity?.Name is { Length: > 0 } name ? name : "admin";

    /// <summary>
    /// Log category marker used by <see cref="ILogger{TCategoryName}"/> bindings.
    /// </summary>
    internal sealed class SceneDatasetEndpointsLogCategory;

    /// <summary>
    /// Source-generated logger messages for the scene dataset admin endpoints.
    /// </summary>
    internal static partial class SceneDatasetEndpointsLog
    {
        [LoggerMessage(EventId = 4500, Level = LogLevel.Information,
            Message = "Registered scene dataset '{SceneId}' ({DatasetId})")]
        public static partial void SceneRegistered(ILogger logger, string sceneId, Guid datasetId);

        [LoggerMessage(EventId = 4501, Level = LogLevel.Warning,
            Message = "Scene dataset registration validation failed for '{SceneId}': {Error}")]
        public static partial void RegisterValidationFailed(ILogger logger, string sceneId, string error);

        [LoggerMessage(EventId = 4502, Level = LogLevel.Warning,
            Message = "Scene dataset registration conflict for '{SceneId}': {Reason}")]
        public static partial void SceneRegistrationConflict(ILogger logger, string? sceneId, string reason);

        [LoggerMessage(EventId = 4503, Level = LogLevel.Information,
            Message = "Updated scene dataset '{SceneId}' ({DatasetId}) revision {Revision}")]
        public static partial void SceneUpdated(ILogger logger, string sceneId, Guid datasetId, int revision);

        [LoggerMessage(EventId = 4504, Level = LogLevel.Warning,
            Message = "Scene dataset update validation failed for '{SceneId}': {Error}")]
        public static partial void UpdateValidationFailed(ILogger logger, string sceneId, string error);

        [LoggerMessage(EventId = 4505, Level = LogLevel.Warning,
            Message = "Scene dataset update conflict for '{SceneId}': {Reason}")]
        public static partial void SceneUpdateConflict(ILogger logger, string sceneId, string reason);

        [LoggerMessage(EventId = 4506, Level = LogLevel.Information,
            Message = "Deactivated scene dataset {DatasetId}")]
        public static partial void SceneDeactivated(ILogger logger, Guid datasetId);

        [LoggerMessage(EventId = 4507, Level = LogLevel.Debug,
            Message = "Listed {Count} scene datasets (includeInactive={IncludeInactive})")]
        public static partial void ListedScenes(ILogger logger, int count, bool includeInactive);

        [LoggerMessage(EventId = 4508, Level = LogLevel.Error,
            Message = "Scene dataset admin operation '{Operation}' failed.")]
        public static partial void OperationFailed(ILogger logger, string operation, Exception exception);
    }
}
