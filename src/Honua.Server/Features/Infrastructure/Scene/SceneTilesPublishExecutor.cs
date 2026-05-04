// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using Honua.Core.Exceptions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Publishing.Abstractions;
using Honua.Core.Features.Publishing.Domain;
using Honua.Core.Features.Scene;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Scene.Generation;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Scene;

/// <summary>
/// Publishing executor that converts a feature layer into a deterministic
/// OGC 3D Tiles 1.1 tileset and registers it with the hosted scene registry.
/// </summary>
/// <remarks>
/// <para>
/// This is the v1 implementation of the <c>FeatureLayer → SceneService</c>
/// publishing path described in #842. The executor is intentionally
/// conservative: small/medium datasets, a single-tile output (no LOD), and a
/// hard 50 000-feature cap. Enterprise-scale tiling and streaming
/// optimization are deferred to future tickets.
/// </para>
/// <para>
/// Outputs are written under <c>{OutputRoot}/{sceneId}/</c>. The executor
/// emits one <c>tileset.json</c> plus one <c>tile_0000.glb</c> binary
/// containing the projected mesh, per-vertex feature ids, and per-feature
/// attribute table. Determinism is guaranteed by ordering features by
/// primary key, applying a stable triangulation, and serializing JSON
/// through a source-generated context with no dictionary keys.
/// </para>
/// </remarks>
internal sealed partial class SceneTilesPublishExecutor : IPublishExecutor
{
    internal const string TargetConfigSceneId = "sceneId";
    internal const string TargetConfigDisplayName = "displayName";
    internal const string TargetConfigDescription = "description";
    internal const string TargetConfigIncludeAttributes = "includeAttributes";
    internal const string TargetConfigMaxFeatureCount = "maxFeatureCount";
    internal const string TargetConfigCacheMaxAge = "cacheMaxAgeSeconds";
    internal const string TargetConfigCreatedBy = "createdBy";
    internal const string TargetConfigEditionGate = "editionGate";

    private readonly ILayerCatalog _catalog;
    private readonly ISceneFeatureSource _featureSource;
    private readonly ISceneRegistrationService? _registration;
    private readonly IHostEnvironment _environment;
    private readonly IOptions<SceneGenerationServerOptions> _options;
    private readonly ILogger<SceneTilesPublishExecutor> _logger;

    public SceneTilesPublishExecutor(
        ILayerCatalog catalog,
        ISceneFeatureSource featureSource,
        IHostEnvironment environment,
        IOptions<SceneGenerationServerOptions> options,
        ILogger<SceneTilesPublishExecutor> logger,
        ISceneRegistrationService? registration = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _featureSource = featureSource ?? throw new ArgumentNullException(nameof(featureSource));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _registration = registration;
    }

    /// <inheritdoc />
    public async Task<PublishedServiceRecord> ExecuteAsync(
        PublishIntent intent,
        CancellationToken cancellationToken = default)
    {
        var outcome = await GenerateAsync(intent, cancellationToken).ConfigureAwait(false);
        var record = PublishedServiceRecord.CreateFromIntent(
            serviceId: $"scene:{outcome.Result.SceneId}",
            intent: intent,
            endpoint: $"/scenes/{outcome.Result.SceneId}/tileset.json");
        return record with { Warnings = outcome.Result.Warnings };
    }

    /// <summary>
    /// Direct entry point used by the admin endpoint to run a generation job
    /// inline and return the rich summary in addition to the published-service
    /// record. The summary includes the resolved scene id, output path, and
    /// bounding region degrees so the response can avoid re-derivation.
    /// </summary>
    internal async Task<SceneGenerationOutcome> RunDirectAsync(
        PublishIntent intent,
        CancellationToken cancellationToken)
        => await GenerateAsync(intent, cancellationToken).ConfigureAwait(false);

    private async Task<SceneGenerationOutcome> GenerateAsync(
        PublishIntent intent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.SourceKind != PublishSourceKind.FeatureLayer)
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: SourceKind must be FeatureLayer for scene generation.");
        }

        if (intent.TargetKind != PublishTargetKind.SceneService)
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: TargetKind must be SceneService for scene generation.");
        }

        var serverOptions = _options.Value;
        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            HonuaTelemetry.Activities.TileGeneration,
            ActivityKind.Internal);
        activity?.SetTag("honua.scene.intent_id", intent.IntentId);
        activity?.SetTag("honua.scene.source_id", intent.SourceId);

        SceneGenerationLog.Started(_logger, intent.IntentId, intent.SourceId);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var generationOptions = ParseGenerationOptions(intent, serverOptions);
            var includeAttributes = generationOptions.IncludeAttributes;
            var maxFeatures = Math.Min(
                generationOptions.MaxFeatureCount,
                serverOptions.MaxFeatureCount);

            // Validate registry-bound option fields before doing any I/O. The
            // manual scene-dataset endpoint runs the same validators; mirroring
            // them here ensures a generation request fails fast with a 400 and
            // never writes a partial directory when limits are exceeded.
            ValidateRegistryBoundOptions(intent, generationOptions);

            if (!int.TryParse(intent.SourceId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId))
            {
                throw new ValidationException(
                    $"{SceneGenerationErrorCodes.LayerNotFound}: SourceId '{intent.SourceId}' is not a valid layer id.");
            }

            var layer = await _catalog.GetLayerAsync(layerId, cancellationToken).ConfigureAwait(false)
                ?? throw new ValidationException(
                    $"{SceneGenerationErrorCodes.LayerNotFound}: Layer {layerId} not found.");
            activity?.SetTag(HonuaTelemetry.Tags.LayerId, layer.Id);

            if (layer.SpatialReference.Wkid <= 0)
            {
                throw new ValidationException(
                    $"{SceneGenerationErrorCodes.LayerCrsUnknown}: Layer {layerId} has no resolvable spatial reference.");
            }

            var extrusion = generationOptions.ExtrusionOverride ?? layer.Metadata?.Extrusion;
            var attributeSchemas = BuildAttributeSchemas(layer, includeAttributes);
            var collected = await CollectFeaturesAsync(
                layer, includeAttributes, maxFeatures, cancellationToken).ConfigureAwait(false);

            var bounds = collected.Bounds;
            var minHeight = collected.MinHeight;
            var maxHeight = collected.MaxHeight;

            // Extrusion only affects polygon GLB output; for points and
            // linestrings the writer keeps the source vertex Z untouched.
            var firstKind = collected.Features[0].Geometry.Kind;
            var extrusionAffectsGlb = extrusion is not null && firstKind == SceneGeometryKind.Polygon;

            if (extrusionAffectsGlb)
            {
                // The GLB writer overrides vertex Z with baseHeight on the
                // bottom face and baseHeight + extrusionHeight on the top
                // face, so the bounding region must reflect that prism range
                // — not the source vertex Z range — or CesiumJS may cull the
                // tile prematurely when BaseHeightField is non-zero.
                var extrusionMaxTop = double.NegativeInfinity;
                var extrusionMinBase = double.PositiveInfinity;
                foreach (var feature in collected.Features)
                {
                    var baseHeight = ResolveExtrusionBase(feature, extrusion!);
                    var topZ = baseHeight + ResolveExtrusionMax(feature, extrusion!);
                    if (topZ > extrusionMaxTop) extrusionMaxTop = topZ;
                    if (baseHeight < extrusionMinBase) extrusionMinBase = baseHeight;
                }
                minHeight = extrusionMinBase;
                maxHeight = extrusionMaxTop;
            }
            else if (!collected.SawAnyHeight)
            {
                collected.Warnings.Add("Layer has no Z values and no extrusion configured; output is flat at Z=0.");
                minHeight = 0.0;
                maxHeight = 0.0;
            }

            if (double.IsPositiveInfinity(minHeight)) minHeight = 0.0;
            if (double.IsNegativeInfinity(maxHeight)) maxHeight = 0.0;

            var sceneId = ResolveSceneId(intent, layer);
            var displayName = ResolveDisplayName(intent, layer);
            ValidateDisplayName(displayName);
            var description = TryGetTargetConfig(intent, TargetConfigDescription);
            var editionGate = TryGetTargetConfig(intent, TargetConfigEditionGate);

            // Preflight registry lookup so a duplicate sceneId returns 409 BEFORE
            // we create any directory. The registry INSERT below remains the
            // canonical collision authority; this preflight closes the practical
            // overwrite window for sequential publishes against the same id.
            await PreflightSceneIdConflictAsync(sceneId, cancellationToken).ConfigureAwait(false);

            // Stage outputs under an intent-scoped directory so concurrent
            // publishes that pass the preflight (same sceneId, both running
            // before either reaches RegisterAsync) cannot overwrite each
            // other's final-path bytes. We register first — that INSERT is the
            // single canonical collision authority — and only promote the
            // winning staging directory to its final location after the
            // registry record is durable.
            var outputDirectory = ResolveOutputDirectory(serverOptions, sceneId);
            var stagingDirectory = ResolveStagingDirectory(serverOptions, intent.IntentId);
            string? stagingToCleanup = stagingDirectory;
            try
            {
                Directory.CreateDirectory(stagingDirectory);

                var glb = GeometryTileBuilder.BuildGlb(
                    collected.Features,
                    attributeSchemas,
                    extrusion,
                    serverOptions.GeneratorTag,
                    collected.Warnings);

                var tileFileName = "tile_0000.glb";
                await File.WriteAllBytesAsync(
                    Path.Combine(stagingDirectory, tileFileName),
                    glb,
                    cancellationToken).ConfigureAwait(false);

                var geometricError = ComputeGeometricError(bounds, minHeight, maxHeight);
                var tileset = TilesetDocumentWriter.Build(
                    bounds,
                    minHeight,
                    maxHeight,
                    geometricError,
                    tileContentUris: [tileFileName],
                    serverOptions.GeneratorTag);
                var tilesetBytes = TilesetDocumentWriter.Serialize(tileset);
                await File.WriteAllBytesAsync(
                    Path.Combine(stagingDirectory, "tileset.json"),
                    tilesetBytes,
                    cancellationToken).ConfigureAwait(false);

                var registeredDatasetId = await TryRegisterSceneAsync(
                    sceneId,
                    displayName,
                    description,
                    outputDirectory,
                    bounds,
                    generationOptions.CacheMaxAgeSeconds,
                    editionGate,
                    createdBy: TryGetTargetConfig(intent, TargetConfigCreatedBy) ?? "publisher",
                    cancellationToken).ConfigureAwait(false);

                // Registration succeeded — promote staging to the final scene
                // path. Directory.Move is atomic on the same filesystem, so any
                // reader that observes the directory observes the final byte
                // image. If the final path already holds detritus from a prior
                // partial run, clear it before the rename — registration was
                // the canonical authority, and the registry now points at this
                // generation. If promotion fails (permission denied, disk
                // full, stale-dir delete fails) we MUST deactivate the
                // already-inserted registry record so the serving path does
                // not resolve a record whose AssetRoot has no bytes.
                try
                {
                    PromoteStagingToFinal(stagingDirectory, outputDirectory);
                }
                catch
                {
                    if (registeredDatasetId is { } datasetId)
                    {
                        await CompensateRegistrationAsync(datasetId, sceneId).ConfigureAwait(false);
                    }
                    throw;
                }
                stagingToCleanup = null;

                stopwatch.Stop();
                activity?.SetTag(HonuaTelemetry.Tags.FeatureCount, collected.Features.Count);
                activity?.SetTag("honua.scene.tile_count", 1);
                activity?.SetTag("honua.scene.id", sceneId);
                SceneGenerationLog.Completed(_logger, intent.IntentId, sceneId, collected.Features.Count, stopwatch.ElapsedMilliseconds);

                foreach (var warning in collected.Warnings)
                {
                    SceneGenerationLog.Warning(_logger, intent.IntentId, warning);
                }

                var summary = new SceneGenerationSummary
                {
                    FeatureCount = collected.Features.Count,
                    TileCount = 1,
                    BoundingRegionDegrees = bounds,
                    GeometricError = geometricError,
                    Warnings = collected.Warnings.AsReadOnly()
                };

                return new SceneGenerationOutcome
                {
                    Result = new SceneGenerationResult
                    {
                        SceneId = sceneId,
                        AssetRoot = outputDirectory,
                        Summary = summary,
                        Warnings = collected.Warnings.AsReadOnly()
                    }
                };
            }
            finally
            {
                if (stagingToCleanup is not null)
                {
                    TryDeleteStaging(stagingToCleanup);
                }
            }
        }
        catch (ValidationException vex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, vex.Message);
            SceneGenerationLog.Failed(_logger, intent.IntentId, intent.SourceId, vex.Message);
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            SceneGenerationLog.Failed(_logger, intent.IntentId, intent.SourceId, ex.GetType().Name);
            throw new ServiceUnavailableException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: Scene generation failed; see server logs for diagnostic detail.");
        }
    }

    private async Task<CollectedFeatures> CollectFeaturesAsync(
        LayerDefinition layer,
        IReadOnlyList<string> includeAttributes,
        int maxFeatures,
        CancellationToken cancellationToken)
    {
        var collected = new CollectedFeatures();

        await foreach (var feature in _featureSource
            .StreamAsync(layer, includeAttributes, cancellationToken)
            .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (collected.Features.Count >= maxFeatures)
            {
                throw new ValidationException(
                    $"{SceneGenerationErrorCodes.FeatureLimitExceeded}: Layer {layer.Id} exceeds the {maxFeatures}-feature v1 limit.");
            }

            if (!IsSupportedKind(feature.Geometry.Kind))
            {
                throw new ValidationException(
                    $"{SceneGenerationErrorCodes.UnsupportedGeometryType}: Geometry kind '{feature.Geometry.Kind}' is not supported in v1.");
            }

            foreach (var vertex in feature.Geometry.Vertices)
            {
                if (vertex.Longitude < collected.MinLon) collected.MinLon = vertex.Longitude;
                if (vertex.Latitude < collected.MinLat) collected.MinLat = vertex.Latitude;
                if (vertex.Longitude > collected.MaxLon) collected.MaxLon = vertex.Longitude;
                if (vertex.Latitude > collected.MaxLat) collected.MaxLat = vertex.Latitude;
                if (vertex.Height is { } z)
                {
                    collected.SawAnyHeight = true;
                    if (z < collected.MinHeight) collected.MinHeight = z;
                    if (z > collected.MaxHeight) collected.MaxHeight = z;
                }
            }

            collected.Features.Add(feature);
        }

        if (collected.Features.Count == 0)
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: Layer {layer.Id} contains no features to generate.");
        }

        return collected;
    }

    private static void ValidateRegistryBoundOptions(
        PublishIntent intent,
        SceneGenerationOptions options)
    {
        var cachePolicy = new SceneCachePolicy(options.CacheMaxAgeSeconds, NoStore: false);
        if (!SceneDatasetValidator.TryValidateCachePolicy(cachePolicy, out var cacheError))
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: cacheMaxAgeSeconds is invalid: {cacheError}");
        }

        var editionGate = TryGetTargetConfig(intent, TargetConfigEditionGate);
        if (!SceneDatasetValidator.TryValidateEditionGate(editionGate, out var editionError))
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: editionGate is invalid: {editionError}");
        }
    }

    private static void ValidateDisplayName(string displayName)
    {
        if (!SceneDatasetValidator.TryValidateName(displayName, out var error))
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: displayName is invalid: {error}");
        }
    }

    private async Task PreflightSceneIdConflictAsync(string sceneId, CancellationToken cancellationToken)
    {
        if (_registration is null)
        {
            return;
        }
        var existing = await _registration.GetBySceneIdAsync(sceneId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.SceneIdConflict}: A scene dataset with id '{sceneId}' already exists.");
        }
    }

    private static SceneGenerationOptions ParseGenerationOptions(
        PublishIntent intent,
        SceneGenerationServerOptions serverOptions)
    {
        var include = TryGetTargetConfig(intent, TargetConfigIncludeAttributes);
        var includeList = string.IsNullOrEmpty(include)
            ? Array.Empty<string>()
            : include.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var maxText = TryGetTargetConfig(intent, TargetConfigMaxFeatureCount);
        var maxFeatures = serverOptions.MaxFeatureCount;
        if (!string.IsNullOrEmpty(maxText)
            && int.TryParse(maxText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMax)
            && parsedMax > 0)
        {
            maxFeatures = Math.Min(parsedMax, serverOptions.MaxFeatureCount);
        }

        var cacheText = TryGetTargetConfig(intent, TargetConfigCacheMaxAge);
        var cacheMaxAge = 3600;
        if (!string.IsNullOrEmpty(cacheText)
            && int.TryParse(cacheText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCache)
            && parsedCache >= 0)
        {
            cacheMaxAge = parsedCache;
        }

        return new SceneGenerationOptions
        {
            IncludeAttributes = includeList,
            MaxFeatureCount = maxFeatures,
            CacheMaxAgeSeconds = cacheMaxAge
        };
    }

    private static List<SceneAttributeSchema> BuildAttributeSchemas(
        LayerDefinition layer,
        IReadOnlyList<string> includeAttributes)
    {
        var allow = includeAttributes.Count == 0
            ? null
            : new HashSet<string>(includeAttributes, StringComparer.OrdinalIgnoreCase);

        var schemas = new List<SceneAttributeSchema>();
        foreach (var field in layer.AttributeFields)
        {
            if (allow is not null && !allow.Contains(field.Name))
            {
                continue;
            }

            var (schemaType, componentType) = MapFieldType(field.Type);
            if (schemaType is null)
            {
                continue;
            }

            schemas.Add(new SceneAttributeSchema
            {
                PropertyId = SanitizePropertyId(field.Name),
                FieldName = field.Name,
                SchemaType = schemaType,
                SchemaComponentType = componentType
            });
        }
        return schemas;
    }

    private static (string? Schema, string Component) MapFieldType(FieldType fieldType) => fieldType switch
    {
        FieldType.Integer => ("SCALAR", "INT32"),
        FieldType.BigInteger => ("SCALAR", "INT64"),
        FieldType.Double => ("SCALAR", "FLOAT32"),
        FieldType.Float => ("SCALAR", "FLOAT32"),
        FieldType.String => ("STRING", string.Empty),
        _ => (null, string.Empty)
    };

    private static string SanitizePropertyId(string name)
    {
        var span = name.AsSpan();
        Span<char> buffer = stackalloc char[span.Length];
        for (var i = 0; i < span.Length; i++)
        {
            var c = span[i];
            buffer[i] = char.IsLetterOrDigit(c) || c == '_' ? c : '_';
        }
        var sanitized = new string(buffer);
        if (sanitized.Length == 0 || char.IsDigit(sanitized[0]))
        {
            sanitized = "_" + sanitized;
        }
        return sanitized;
    }

    private static bool IsSupportedKind(SceneGeometryKind kind)
        => kind is SceneGeometryKind.Point or SceneGeometryKind.LineString or SceneGeometryKind.Polygon;

    private static double ResolveExtrusionMax(SceneFeature feature, LayerExtrusionInfo extrusion)
    {
        if (!feature.Attributes.TryGetValue(extrusion.HeightField, out var raw) || raw is null)
        {
            return extrusion.DefaultHeight ?? 0.0;
        }

        var value = raw switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            short s => s,
            decimal m => (double)m,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) => v,
            _ => extrusion.DefaultHeight ?? 0.0
        };

        return ConvertVerticalToMeters(value, extrusion.Unit);
    }

    private static double ResolveExtrusionBase(SceneFeature feature, LayerExtrusionInfo extrusion)
    {
        if (string.IsNullOrEmpty(extrusion.BaseHeightField))
        {
            return 0.0;
        }
        if (!feature.Attributes.TryGetValue(extrusion.BaseHeightField, out var raw) || raw is null)
        {
            return 0.0;
        }
        var value = raw switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            short s => s,
            decimal m => (double)m,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) => v,
            _ => 0.0
        };
        return ConvertVerticalToMeters(value, extrusion.Unit);
    }

    private static double ConvertVerticalToMeters(double value, string? unit)
    {
        VerticalUnits.TryNormalize(unit, out var normalized);
        return normalized switch
        {
            VerticalUnits.Feet => value * 0.3048,
            VerticalUnits.UsSurveyFeet => value * (1200.0 / 3937.0),
            _ => value
        };
    }

    private static string ResolveSceneId(PublishIntent intent, LayerDefinition layer)
    {
        var explicitId = TryGetTargetConfig(intent, TargetConfigSceneId);
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

        var suffix = intent.IntentId.Length >= 8
            ? intent.IntentId[..8]
            : intent.IntentId;
        // Reserve room for the "-{suffix}" tail so the auto-generated id
        // satisfies the registry's MaxSceneIdLength budget.
        var slugBudget = SceneDatasetValidator.MaxSceneIdLength - 1 - suffix.Length;
        var slug = SlugifyName(layer.Name, Math.Max(1, slugBudget));
        var candidate = $"{slug}-{suffix}".ToLowerInvariant();
        if (!SceneDatasetValidator.TryValidateSceneId(candidate, out var autoError))
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: derived sceneId '{candidate}' is invalid: {autoError}. Supply an explicit sceneId.");
        }
        return candidate;
    }

    private static string ResolveDisplayName(PublishIntent intent, LayerDefinition layer)
    {
        var name = TryGetTargetConfig(intent, TargetConfigDisplayName);
        return string.IsNullOrEmpty(name) ? layer.Name : name;
    }

    private static string? TryGetTargetConfig(PublishIntent intent, string key)
        => intent.TargetConfig.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value)
            ? value
            : null;

    private string ResolveOutputDirectory(SceneGenerationServerOptions serverOptions, string sceneId)
    {
        var rooted = Path.IsPathRooted(serverOptions.OutputRoot)
            ? serverOptions.OutputRoot
            : Path.Combine(_environment.ContentRootPath, serverOptions.OutputRoot);
        return Path.GetFullPath(Path.Combine(rooted, sceneId));
    }

    private string ResolveStagingDirectory(SceneGenerationServerOptions serverOptions, string intentId)
    {
        var rooted = Path.IsPathRooted(serverOptions.OutputRoot)
            ? serverOptions.OutputRoot
            : Path.Combine(_environment.ContentRootPath, serverOptions.OutputRoot);
        // Prefix with '.' so the directory name cannot collide with any valid
        // sceneId (the canonical validator forbids leading dots/hyphens).
        return Path.GetFullPath(Path.Combine(rooted, $".staging-{intentId}"));
    }

    private void PromoteStagingToFinal(string stagingDirectory, string finalDirectory)
    {
        var parentDir = Path.GetDirectoryName(finalDirectory);
        if (!string.IsNullOrEmpty(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }
        if (Directory.Exists(finalDirectory))
        {
            // Detritus from a prior partial run that did not register; the
            // canonical registry now points at the staging contents, so clear
            // the stale path before the rename.
            SceneGenerationLog.PromotionOverwroteStaleFinalDir(_logger, finalDirectory);
            Directory.Delete(finalDirectory, recursive: true);
        }
        Directory.Move(stagingDirectory, finalDirectory);
    }

    private void TryDeleteStaging(string stagingDirectory)
    {
        try
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            // Cleanup is best-effort: a failed delete leaves a hidden staging
            // dir that is harmless (it never gets served, no registry record
            // points at it). Log so operators can sweep with a janitor job if
            // it accumulates.
            SceneGenerationLog.StagingCleanupFailed(_logger, stagingDirectory, ex);
        }
    }

    private async Task<Guid?> TryRegisterSceneAsync(
        string sceneId,
        string displayName,
        string? description,
        string outputDirectory,
        double[] bounds,
        int cacheMaxAgeSeconds,
        string? editionGate,
        string createdBy,
        CancellationToken cancellationToken)
    {
        if (_registration is null)
        {
            return null;
        }

        var record = new SceneDatasetRecord
        {
            DatasetId = Guid.NewGuid(),
            Id = sceneId,
            Name = displayName,
            Description = description,
            AssetRoot = outputDirectory,
            TilesetFileName = "tileset.json",
            DatasetType = SceneDatasetType.HostedTiles,
            Extent = new SceneExtent(bounds[0], bounds[1], bounds[2], bounds[3]),
            Crs = "EPSG:4979",
            CachePolicy = new SceneCachePolicy(cacheMaxAgeSeconds, NoStore: false),
            EditionGate = editionGate,
            RequiresAuth = false,
            IsPublic = true,
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
            // Implementations are free to overwrite the supplied DatasetId
            // (Postgres regenerates Guid.Empty); use the returned record's id
            // when present so a later compensation targets the durable row.
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
        // Use CancellationToken.None so a cancelled request does not skip
        // compensation — once the row exists, leaving it Active points the
        // serving path at AssetRoot bytes that promotion never wrote.
        try
        {
            var deactivated = await _registration.DeactivateAsync(datasetId, CancellationToken.None).ConfigureAwait(false);
            SceneGenerationLog.RegistrationCompensated(_logger, sceneId, datasetId, deactivated);
        }
        catch (Exception ex)
        {
            // Compensation is best-effort: a failure leaves an Active record
            // with no bytes on disk. Log loudly so operators can clean up via
            // the admin scene CRUD path.
            SceneGenerationLog.RegistrationCompensationFailed(_logger, sceneId, datasetId, ex);
        }
    }

    private static double ComputeGeometricError(double[] bounds, double minHeight, double maxHeight)
    {
        var lonSpanRad = (bounds[2] - bounds[0]) * Math.PI / 180.0;
        var latSpanRad = (bounds[3] - bounds[1]) * Math.PI / 180.0;
        var lonMeters = Math.Abs(lonSpanRad) * EcefCoordinateTransform.WgsSemiMajorAxis
            * Math.Cos((bounds[1] + bounds[3]) * 0.5 * Math.PI / 180.0);
        var latMeters = Math.Abs(latSpanRad) * EcefCoordinateTransform.WgsSemiMajorAxis;
        var heightMeters = Math.Max(0.0, maxHeight - minHeight);
        var diagonal = Math.Sqrt(lonMeters * lonMeters + latMeters * latMeters + heightMeters * heightMeters);
        return Math.Round(diagonal, 6, MidpointRounding.AwayFromZero);
    }

    private static string SlugifyName(string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(name) || maxLength <= 0)
        {
            return "scene";
        }
        var span = name.AsSpan();
        var capacity = Math.Min(span.Length, maxLength);
        var buffer = new char[capacity];
        var written = 0;
        var lastDash = false;
        for (var i = 0; i < span.Length && written < capacity; i++)
        {
            var c = span[i];
            // Restrict to ASCII alphanumerics so the resulting slug always
            // satisfies SceneDatasetValidator's pattern. Non-ASCII letters are
            // mapped to the dash separator.
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
        if (written == 0) return "scene";
        if (lastDash) written--;
        return new string(buffer, 0, written);
    }

    private sealed class CollectedFeatures
    {
        public List<SceneFeature> Features { get; } = new();
        public List<string> Warnings { get; } = new();
        public double MinLon { get; set; } = double.PositiveInfinity;
        public double MinLat { get; set; } = double.PositiveInfinity;
        public double MaxLon { get; set; } = double.NegativeInfinity;
        public double MaxLat { get; set; } = double.NegativeInfinity;
        public double MinHeight { get; set; } = double.PositiveInfinity;
        public double MaxHeight { get; set; } = double.NegativeInfinity;
        public bool SawAnyHeight { get; set; }
        public double[] Bounds => [MinLon, MinLat, MaxLon, MaxLat];
    }

    /// <summary>
    /// Outcome returned by the executor's direct entry point, capturing both
    /// the registry-facing result and the published-service identifier
    /// derived from the generated scene id.
    /// </summary>
    internal sealed class SceneGenerationOutcome
    {
        public SceneGenerationResult Result { get; init; } = new();
    }

    /// <summary>
    /// Lightweight summary returned by the executor's direct entry-point.
    /// </summary>
    internal sealed class SceneGenerationResult
    {
        public string SceneId { get; init; } = string.Empty;
        public string AssetRoot { get; init; } = string.Empty;
        public SceneGenerationSummary Summary { get; init; } = new();
        public IReadOnlyList<string> Warnings { get; init; } = [];
    }

    internal static partial class SceneGenerationLog
    {
        [LoggerMessage(EventId = 8410, Level = LogLevel.Information,
            Message = "Scene generation started: intent {IntentId}, source {SourceId}")]
        public static partial void Started(ILogger logger, string intentId, string sourceId);

        [LoggerMessage(EventId = 8411, Level = LogLevel.Information,
            Message = "Scene generation completed: intent {IntentId}, scene {SceneId}, features {FeatureCount}, elapsed {ElapsedMs}ms")]
        public static partial void Completed(ILogger logger, string intentId, string sceneId, int featureCount, long elapsedMs);

        [LoggerMessage(EventId = 8412, Level = LogLevel.Warning,
            Message = "Scene generation failed: intent {IntentId}, source {SourceId}, reason {Reason}")]
        public static partial void Failed(ILogger logger, string intentId, string sourceId, string reason);

        [LoggerMessage(EventId = 8413, Level = LogLevel.Information,
            Message = "Scene generation warning: intent {IntentId}, message {Message}")]
        public static partial void Warning(ILogger logger, string intentId, string message);

        [LoggerMessage(EventId = 8414, Level = LogLevel.Warning,
            Message = "Scene generation overwrote stale final directory {FinalDirectory} during staging promotion; the registry record now points at the new bytes.")]
        public static partial void PromotionOverwroteStaleFinalDir(ILogger logger, string finalDirectory);

        [LoggerMessage(EventId = 8415, Level = LogLevel.Warning,
            Message = "Scene generation could not delete staging directory {StagingDirectory}; subsequent generations are unaffected but the directory may need a manual sweep.")]
        public static partial void StagingCleanupFailed(ILogger logger, string stagingDirectory, Exception exception);

        [LoggerMessage(EventId = 8416, Level = LogLevel.Warning,
            Message = "Scene generation deactivated registry record for scene {SceneId} ({DatasetId}) after staging promotion failed; deactivated={Deactivated}.")]
        public static partial void RegistrationCompensated(ILogger logger, string sceneId, Guid datasetId, bool deactivated);

        [LoggerMessage(EventId = 8417, Level = LogLevel.Error,
            Message = "Scene generation could not deactivate registry record for scene {SceneId} ({DatasetId}) after staging promotion failed; record remains Active and the operator must clean it up via the admin scene CRUD path.")]
        public static partial void RegistrationCompensationFailed(ILogger logger, string sceneId, Guid datasetId, Exception exception);
    }
}
