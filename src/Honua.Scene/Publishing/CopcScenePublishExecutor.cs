// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Exceptions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Scene;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Scene.PointCloud;
using Honua.Scene.Assets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Scene;

internal sealed partial class CopcScenePublishExecutor
{
    private const int DefaultCacheMaxAgeSeconds = 3600;

    private readonly IHostEnvironment _environment;
    private readonly IOptions<SceneGenerationServerOptions> _options;
    private readonly ILogger<CopcScenePublishExecutor> _logger;
    private readonly ISceneRegistrationService? _registration;
    private readonly ICloudFileStorage? _storage;

    public CopcScenePublishExecutor(
        IHostEnvironment environment,
        IOptions<SceneGenerationServerOptions> options,
        ILogger<CopcScenePublishExecutor> logger,
        ISceneRegistrationService? registration = null,
        ICloudFileStorage? storage = null)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _registration = registration;
        _storage = storage;
    }

    public async Task<CopcSceneIngestOutcome> IngestAsync(
        CopcSceneIngestRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Source);

        var copcInfo = LasPointCloudReader.ReadCopcInfo(request.Source);
        if (!copcInfo.TryValidateFileLength(request.Source.LongLength, out var lengthError))
        {
            throw new ValidationException($"{SceneGenerationErrorCodes.ModelAssetInvalid}: {lengthError}");
        }

        var intentId = Guid.NewGuid().ToString("N");
        var sceneId = ResolveSceneId(request.SceneId, intentId);
        var displayName = ResolveDisplayName(request.DisplayName, sceneId);
        ValidateOptions(displayName, request.EditionGate, request.CacheMaxAgeSeconds);

        await PreflightConflictAsync(sceneId, cancellationToken).ConfigureAwait(false);

        var serverOptions = _options.Value;
        var fileName = string.Create(CultureInfo.InvariantCulture, $"{sceneId}.copc.laz");
        var outputDirectory = SceneTilesetStaging.ResolveOutputDirectory(
            _environment.ContentRootPath, serverOptions.OutputRoot, sceneId);
        var stagingDirectory = SceneTilesetStaging.ResolveStagingDirectory(
            _environment.ContentRootPath, serverOptions.OutputRoot, intentId);
        string? stagingToCleanup = stagingDirectory;

        try
        {
            Directory.CreateDirectory(stagingDirectory);
            await File.WriteAllBytesAsync(Path.Join(stagingDirectory, fileName), request.Source, cancellationToken)
                .ConfigureAwait(false);

            var newDatasetId = Guid.NewGuid();
            var storagePrefix = await SceneAssetStorageUploader.UploadTreeAsync(
                _storage, stagingDirectory, sceneId, cancellationToken).ConfigureAwait(false);
            if (storagePrefix is not null)
            {
                await SceneAssetHydration.WriteMarkerAsync(
                    stagingDirectory,
                    SceneAssetHydration.BuildToken(newDatasetId, storagePrefix),
                    cancellationToken).ConfigureAwait(false);
            }

            var registeredId = await TryRegisterAsync(
                newDatasetId,
                sceneId,
                displayName,
                request.Description,
                outputDirectory,
                storagePrefix,
                fileName,
                request.CacheMaxAgeSeconds ?? DefaultCacheMaxAgeSeconds,
                request.EditionGate,
                request.RequiresAuth,
                request.CreatedBy ?? "publisher",
                cancellationToken).ConfigureAwait(false);

            try
            {
                SceneTilesetStaging.PromoteStagingToFinal(
                    stagingDirectory,
                    outputDirectory,
                    stale => Log.OverwroteStaleDir(_logger, stale));
            }
            catch
            {
                if (registeredId is { } id && _registration is not null)
                {
                    await _registration.DeactivateAsync(id, CancellationToken.None).ConfigureAwait(false);
                }
                throw;
            }

            stagingToCleanup = null;
            Log.Completed(_logger, sceneId, copcInfo.PointCount, request.Source.Length);
            return new CopcSceneIngestOutcome(
                sceneId,
                outputDirectory,
                fileName,
                copcInfo);
        }
        finally
        {
            if (stagingToCleanup is not null)
            {
                SceneTilesetStaging.TryDeleteStaging(
                    stagingToCleanup,
                    ex => Log.StagingCleanupFailed(_logger, stagingToCleanup, ex));
            }
        }
    }

    private static string ResolveSceneId(string? explicitId, string intentId)
    {
        if (!string.IsNullOrEmpty(explicitId))
        {
            var normalized = explicitId.ToLowerInvariant();
            if (!SceneDatasetValidator.TryValidateSceneId(normalized, out var error))
            {
                throw new ValidationException($"{SceneGenerationErrorCodes.OptionsInvalid}: sceneId is invalid: {error}");
            }
            return normalized;
        }

        var suffix = intentId.Length >= 8 ? intentId[..8] : intentId;
        return $"copc-{suffix}".ToLowerInvariant();
    }

    private static string ResolveDisplayName(string? explicitName, string sceneId)
        => string.IsNullOrEmpty(explicitName) ? $"COPC Point Cloud ({sceneId})" : explicitName;

    private static void ValidateOptions(string displayName, string? editionGate, int? cacheMaxAgeSeconds)
    {
        if (!SceneDatasetValidator.TryValidateName(displayName, out var nameError))
        {
            throw new ValidationException($"{SceneGenerationErrorCodes.OptionsInvalid}: displayName is invalid: {nameError}");
        }

        if (!SceneDatasetValidator.TryValidateEditionGate(editionGate, out var editionError))
        {
            throw new ValidationException($"{SceneGenerationErrorCodes.OptionsInvalid}: editionGate is invalid: {editionError}");
        }

        if (cacheMaxAgeSeconds is { } cache
            && !SceneDatasetValidator.TryValidateCachePolicy(new SceneCachePolicy(cache, NoStore: false), out var cacheError))
        {
            throw new ValidationException($"{SceneGenerationErrorCodes.OptionsInvalid}: cacheMaxAgeSeconds is invalid: {cacheError}");
        }
    }

    private async Task PreflightConflictAsync(string sceneId, CancellationToken cancellationToken)
    {
        if (_registration is null)
        {
            return;
        }

        var existing = await _registration.GetBySceneIdAsync(sceneId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.SceneRegistrationConflict}: A scene dataset with id '{sceneId}' already exists.");
        }
    }

    private async Task<Guid?> TryRegisterAsync(
        Guid datasetId,
        string sceneId,
        string displayName,
        string? description,
        string outputDirectory,
        string? assetStoragePrefix,
        string fileName,
        int cacheMaxAgeSeconds,
        string? editionGate,
        bool requiresAuth,
        string createdBy,
        CancellationToken cancellationToken)
    {
        if (_registration is null)
        {
            return null;
        }

        var record = new SceneDatasetRecord
        {
            DatasetId = datasetId,
            Id = sceneId,
            Name = displayName,
            Description = description,
            AssetRoot = outputDirectory,
            AssetStoragePrefix = assetStoragePrefix,
            TilesetFileName = fileName,
            DatasetType = SceneDatasetType.PointCloud,
            ServingFormat = SceneServingFormat.Copc,
            ContentKind = SceneContentKind.PointCloud,
            CachePolicy = new SceneCachePolicy(cacheMaxAgeSeconds, NoStore: false),
            EditionGate = editionGate,
            RequiresAuth = requiresAuth,
            IsPublic = !requiresAuth,
            Status = SceneDatasetStatus.Active,
            Revision = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = createdBy
        };

        try
        {
            var saved = await _registration.RegisterAsync(record, cancellationToken).ConfigureAwait(false);
            return saved?.DatasetId is { } id && id != Guid.Empty ? id : record.DatasetId;
        }
        catch (SceneDatasetAlreadyExistsException ex)
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.SceneRegistrationConflict}: A scene dataset with id '{sceneId}' or name '{displayName}' already exists.",
                ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 8476, Level = LogLevel.Information,
            Message = "Registered direct COPC scene {SceneId} ({PointCount} points, {ByteCount} bytes).")]
        public static partial void Completed(ILogger logger, string sceneId, ulong pointCount, int byteCount);

        [LoggerMessage(EventId = 8477, Level = LogLevel.Warning,
            Message = "COPC ingest overwrote stale final directory {FinalDirectory}.")]
        public static partial void OverwroteStaleDir(ILogger logger, string finalDirectory);

        [LoggerMessage(EventId = 8478, Level = LogLevel.Warning,
            Message = "COPC ingest could not delete staging directory {StagingDirectory}.")]
        public static partial void StagingCleanupFailed(ILogger logger, string stagingDirectory, Exception exception);
    }
}

internal sealed record CopcSceneIngestRequest(
    byte[] Source,
    string? SceneId = null,
    string? DisplayName = null,
    string? Description = null,
    string? EditionGate = null,
    int? CacheMaxAgeSeconds = null,
    bool RequiresAuth = false,
    string? CreatedBy = null);

internal sealed record CopcSceneIngestOutcome(
    string SceneId,
    string AssetRoot,
    string FileName,
    CopcInfo CopcInfo);
