// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using Honua.Core.Exceptions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Scene;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Bim;
using Honua.Core.Features.Scene.Domain;
using Honua.Scene.Assets;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Scene;

/// <summary>
/// Publishing executor that converts an uploaded CityGML building document into a
/// deterministic Building Scene Layer (BSL) 3D Tiles tileset and registers it
/// with the hosted scene registry (#1207). This is the wired ingest path on top
/// of the <see cref="CityGmlReader"/> + <see cref="BuildingSceneLayerBuilder"/>
/// vertical slice: it stages the generated <c>tileset.json</c> and GLB tile,
/// registers the dataset (the canonical collision authority), then atomically
/// promotes the staged bytes to the final scene asset root so the standard scene
/// serving path can stream them.
/// </summary>
/// <remarks>
/// <para>
/// The executor reuses <see cref="SceneTilesetStaging"/> for the stage → register
/// → promote → compensate mechanics so its durability semantics match the
/// feature-layer generation path. CRS handling is intentionally conservative for
/// v1: geographic source documents (EPSG:4326/4979 or OGC CRS84, in either axis
/// order) pass through to the ellipsoid; projected documents are rejected with
/// <see cref="SceneGenerationErrorCodes.LayerCrsUnknown"/> and are a documented
/// follow-up.
/// </para>
/// </remarks>
internal sealed partial class CityGmlScenePublishExecutor
{
    private readonly IHostEnvironment _environment;
    private readonly IOptions<SceneGenerationServerOptions> _options;
    private readonly ILogger<CityGmlScenePublishExecutor> _logger;
    private readonly ISceneRegistrationService? _registration;
    private readonly ICloudFileStorage? _storage;

    public CityGmlScenePublishExecutor(
        IHostEnvironment environment,
        IOptions<SceneGenerationServerOptions> options,
        ILogger<CityGmlScenePublishExecutor> logger,
        ISceneRegistrationService? registration = null,
        ICloudFileStorage? storage = null)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _registration = registration;
        _storage = storage;
    }

    /// <summary>
    /// Parses, tiles, registers, and promotes a CityGML document into a servable
    /// Building Scene Layer scene.
    /// </summary>
    /// <param name="request">The ingest request carrying the document bytes and target metadata.</param>
    /// <param name="cancellationToken">Cancels the ingest before promotion.</param>
    /// <returns>The ingest outcome with the resolved scene id, asset root, and BSL summary.</returns>
    /// <exception cref="CityGmlFormatException">The document is malformed or carries no parseable building geometry.</exception>
    /// <exception cref="ValidationException">An option is invalid, the source CRS is unsupported, or the scene id/name collides with an existing dataset.</exception>
    public async Task<CityGmlSceneIngestOutcome> IngestAsync(
        CityGmlSceneIngestRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Document);

        var serverOptions = _options.Value;
        var intentId = Guid.NewGuid().ToString("N");

        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            HonuaTelemetry.Activities.TileGeneration,
            ActivityKind.Internal);
        activity?.SetTag("honua.scene.intent_id", intentId);
        activity?.SetTag("honua.scene.source_kind", "citygml");

        CityGmlIngestLog.Started(_logger, intentId, request.Document.Length);
        var stopwatch = Stopwatch.StartNew();

        // 1. Parse + tile before any I/O so a malformed document or unsupported
        //    CRS fails fast without creating a staging directory.
        var model = CityGmlReader.Read(request.Document);
        var transform = BuildGeoTransform(model);

        BuildingSceneLayerResult tileset;
        try
        {
            tileset = BuildingSceneLayerBuilder.Build(model, transform, serverOptions.GeneratorTag);
        }
        catch (ArgumentException ex)
        {
            // The builder rejects a model with no renderable boundary surfaces.
            // Surface it as the documented options-invalid 400 rather than a 500.
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: CityGML document produced no renderable boundary surfaces.",
                ex);
        }

        // 2. Resolve + validate the registry-bound identifiers and options.
        var sceneId = ResolveSceneId(request.SceneId, model, intentId);
        var displayName = ResolveDisplayName(request.DisplayName, model, sceneId);
        ValidateOptions(displayName, request.EditionGate, request.CacheMaxAgeSeconds);
        var cacheMaxAge = request.CacheMaxAgeSeconds ?? DefaultCacheMaxAgeSeconds;

        await PreflightSceneRegistrationConflictAsync(sceneId, cancellationToken).ConfigureAwait(false);

        // 3. Stage → register → promote, reusing the shared durability mechanics.
        var outputDirectory = SceneTilesetStaging.ResolveOutputDirectory(
            _environment.ContentRootPath, serverOptions.OutputRoot, sceneId);
        var stagingDirectory = SceneTilesetStaging.ResolveStagingDirectory(
            _environment.ContentRootPath, serverOptions.OutputRoot, intentId);
        string? stagingToCleanup = stagingDirectory;
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            await File.WriteAllBytesAsync(
                Path.Combine(stagingDirectory, "tileset.json"),
                tileset.TilesetJsonBytes,
                cancellationToken).ConfigureAwait(false);
            foreach (var (uri, bytes) in tileset.Tiles)
            {
                await File.WriteAllBytesAsync(
                    Path.Combine(stagingDirectory, uri),
                    bytes,
                    cancellationToken).ConfigureAwait(false);
            }

            // Replicate the staged tileset tree to the shared object store before
            // registering so the recorded storage prefix always implies the bytes
            // exist and any node can hydrate it (#2459, ADR-0060).
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

            var registeredDatasetId = await TryRegisterSceneAsync(
                newDatasetId,
                sceneId,
                displayName,
                request.Description,
                outputDirectory,
                storagePrefix,
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
                    stale => CityGmlIngestLog.PromotionOverwroteStaleFinalDir(_logger, stale));
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
            activity?.SetTag("honua.scene.building_count", tileset.BuildingCount);
            activity?.SetTag("honua.scene.surface_count", tileset.SurfaceCount);
            CityGmlIngestLog.Completed(
                _logger, intentId, sceneId, tileset.BuildingCount, tileset.SurfaceCount, stopwatch.ElapsedMilliseconds);

            return new CityGmlSceneIngestOutcome(
                sceneId,
                outputDirectory,
                tileset.BoundsDegrees,
                tileset.BuildingCount,
                tileset.SurfaceCount,
                tileset.Tiles.Count,
                tileset.Disciplines,
                tileset.Warnings);
        }
        finally
        {
            if (stagingToCleanup is not null)
            {
                SceneTilesetStaging.TryDeleteStaging(
                    stagingToCleanup,
                    ex => CityGmlIngestLog.StagingCleanupFailed(_logger, stagingToCleanup, ex));
            }
        }
    }

    /// <summary>Default scene cache <c>max-age</c> when the request omits one.</summary>
    private const int DefaultCacheMaxAgeSeconds = 3600;

    /// <summary>
    /// Builds the geographic projection delegate for the parsed document. v1
    /// supports geographic source CRS only (EPSG:4326/4979 or OGC CRS84); axis
    /// order is taken from the reader's <see cref="CityGmlModel.LatitudeFirst"/>
    /// flag. A projected/unknown CRS is rejected as a documented limitation.
    /// </summary>
    private static CityGmlGeoTransform BuildGeoTransform(CityGmlModel model)
    {
        if (!IsGeographic(model.SourceCrs))
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.LayerCrsUnknown}: CityGML source CRS " +
                $"'{model.SourceCrs}' is projected or unsupported. v1 BIM ingest supports " +
                "geographic coordinates only (EPSG:4326, EPSG:4979, or OGC CRS84).");
        }

        var latitudeFirst = model.LatitudeFirst;
        // Geographic pass-through: keep height (ellipsoidal meters) untouched and
        // swap the horizontal ordinates when the document declares latitude-first
        // axis order so the builder always receives (longitude, latitude, height).
        return latitudeFirst
            ? (x, y, z) => (y, x, z)
            : (x, y, z) => (x, y, z);
    }

    private static bool IsGeographic(string? srsName)
    {
        // A document with no declared CRS is assumed to already be in lon/lat
        // degrees (the inline-fixture / pre-projected case), matching the
        // builder's identity-transform unit tests.
        if (string.IsNullOrWhiteSpace(srsName))
        {
            return true;
        }

        if (srsName.Contains("CRS84", StringComparison.OrdinalIgnoreCase)
            || srsName.Contains("WGS84", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // SRID-classifier-guard allowlist (#2732): CityGML srsName geographic detection. Kept local
        // rather than routed through GeographicSridClassifier because it must also accept the
        // deprecated 3D code EPSG:4327, which the canonical axis-order list intentionally omits.
        var epsg = TryParseEpsgCode(srsName);
        return epsg is 4326 or 4979 or 4327;
    }

    private static int? TryParseEpsgCode(string srsName)
    {
        // Accept both the URN form (urn:ogc:def:crs:EPSG::4979) and the short
        // form (EPSG:4326) by taking the last colon-delimited token that parses
        // as an integer.
        var tokens = srsName.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = tokens.Length - 1; i >= 0; i--)
        {
            if (int.TryParse(tokens[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
            {
                return code;
            }
        }
        return null;
    }

    private static string ResolveSceneId(string? explicitId, CityGmlModel model, string intentId)
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
        var slugBudget = SceneDatasetValidator.MaxSceneIdLength - 1 - suffix.Length;
        var seed = model.Buildings.Count > 0
            ? (model.Buildings[0].Name ?? model.Buildings[0].Id)
            : "citygml";
        var slug = Slugify(seed, Math.Max(1, slugBudget));
        var candidate = $"{slug}-{suffix}".ToLowerInvariant();
        if (!SceneDatasetValidator.TryValidateSceneId(candidate, out var autoError))
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: derived sceneId '{candidate}' is invalid: {autoError}. Supply an explicit sceneId.");
        }
        return candidate;
    }

    private static string ResolveDisplayName(string? explicitName, CityGmlModel model, string sceneId)
    {
        if (!string.IsNullOrEmpty(explicitName))
        {
            return explicitName;
        }

        var baseName = model.Buildings.Count > 0
            ? (model.Buildings[0].Name ?? model.Buildings[0].Id)
            : "CityGML Scene";
        // Suffix with the resolved sceneId so the default name is unique by
        // construction (the registry enforces name uniqueness), mirroring the
        // feature-layer generation default.
        var suffix = $" ({sceneId})";
        var available = SceneDatasetValidator.MaxSceneNameLength - suffix.Length;
        if (available <= 0)
        {
            return sceneId;
        }
        if (baseName.Length > available)
        {
            baseName = baseName[..available];
        }
        return baseName + suffix;
    }

    private static void ValidateOptions(string displayName, string? editionGate, int? cacheMaxAgeSeconds)
    {
        if (!SceneDatasetValidator.TryValidateName(displayName, out var nameError))
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: displayName is invalid: {nameError}");
        }

        if (!SceneDatasetValidator.TryValidateEditionGate(editionGate, out var editionError))
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: editionGate is invalid: {editionError}");
        }

        if (cacheMaxAgeSeconds is { } cache)
        {
            var policy = new SceneCachePolicy(cache, NoStore: false);
            if (!SceneDatasetValidator.TryValidateCachePolicy(policy, out var cacheError))
            {
                throw new ValidationException(
                    $"{SceneGenerationErrorCodes.OptionsInvalid}: cacheMaxAgeSeconds is invalid: {cacheError}");
            }
        }
    }

    private async Task PreflightSceneRegistrationConflictAsync(string sceneId, CancellationToken cancellationToken)
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

    private async Task<Guid?> TryRegisterSceneAsync(
        Guid datasetId,
        string sceneId,
        string displayName,
        string? description,
        string outputDirectory,
        string? assetStoragePrefix,
        double[] bounds,
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
            TilesetFileName = "tileset.json",
            DatasetType = SceneDatasetType.HostedTiles,
            Extent = new SceneExtent(bounds[0], bounds[1], bounds[2], bounds[3]),
            // BSL coordinates are placed on the WGS-84 ellipsoid with ellipsoidal
            // height in meters, so the registered CRS is the 3D geographic 4979.
            Crs = "EPSG:4979",
            CachePolicy = new SceneCachePolicy(cacheMaxAgeSeconds, NoStore: false),
            EditionGate = editionGate,
            RequiresAuth = requiresAuth,
            IsPublic = !requiresAuth,
            AllowedRoles = null,
            Status = SceneDatasetStatus.Active,
            ValidationMessage = null,
            Revision = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = createdBy,
            UpdatedAt = null
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

    private async Task CompensateRegistrationAsync(Guid datasetId, string sceneId)
    {
        if (_registration is null)
        {
            return;
        }
        try
        {
            var deactivated = await _registration.DeactivateAsync(datasetId, CancellationToken.None).ConfigureAwait(false);
            CityGmlIngestLog.RegistrationCompensated(_logger, sceneId, datasetId, deactivated);
        }
        catch (Exception ex)
        {
            CityGmlIngestLog.RegistrationCompensationFailed(_logger, sceneId, datasetId, ex);
        }
    }

    private static string Slugify(string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(name) || maxLength <= 0)
        {
            return "citygml";
        }
        var span = name.AsSpan();
        var capacity = Math.Min(span.Length, maxLength);
        var buffer = new char[capacity];
        var written = 0;
        var lastDash = false;
        for (var i = 0; i < span.Length && written < capacity; i++)
        {
            var c = span[i];
            if (char.IsAsciiLetterOrDigit(c))
            {
                buffer[written++] = char.ToLowerInvariant(c);
                lastDash = false;
            }
            else if (!lastDash && written > 0)
            {
                buffer[written++] = '-';
                lastDash = true;
            }
        }
        if (written == 0) return "citygml";
        if (lastDash) written--;
        return new string(buffer, 0, written);
    }

    internal static partial class CityGmlIngestLog
    {
        [LoggerMessage(EventId = 8450, Level = LogLevel.Information,
            Message = "CityGML scene ingest started: intent {IntentId}, document {ByteCount} bytes")]
        public static partial void Started(ILogger logger, string intentId, int byteCount);

        [LoggerMessage(EventId = 8451, Level = LogLevel.Information,
            Message = "CityGML scene ingest completed: intent {IntentId}, scene {SceneId}, buildings {BuildingCount}, surfaces {SurfaceCount}, elapsed {ElapsedMs}ms")]
        public static partial void Completed(ILogger logger, string intentId, string sceneId, int buildingCount, int surfaceCount, long elapsedMs);

        [LoggerMessage(EventId = 8452, Level = LogLevel.Warning,
            Message = "CityGML scene ingest overwrote stale final directory {FinalDirectory} during staging promotion; the registry record now points at the new bytes.")]
        public static partial void PromotionOverwroteStaleFinalDir(ILogger logger, string finalDirectory);

        [LoggerMessage(EventId = 8453, Level = LogLevel.Warning,
            Message = "CityGML scene ingest could not delete staging directory {StagingDirectory}; subsequent ingests are unaffected but the directory may need a manual sweep.")]
        public static partial void StagingCleanupFailed(ILogger logger, string stagingDirectory, Exception exception);

        [LoggerMessage(EventId = 8454, Level = LogLevel.Warning,
            Message = "CityGML scene ingest deactivated registry record for scene {SceneId} ({DatasetId}) after staging promotion failed; deactivated={Deactivated}.")]
        public static partial void RegistrationCompensated(ILogger logger, string sceneId, Guid datasetId, bool deactivated);

        [LoggerMessage(EventId = 8455, Level = LogLevel.Error,
            Message = "CityGML scene ingest could not deactivate registry record for scene {SceneId} ({DatasetId}) after staging promotion failed; record remains Active and must be cleaned up via the admin scene CRUD path.")]
        public static partial void RegistrationCompensationFailed(ILogger logger, string sceneId, Guid datasetId, Exception exception);
    }
}

/// <summary>
/// Request to ingest a CityGML document into a servable Building Scene Layer
/// scene (#1207).
/// </summary>
/// <param name="Document">The CityGML document bytes (UTF-8 or BOM-prefixed).</param>
/// <param name="SceneId">Optional explicit scene slug; derived from the first building when omitted.</param>
/// <param name="DisplayName">Optional display name; derived from the first building when omitted.</param>
/// <param name="Description">Optional human-readable description.</param>
/// <param name="EditionGate">Optional edition gate slug recorded on the dataset.</param>
/// <param name="CacheMaxAgeSeconds">Optional cache <c>max-age</c>; defaults to 3600 when omitted.</param>
/// <param name="RequiresAuth">When true, the registered scene requires authentication instead of being public.</param>
/// <param name="CreatedBy">Optional audit identity for the registration record.</param>
internal sealed record CityGmlSceneIngestRequest(
    byte[] Document,
    string? SceneId = null,
    string? DisplayName = null,
    string? Description = null,
    string? EditionGate = null,
    int? CacheMaxAgeSeconds = null,
    bool RequiresAuth = false,
    string? CreatedBy = null);

/// <summary>
/// Outcome of a CityGML scene ingest: the resolved scene id, on-disk asset root,
/// and the Building Scene Layer summary surfaced to the admin response.
/// </summary>
/// <param name="SceneId">Resolved scene slug used in the tileset URL.</param>
/// <param name="AssetRoot">Final on-disk directory holding the promoted tileset.</param>
/// <param name="BoundsDegrees">Dataset envelope [west, south, east, north] in degrees.</param>
/// <param name="BuildingCount">Number of buildings ingested.</param>
/// <param name="SurfaceCount">Number of boundary-surface components emitted.</param>
/// <param name="TileCount">Number of tile content files written.</param>
/// <param name="Disciplines">Distinct BSL disciplines / sub-layers present, sorted.</param>
/// <param name="Warnings">Non-fatal warnings raised during tiling.</param>
internal sealed record CityGmlSceneIngestOutcome(
    string SceneId,
    string AssetRoot,
    double[] BoundsDegrees,
    int BuildingCount,
    int SurfaceCount,
    int TileCount,
    IReadOnlyList<string> Disciplines,
    IReadOnlyList<string> Warnings);
