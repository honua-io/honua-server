// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Scene;
using Honua.Core.Features.Scene.Domain;
using Honua.Server.Features.Admin.Models;

namespace Honua.Server.Features.Admin;

internal static partial class SceneDatasetEndpoints
{
    private const string DefaultTilesetFileName = "tileset.json";

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

        var (servingFormat, formatError) = ParseOptionalServingFormat(request.ServingFormat);
        if (formatError is not null)
        {
            error = formatError;
            return false;
        }

        var (contentKind, kindError) = ParseOptionalContentKind(request.ContentKind);
        if (kindError is not null)
        {
            error = kindError;
            return false;
        }

        var tilesetFileName = string.IsNullOrWhiteSpace(request.TilesetFileName)
            ? DefaultTilesetFileName
            : request.TilesetFileName!;
        if (servingFormat is null && SceneServingProjection.IsCopcFileName(tilesetFileName))
        {
            servingFormat = SceneServingFormat.Copc;
        }

        if (contentKind is null && servingFormat == SceneServingFormat.Copc)
        {
            contentKind = SceneContentKind.PointCloud;
        }

        record = new SceneDatasetRecord
        {
            DatasetId = Guid.NewGuid(),
            Id = request.Id,
            Name = request.Name,
            Description = NormalizeOptional(request.Description),
            AssetRoot = request.AssetRoot,
            TilesetFileName = tilesetFileName,
            DatasetType = datasetType,
            ServingFormat = servingFormat,
            ContentKind = contentKind,
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
        if (!string.IsNullOrWhiteSpace(request.DatasetType)
            && !TryParseDatasetType(request.DatasetType, out datasetType, out var datasetTypeError))
        {
            error = datasetTypeError;
            return false;
        }

        var servingFormat = existing.ServingFormat;
        if (!string.IsNullOrWhiteSpace(request.ServingFormat))
        {
            var (parsed, formatError) = ParseOptionalServingFormat(request.ServingFormat);
            if (formatError is not null)
            {
                error = formatError;
                return false;
            }

            servingFormat = parsed;
        }

        var contentKind = existing.ContentKind;
        if (!string.IsNullOrWhiteSpace(request.ContentKind))
        {
            var (parsed, kindError) = ParseOptionalContentKind(request.ContentKind);
            if (kindError is not null)
            {
                error = kindError;
                return false;
            }

            contentKind = parsed;
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
            ServingFormat = servingFormat,
            ContentKind = contentKind,
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
            case "building":
                type = SceneDatasetType.Building;
                error = string.Empty;
                return true;
            case "point_cloud":
            case "pointcloud":
                type = SceneDatasetType.PointCloud;
                error = string.Empty;
                return true;
            default:
                type = SceneDatasetType.HostedTiles;
                error = $"Unknown dataset type '{value}'. Allowed: hosted_tiles, terrain, building, point_cloud.";
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

    private static (SceneServingFormat? Value, string? Error) ParseOptionalServingFormat(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (null, null);
        }

        return SceneServingProjection.TryParseServingFormat(value, out var parsed)
            ? (parsed, null)
            : (null, $"Unknown servingFormat '{value}'. Allowed: 3d-tiles, i3s, copc, quantized-mesh, terrain-rgb.");
    }

    private static (SceneContentKind? Value, string? Error) ParseOptionalContentKind(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (null, null);
        }

        return SceneServingProjection.TryParseContentKind(value, out var parsed)
            ? (parsed, null)
            : (null, $"Unknown contentKind '{value}'. Allowed: mesh, buildings, point-cloud, terrain, vector-3d, unclassified.");
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string ResolveCreatedBy(HttpContext context)
        => context.User.Identity?.Name is { Length: > 0 } name ? name : "admin";
}
