// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Exceptions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Scene.Generation;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Scene;

internal sealed partial class ModelScenePublishExecutor
{
    private const string ModelTileFileName = "model_0000.glb";
    private const string RootTilesetFileName = "tileset.json";

    private readonly IHostEnvironment _environment;
    private readonly IOptions<SceneGenerationServerOptions> _options;
    private readonly ILogger<ModelScenePublishExecutor> _logger;
    private readonly ISceneRegistrationService? _registration;
    private readonly ICloudFileStorage? _storage;

    public ModelScenePublishExecutor(
        IHostEnvironment environment,
        IOptions<SceneGenerationServerOptions> options,
        ILogger<ModelScenePublishExecutor> logger,
        ISceneRegistrationService? registration = null,
        ICloudFileStorage? storage = null)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _registration = registration;
        _storage = storage;
    }

    public async Task<ModelSceneIngestOutcome> IngestAsync(
        ModelSceneIngestRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ModelBytes);
        ArgumentNullException.ThrowIfNull(request.Placement);

        var serverOptions = _options.Value;
        var intentId = Guid.NewGuid().ToString("N");

        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            HonuaTelemetry.Activities.TileGeneration,
            ActivityKind.Internal);
        activity?.SetTag("honua.scene.intent_id", intentId);
        activity?.SetTag("honua.scene.source_kind", "model");

        ModelIngestLog.Started(_logger, intentId, request.ModelBytes.Length);
        var stopwatch = Stopwatch.StartNew();

        // 1. Validate placement and normalize the model to self-contained GLB before any disk I/O.
        var tileTransform = GltfModelNormalizer.BuildPlacementTransform(request.Placement);
        var normalized = GltfModelNormalizer.NormalizeToGlb(
            request.ModelBytes,
            request.FileName,
            serverOptions.GeneratorTag,
            cancellationToken);

        var (boundsDegrees, minHeight, maxHeight) = GltfModelNormalizer.ComputeGeographicBounds(
            normalized.LocalBounds,
            tileTransform);
        var extent = new SceneExtent(
            boundsDegrees[0],
            boundsDegrees[1],
            boundsDegrees[2],
            boundsDegrees[3],
            minHeight,
            maxHeight);

        var seedName = Path.GetFileNameWithoutExtension(request.FileName);
        var sceneId = ScenePublishHelper.ResolveSceneId(
            request.SceneId,
            string.IsNullOrWhiteSpace(seedName) ? "model" : seedName,
            intentId);
        var displayName = ScenePublishHelper.ResolveDisplayName(
            request.DisplayName,
            string.IsNullOrWhiteSpace(seedName) ? "3D Model" : seedName,
            sceneId);
        ScenePublishHelper.ValidateCommonOptions(
            displayName,
            RootTilesetFileName,
            request.EditionGate,
            request.CacheMaxAgeSeconds,
            extent,
            request.RequiresAuth);

        var cacheMaxAge = request.CacheMaxAgeSeconds ?? ScenePublishHelper.DefaultCacheMaxAgeSeconds;
        await ScenePublishHelper.PreflightConflictAsync(_registration, sceneId, cancellationToken).ConfigureAwait(false);

        var geometricError = GeodesicError.RootGeometricError(boundsDegrees, minHeight, maxHeight);
        var region = new[]
        {
            boundsDegrees[0] * Math.PI / 180.0,
            boundsDegrees[1] * Math.PI / 180.0,
            boundsDegrees[2] * Math.PI / 180.0,
            boundsDegrees[3] * Math.PI / 180.0,
            minHeight,
            maxHeight,
        };

        var tileset = new TilesetDocument
        {
            Asset = new TilesetAsset
            {
                Version = "1.1",
                Generator = serverOptions.GeneratorTag,
            },
            GeometricError = geometricError,
            Root = new TileNode
            {
                BoundingVolume = new BoundingVolume { Region = region },
                GeometricError = geometricError,
                Refine = "ADD",
                Children =
                [
                    new TileNode
                    {
                        BoundingVolume = new BoundingVolume { Region = region },
                        GeometricError = 0.0,
                        Refine = "ADD",
                        Transform = tileTransform,
                        Content = new TileContent { Uri = ModelTileFileName },
                    },
                ],
            },
        };
        var tilesetBytes = TilesetDocumentWriter.Serialize(tileset);
        var totalBytes = (long)tilesetBytes.Length + normalized.GlbBytes.Length;

        var outputDirectory = SceneTilesetStaging.ResolveOutputDirectory(
            _environment.ContentRootPath, serverOptions.OutputRoot, sceneId);
        var stagingDirectory = SceneTilesetStaging.ResolveStagingDirectory(
            _environment.ContentRootPath, serverOptions.OutputRoot, intentId);
        string? stagingToCleanup = stagingDirectory;

        try
        {
            SceneOutputGuard.Run(() => Directory.CreateDirectory(stagingDirectory));
            await SceneOutputGuard.RunAsync(() => File.WriteAllBytesAsync(
                Path.Join(stagingDirectory, RootTilesetFileName),
                tilesetBytes,
                cancellationToken)).ConfigureAwait(false);
            await SceneOutputGuard.RunAsync(() => File.WriteAllBytesAsync(
                Path.Join(stagingDirectory, ModelTileFileName),
                normalized.GlbBytes,
                cancellationToken)).ConfigureAwait(false);

            var commit = new ScenePublishCommitContext(
                Registration: _registration,
                Storage: _storage,
                SceneId: sceneId,
                DisplayName: displayName,
                Description: request.Description,
                StagingDirectory: stagingDirectory,
                OutputDirectory: outputDirectory,
                TilesetFileName: RootTilesetFileName,
                DatasetType: SceneDatasetType.HostedTiles,
                Extent: extent,
                Crs: "EPSG:4979",
                CacheMaxAgeSeconds: cacheMaxAge,
                EditionGate: request.EditionGate,
                RequiresAuth: request.RequiresAuth,
                CreatedBy: request.CreatedBy ?? "publisher",
                OnOverwroteStaleFinalDir: stale => ModelIngestLog.PromotionOverwroteStaleFinalDir(_logger, stale),
                OnRegistrationCompensated: (id, deactivated) => ModelIngestLog.RegistrationCompensated(_logger, sceneId, id, deactivated),
                OnRegistrationCompensationFailed: (id, ex) => ModelIngestLog.RegistrationCompensationFailed(_logger, sceneId, id, ex));

            try
            {
                await ScenePublishHelper.UploadRegisterAndPromoteAsync(commit, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                throw;
            }

            stagingToCleanup = null;
            stopwatch.Stop();

            activity?.SetTag("honua.scene.id", sceneId);
            activity?.SetTag("honua.scene.model_format", normalized.SourceFormat);
            ModelIngestLog.Completed(
                _logger, intentId, sceneId, normalized.SourceFormat, totalBytes, stopwatch.ElapsedMilliseconds);

            return new ModelSceneIngestOutcome(
                sceneId,
                outputDirectory,
                normalized.SourceFormat,
                boundsDegrees,
                minHeight,
                maxHeight,
                totalBytes,
                Array.Empty<string>());
        }
        finally
        {
            if (stagingToCleanup is not null)
            {
                SceneTilesetStaging.TryDeleteStaging(
                    stagingToCleanup,
                    ex => ModelIngestLog.StagingCleanupFailed(_logger, stagingToCleanup, ex));
            }
        }
    }

    internal static partial class ModelIngestLog
    {
        [LoggerMessage(EventId = 8500, Level = LogLevel.Information,
            Message = "Model scene ingest started: intent {IntentId}, payload {ByteCount} bytes")]
        public static partial void Started(ILogger logger, string intentId, int byteCount);

        [LoggerMessage(EventId = 8501, Level = LogLevel.Information,
            Message = "Model scene ingest completed: intent {IntentId}, scene {SceneId}, format {Format}, bytes {TotalBytes}, elapsed {ElapsedMs}ms")]
        public static partial void Completed(ILogger logger, string intentId, string sceneId, string format, long totalBytes, long elapsedMs);

        [LoggerMessage(EventId = 8502, Level = LogLevel.Warning,
            Message = "Model scene ingest overwrote stale final directory {FinalDirectory} during staging promotion.")]
        public static partial void PromotionOverwroteStaleFinalDir(ILogger logger, string finalDirectory);

        [LoggerMessage(EventId = 8503, Level = LogLevel.Warning,
            Message = "Model scene ingest could not delete staging directory {StagingDirectory}.")]
        public static partial void StagingCleanupFailed(ILogger logger, string stagingDirectory, Exception exception);

        [LoggerMessage(EventId = 8504, Level = LogLevel.Warning,
            Message = "Model scene ingest deactivated registry record for scene {SceneId} ({DatasetId}) after staging promotion failed; deactivated={Deactivated}.")]
        public static partial void RegistrationCompensated(ILogger logger, string sceneId, Guid datasetId, bool deactivated);

        [LoggerMessage(EventId = 8505, Level = LogLevel.Error,
            Message = "Model scene ingest could not deactivate registry record for scene {SceneId} ({DatasetId}) after staging promotion failed.")]
        public static partial void RegistrationCompensationFailed(ILogger logger, string sceneId, Guid datasetId, Exception exception);
    }
}

internal sealed record ModelSceneIngestRequest(
    byte[] ModelBytes,
    string FileName,
    ModelPlacement Placement,
    string? SceneId = null,
    string? DisplayName = null,
    string? Description = null,
    string? EditionGate = null,
    int? CacheMaxAgeSeconds = null,
    bool RequiresAuth = false,
    string? CreatedBy = null);

internal sealed record ModelSceneIngestOutcome(
    string SceneId,
    string AssetRoot,
    string SourceFormat,
    double[] BoundsDegrees,
    double MinHeightMeters,
    double MaxHeightMeters,
    long ByteSize,
    IReadOnlyList<string> Warnings);
