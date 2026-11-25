// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Exceptions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Scene;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Honua.Scene.Assets;

namespace Honua.Infrastructure.Scene;

internal sealed record SceneRegistrationRequest
{
    public required Guid DatasetId { get; init; }

    public required string SceneId { get; init; }

    public required string DisplayName { get; init; }

    public string? Description { get; init; }

    public required string OutputDirectory { get; init; }

    public string? AssetStoragePrefix { get; init; }

    public required SceneContentKind ContentKind { get; init; }

    public required double[] Bounds { get; init; }

    public required int CacheMaxAgeSeconds { get; init; }

    public string? EditionGate { get; init; }

    public bool RequiresAuth { get; init; }

    public required string CreatedBy { get; init; }
}

internal sealed record HostedScenePublishRequest
{
    public required string ContentRootPath { get; init; }

    public required string OutputRoot { get; init; }

    public required string IntentId { get; init; }

    public required string SceneId { get; init; }

    public required string DisplayName { get; init; }

    public string? Description { get; init; }

    public required SceneContentKind ContentKind { get; init; }

    public required byte[] TilesetJsonBytes { get; init; }

    public required IReadOnlyDictionary<string, byte[]> Tiles { get; init; }

    public required double[] BoundsDegrees { get; init; }

    public required int CacheMaxAgeSeconds { get; init; }

    public string? EditionGate { get; init; }

    public bool RequiresAuth { get; init; }

    public required string CreatedBy { get; init; }

    public ISceneRegistrationService? Registration { get; init; }

    public ICloudFileStorage? Storage { get; init; }

    public Activity? Activity { get; init; }

    public Action<string>? OnOverwroteStaleFinalDir { get; init; }

    public Action<string, Exception>? OnStagingCleanupFailed { get; init; }

    public Func<Guid, string, Task>? CompensateRegistrationAsync { get; init; }
}

internal static class HostedScenePublishPipeline
{
    private readonly record struct StagedScenePaths(string StagingDirectory, string OutputDirectory);

    internal static async Task<string> StageAndPublishAsync(
        HostedScenePublishRequest request,
        CancellationToken cancellationToken)
    {
        await ScenePublishHelper.PreflightConflictAsync(request.Registration, request.SceneId, cancellationToken)
            .ConfigureAwait(false);

        var paths = new StagedScenePaths(
            SceneTilesetStaging.ResolveStagingDirectory(request.ContentRootPath, request.OutputRoot, request.IntentId),
            SceneTilesetStaging.ResolveOutputDirectory(request.ContentRootPath, request.OutputRoot, request.SceneId));
        try
        {
            await WriteStagedTilesAsync(request, paths.StagingDirectory, cancellationToken).ConfigureAwait(false);
            var registeredDatasetId = await UploadAndRegisterAsync(request, paths, cancellationToken).ConfigureAwait(false);
            await PromoteWithCompensationAsync(request, paths, registeredDatasetId).ConfigureAwait(false);
            return paths.OutputDirectory;
        }
        catch
        {
            SceneTilesetStaging.TryDeleteStaging(
                paths.StagingDirectory,
                ex => request.OnStagingCleanupFailed?.Invoke(paths.StagingDirectory, ex));
            throw;
        }
    }

    internal static async Task<Guid?> TryRegisterSceneAsync(
        ISceneRegistrationService? registration,
        SceneRegistrationRequest request,
        CancellationToken cancellationToken)
    {
        if (registration is null)
        {
            return null;
        }

        var record = BuildDatasetRecord(request);
        try
        {
            var saved = await registration.RegisterAsync(record, cancellationToken).ConfigureAwait(false);
            return saved?.DatasetId is { } id && id != Guid.Empty ? id : record.DatasetId;
        }
        catch (SceneDatasetAlreadyExistsException ex)
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.SceneRegistrationConflict}: A scene dataset with id '{request.SceneId}' or name '{request.DisplayName}' already exists.",
                ex);
        }
    }

    private static async Task WriteStagedTilesAsync(
        HostedScenePublishRequest request,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(stagingDirectory);
        // Safe: "tileset.json" and tile URIs are deterministic builder-generated names, never user paths.
        await File.WriteAllBytesAsync(
            Path.Join(stagingDirectory, "tileset.json"),
            request.TilesetJsonBytes,
            cancellationToken).ConfigureAwait(false);
        foreach (var (uri, bytes) in request.Tiles)
        {
            await File.WriteAllBytesAsync(
                Path.Join(stagingDirectory, uri),
                bytes,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<Guid?> UploadAndRegisterAsync(
        HostedScenePublishRequest request,
        StagedScenePaths paths,
        CancellationToken cancellationToken)
    {
        // Replicate staged tileset to object store before registering so any node can hydrate it (ADR-0060).
        var newDatasetId = Guid.NewGuid();
        var storagePrefix = await SceneAssetStorageUploader.UploadTreeAsync(
            request.Storage, paths.StagingDirectory, request.SceneId, cancellationToken).ConfigureAwait(false);
        if (storagePrefix is not null)
        {
            await SceneAssetHydration.WriteMarkerAsync(
                paths.StagingDirectory,
                SceneAssetHydration.BuildToken(newDatasetId, storagePrefix),
                cancellationToken).ConfigureAwait(false);
        }

        return await TryRegisterSceneAsync(
            request.Registration,
            new SceneRegistrationRequest
            {
                DatasetId = newDatasetId,
                SceneId = request.SceneId,
                DisplayName = request.DisplayName,
                Description = request.Description,
                OutputDirectory = paths.OutputDirectory,
                AssetStoragePrefix = storagePrefix,
                ContentKind = request.ContentKind,
                Bounds = request.BoundsDegrees,
                CacheMaxAgeSeconds = request.CacheMaxAgeSeconds,
                EditionGate = request.EditionGate,
                RequiresAuth = request.RequiresAuth,
                CreatedBy = request.CreatedBy,
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task PromoteWithCompensationAsync(
        HostedScenePublishRequest request,
        StagedScenePaths paths,
        Guid? registeredDatasetId)
    {
        try
        {
            SceneTilesetStaging.PromoteStagingToFinal(
                paths.StagingDirectory,
                paths.OutputDirectory,
                request.OnOverwroteStaleFinalDir);
        }
        catch (Exception ex)
        {
            request.Activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            if (registeredDatasetId is { } datasetId && request.CompensateRegistrationAsync is not null)
            {
                await request.CompensateRegistrationAsync(datasetId, request.SceneId).ConfigureAwait(false);
            }

            throw;
        }
    }

    private static SceneDatasetRecord BuildDatasetRecord(SceneRegistrationRequest request)
        => new()
        {
            DatasetId = request.DatasetId,
            Id = request.SceneId,
            Name = request.DisplayName,
            Description = request.Description,
            AssetRoot = request.OutputDirectory,
            AssetStoragePrefix = request.AssetStoragePrefix,
            TilesetFileName = "tileset.json",
            DatasetType = SceneDatasetType.HostedTiles,
            ServingFormat = SceneServingFormat.ThreeDTiles,
            ContentKind = request.ContentKind,
            Extent = new SceneExtent(request.Bounds[0], request.Bounds[1], request.Bounds[2], request.Bounds[3]),
            // Coordinates are placed on the WGS-84 ellipsoid with ellipsoidal height in meters (EPSG:4979).
            Crs = "EPSG:4979",
            CachePolicy = new SceneCachePolicy(request.CacheMaxAgeSeconds, NoStore: false),
            EditionGate = request.EditionGate,
            RequiresAuth = request.RequiresAuth,
            IsPublic = !request.RequiresAuth,
            AllowedRoles = null,
            Status = SceneDatasetStatus.Active,
            ValidationMessage = null,
            Revision = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = request.CreatedBy,
            UpdatedAt = null,
        };
}
