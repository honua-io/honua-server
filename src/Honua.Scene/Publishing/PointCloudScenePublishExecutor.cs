// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using Honua.Core.Exceptions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Scene;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Scene.PointCloud;
using Honua.Scene.Assets;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Scene;

internal sealed partial class PointCloudScenePublishExecutor
{
    private readonly IHostEnvironment _environment;
    private readonly IOptions<SceneGenerationServerOptions> _options;
    private readonly ILogger<PointCloudScenePublishExecutor> _logger;
    private readonly ISceneRegistrationService? _registration;
    private readonly ICloudFileStorage? _storage;

    public PointCloudScenePublishExecutor(
        IHostEnvironment environment,
        IOptions<SceneGenerationServerOptions> options,
        ILogger<PointCloudScenePublishExecutor> logger,
        ISceneRegistrationService? registration = null,
        ICloudFileStorage? storage = null)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _registration = registration;
        _storage = storage;
    }

    public async Task<PointCloudSceneIngestOutcome> IngestAsync(
        PointCloudSceneIngestRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Source);

        var serverOptions = _options.Value;
        var intentId = Guid.NewGuid().ToString("N");

        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            HonuaTelemetry.Activities.TileGeneration,
            ActivityKind.Internal);
        activity?.SetTag("honua.scene.intent_id", intentId);
        activity?.SetTag("honua.scene.source_kind", "pointcloud");

        PointCloudIngestLog.Started(_logger, intentId, request.Source.Length);
        var stopwatch = Stopwatch.StartNew();

        // 1. Decode + tile before any I/O so a malformed buffer, an unsupported
        //    (LAZ/COPC) format, or an unsupported CRS fails fast without ever
        //    creating a staging directory. The reader throws LasFormatException
        //    for compressed/unsupported inputs; the transform throws a
        //    ValidationException for projected/unknown CRS.
        var header = LasPointCloudReader.ReadHeader(request.Source);
        var transform = BuildGeoTransform(request.SourceCrs);
        var points = LasPointCloudReader.ReadPoints(request.Source, header).ToList();
        if (points.Count == 0)
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: LAS point cloud contains no points to ingest.");
        }

        PointCloudTilesetResult tileset;
        try
        {
            tileset = PointCloudTilesetBuilder.Build(
                points,
                transform,
                BuildTilingOptions(serverOptions),
                serverOptions.GeneratorTag);
        }
        catch (LasFormatException)
        {
            // The builder enforces the same stable scene-error contract as the
            // reader (e.g. the total-point cap); surface it unchanged so the
            // endpoint maps it to a problem-detail.
            throw;
        }
        catch (ArgumentException ex)
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: LAS point cloud produced no renderable points.",
                ex);
        }

        // 2. Resolve + validate the registry-bound identifiers and options.
        var sceneId = ResolveSceneId(request.SceneId, intentId);
        var displayName = ResolveDisplayName(request.DisplayName, sceneId);
        CityGmlScenePublishExecutor.ValidateOptions(displayName, request.EditionGate, request.CacheMaxAgeSeconds);
        var cacheMaxAge = request.CacheMaxAgeSeconds ?? ScenePublishHelper.DefaultCacheMaxAgeSeconds;

        await ScenePublishHelper.PreflightConflictAsync(_registration, sceneId, cancellationToken).ConfigureAwait(false);

        // 3. Stage → register → promote, reusing the shared durability mechanics.
        var outputDirectory = SceneTilesetStaging.ResolveOutputDirectory(
            _environment.ContentRootPath, serverOptions.OutputRoot, sceneId);
        var stagingDirectory = SceneTilesetStaging.ResolveStagingDirectory(
            _environment.ContentRootPath, serverOptions.OutputRoot, intentId);
        string? stagingToCleanup = stagingDirectory;
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            // Safe: "tileset.json" is a hardcoded literal, and tileset.Tiles' uri keys
            // are deterministic "points_NNNN.pnts" names built from the quadtree node
            // index by PointCloudTilesetBuilder — neither is derived from the uploaded
            // LAS point data, so neither combine can be rooted.
            await File.WriteAllBytesAsync(
                Path.Join(stagingDirectory, "tileset.json"),
                tileset.TilesetJsonBytes,
                cancellationToken).ConfigureAwait(false);
            foreach (var (uri, bytes) in tileset.Tiles)
            {
                await File.WriteAllBytesAsync(
                    Path.Join(stagingDirectory, uri),
                    bytes,
                    cancellationToken).ConfigureAwait(false);
            }

            // Replicate the staged point-cloud tileset to the shared object store
            // before registering so the recorded storage prefix always implies the
            // bytes exist and any node can hydrate it (ADR-0060).
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

            var registeredDatasetId = await CityGmlScenePublishExecutor.TryRegisterSceneAsync(
                _registration,
                newDatasetId,
                sceneId,
                displayName,
                request.Description,
                outputDirectory,
                storagePrefix,
                SceneContentKind.PointCloud,
                tileset.BoundsDegrees,
                cacheMaxAge,
                request.EditionGate,
                request.RequiresAuth,
                request.CreatedBy ?? "publisher",
                cancellationToken).ConfigureAwait(false);

            try
            {
                SceneTilesetStaging.PromoteStagingToFinal(
                    stagingDirectory,
                    outputDirectory,
                    stale => PointCloudIngestLog.PromotionOverwroteStaleFinalDir(_logger, stale));
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                if (registeredDatasetId is { } datasetId)
                {
                    await CompensateRegistrationAsync(datasetId, sceneId).ConfigureAwait(false);
                }
                throw;
            }
            stagingToCleanup = null;

            stopwatch.Stop();
            activity?.SetTag("honua.scene.id", sceneId);
            activity?.SetTag("honua.scene.point_count", tileset.PointCount);
            activity?.SetTag("honua.scene.tile_count", tileset.TileCount);
            PointCloudIngestLog.Completed(
                _logger, intentId, sceneId, tileset.PointCount, tileset.TileCount, stopwatch.ElapsedMilliseconds);

            return new PointCloudSceneIngestOutcome(
                sceneId,
                outputDirectory,
                tileset.BoundsDegrees,
                tileset.PointCount,
                tileset.TileCount,
                header.HasColor);
        }
        finally
        {
            if (stagingToCleanup is not null)
            {
                SceneTilesetStaging.TryDeleteStaging(
                    stagingToCleanup,
                    ex => PointCloudIngestLog.StagingCleanupFailed(_logger, stagingToCleanup, ex));
            }
        }
    }

    private static PointCloudTilingOptions BuildTilingOptions(SceneGenerationServerOptions serverOptions)
        => new()
        {
            MaxPointsPerTile = serverOptions.MaxFeaturesPerTile,
            MaxDepth = serverOptions.MaxLodDepth,
            InteriorSampleCount = serverOptions.InteriorSampleCount
        };

    private static PointCloudGeoTransform BuildGeoTransform(PointCloudSourceCrs sourceCrs)
    {
        // Geographic pass-through: keep height (ellipsoidal meters) untouched and
        // swap the horizontal ordinates when the request declares latitude-first
        // axis order so the builder always receives (longitude, latitude, height).
        return sourceCrs switch
        {
            PointCloudSourceCrs.GeographicLonLat => static (x, y, z) => (x, y, z),
            PointCloudSourceCrs.GeographicLatLon => static (x, y, z) => (y, x, z),
            _ => throw new ValidationException(
                $"{SceneGenerationErrorCodes.LayerCrsUnknown}: point-cloud source CRS '{sourceCrs}' is " +
                "projected or unsupported. v1 point-cloud ingest supports geographic coordinates only " +
                "(EPSG:4326, EPSG:4979, or OGC CRS84). Reproject the LAS to geographic coordinates before ingest."),
        };
    }

    private static string ResolveSceneId(string? explicitId, string intentId)
    {
        if (!string.IsNullOrEmpty(explicitId))
        {
            var normalized = explicitId.ToLowerInvariant();
            if (!SceneDatasetValidator.TryValidateSceneId(normalized, out var error))
            {
                throw new ValidationException(
                    $"{SceneGenerationErrorCodes.OptionsInvalid}: sceneId is invalid: {error}");
            }
            return normalized;
        }

        var suffix = intentId.Length >= 8 ? intentId[..8] : intentId;
        var candidate = $"pointcloud-{suffix}".ToLowerInvariant();
        if (!SceneDatasetValidator.TryValidateSceneId(candidate, out var autoError))
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: derived sceneId '{candidate}' is invalid: {autoError}. Supply an explicit sceneId.");
        }
        return candidate;
    }

    private static string ResolveDisplayName(string? explicitName, string sceneId)
    {
        if (!string.IsNullOrEmpty(explicitName))
        {
            return explicitName;
        }

        // Suffix with the resolved sceneId so the default name is unique by
        // construction (the registry enforces name uniqueness), mirroring the
        // feature-layer and CityGML generation defaults.
        const string baseName = "Point Cloud";
        var suffix = $" ({sceneId})";
        var available = SceneDatasetValidator.MaxSceneNameLength - suffix.Length;
        if (available <= 0)
        {
            return sceneId;
        }
        var trimmed = baseName.Length > available ? baseName[..available] : baseName;
        return trimmed + suffix;
    }

    private async Task CompensateRegistrationAsync(Guid datasetId, string sceneId)
    {
        if (_registration is null)
        {
            return;
        }
        try
        {
            var deactivated = await _registration.DeactivateAsync(datasetId, CancellationToken.None).ConfigureAwait(false);
            PointCloudIngestLog.RegistrationCompensated(_logger, sceneId, datasetId, deactivated);
        }
        // Publish-pipeline compensation boundary: this is a best-effort rollback of a
        // partially-completed publish, already logged below; any exception here must
        // not mask the original failure that triggered compensation.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PointCloudIngestLog.RegistrationCompensationFailed(_logger, sceneId, datasetId, ex);
        }
    }

    internal static partial class PointCloudIngestLog
    {
        [LoggerMessage(EventId = 8470, Level = LogLevel.Information,
            Message = "Point-cloud scene ingest started: intent {IntentId}, document {ByteCount} bytes")]
        public static partial void Started(ILogger logger, string intentId, int byteCount);

        [LoggerMessage(EventId = 8471, Level = LogLevel.Information,
            Message = "Point-cloud scene ingest completed: intent {IntentId}, scene {SceneId}, points {PointCount}, tiles {TileCount}, elapsed {ElapsedMs}ms")]
        public static partial void Completed(ILogger logger, string intentId, string sceneId, int pointCount, int tileCount, long elapsedMs);

        [LoggerMessage(EventId = 8472, Level = LogLevel.Warning,
            Message = "Point-cloud scene ingest overwrote stale final directory {FinalDirectory} during staging promotion; the registry record now points at the new bytes.")]
        public static partial void PromotionOverwroteStaleFinalDir(ILogger logger, string finalDirectory);

        [LoggerMessage(EventId = 8473, Level = LogLevel.Warning,
            Message = "Point-cloud scene ingest could not delete staging directory {StagingDirectory}; subsequent ingests are unaffected but the directory may need a manual sweep.")]
        public static partial void StagingCleanupFailed(ILogger logger, string stagingDirectory, Exception exception);

        [LoggerMessage(EventId = 8474, Level = LogLevel.Warning,
            Message = "Point-cloud scene ingest deactivated registry record for scene {SceneId} ({DatasetId}) after staging promotion failed; deactivated={Deactivated}.")]
        public static partial void RegistrationCompensated(ILogger logger, string sceneId, Guid datasetId, bool deactivated);

        [LoggerMessage(EventId = 8475, Level = LogLevel.Error,
            Message = "Point-cloud scene ingest could not deactivate registry record for scene {SceneId} ({DatasetId}) after staging promotion failed; record remains Active and must be cleaned up via the admin scene CRUD path.")]
        public static partial void RegistrationCompensationFailed(ILogger logger, string sceneId, Guid datasetId, Exception exception);
    }
}

internal enum PointCloudSourceCrs
{
    GeographicLonLat = 0,

    GeographicLatLon = 1,
}

internal sealed record PointCloudSceneIngestRequest(
    byte[] Source,
    PointCloudSourceCrs SourceCrs = PointCloudSourceCrs.GeographicLonLat,
    string? SceneId = null,
    string? DisplayName = null,
    string? Description = null,
    string? EditionGate = null,
    int? CacheMaxAgeSeconds = null,
    bool RequiresAuth = false,
    string? CreatedBy = null);

internal sealed record PointCloudSceneIngestOutcome(
    string SceneId,
    string AssetRoot,
    double[] BoundsDegrees,
    int PointCount,
    int TileCount,
    bool HasColor);
