// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using Honua.Core.Exceptions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Publishing.Abstractions;
using Honua.Core.Features.Publishing.Domain;
using Honua.Core.Features.Scene;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Scene.Generation;
using Honua.Core.Features.Security.Domain;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Scene;

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

    private readonly ISceneFeatureSource _featureSource;
    private readonly ISceneRegistrationService? _registration;
    private readonly IMetadataV2GraphProvider _metadataProvider;
    private readonly IHostEnvironment _environment;
    private readonly IOptions<SceneGenerationServerOptions> _options;
    private readonly ILogger<SceneTilesPublishExecutor> _logger;

    public SceneTilesPublishExecutor(
        ISceneFeatureSource featureSource,
        IMetadataV2GraphProvider metadataProvider,
        IHostEnvironment environment,
        IOptions<SceneGenerationServerOptions> options,
        ILogger<SceneTilesPublishExecutor> logger,
        ISceneRegistrationService? registration = null)
    {
        _featureSource = featureSource ?? throw new ArgumentNullException(nameof(featureSource));
        _metadataProvider = metadataProvider ?? throw new ArgumentNullException(nameof(metadataProvider));
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

            var (resource, storageLayerId) = await ResolveSourceResourceAsync(layerId, cancellationToken)
                .ConfigureAwait(false);
            activity?.SetTag(HonuaTelemetry.Tags.LayerId, storageLayerId);

            if (resource.ReadSrid() is not > 0)
            {
                throw new ValidationException(
                    $"{SceneGenerationErrorCodes.LayerCrsUnknown}: Layer {layerId} has no resolvable spatial reference.");
            }

            // Resolve and validate sceneId / displayName / preflight conflict
            // BEFORE streaming features. An invalid sceneId or duplicate
            // registration would otherwise drag a full 50k-feature page
            // through PostGIS only to fail with 400/409 — wasted DB work, and
            // worse, the project rule is to reject identifiers before any
            // provider call. The bounds-dependent extrusion math still runs
            // after collection because it needs per-feature Z values.
            var sceneId = ResolveSceneId(intent, resource);
            var displayName = ResolveDisplayName(intent, resource, sceneId);
            ValidateDisplayName(displayName);
            var description = TryGetTargetConfig(intent, TargetConfigDescription);
            var editionGate = TryGetTargetConfig(intent, TargetConfigEditionGate);

            // Preflight registry lookup so a duplicate sceneId returns 409 BEFORE
            // we create any directory or stream a single feature. The registry
            // INSERT below remains the canonical collision authority; this
            // preflight closes the practical overwrite window for sequential
            // publishes against the same id.
            await PreflightSceneRegistrationConflictAsync(sceneId, cancellationToken).ConfigureAwait(false);

            var extrusion = generationOptions.ExtrusionOverride ?? resource.Extrusion;
            var symbology = resource.Symbology3D;
            var attributeSchemas = BuildAttributeSchemas(resource, includeAttributes);
            var collected = await CollectFeaturesAsync(
                resource, storageLayerId, includeAttributes, maxFeatures, cancellationToken).ConfigureAwait(false);

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
                // face. The bounding region must enclose both faces. The
                // extrusion height value is signed: AEC and infrastructure
                // workflows use negative values for downward extrusions
                // (basements, underground utilities), so the prism's "top"
                // face can be below its "base". Take min/max over BOTH
                // baseHeight and topZ per feature; using topZ alone for max
                // and baseHeight alone for min would underestimate the
                // region for downward extrusions and let CesiumJS cull the
                // tile prematurely.
                var extrusionMaxZ = double.NegativeInfinity;
                var extrusionMinZ = double.PositiveInfinity;
                foreach (var feature in collected.Features)
                {
                    var baseHeight = ResolveExtrusionBase(feature, extrusion!);
                    var topZ = baseHeight + ResolveExtrusionMax(feature, extrusion!);
                    var featureMin = Math.Min(baseHeight, topZ);
                    var featureMax = Math.Max(baseHeight, topZ);
                    if (featureMax > extrusionMaxZ) extrusionMaxZ = featureMax;
                    if (featureMin < extrusionMinZ) extrusionMinZ = featureMin;
                }
                minHeight = extrusionMinZ;
                maxHeight = extrusionMaxZ;
            }
            else if (!collected.SawAnyHeight)
            {
                collected.Warnings.Add("Layer has no Z values and no extrusion configured; output is flat at Z=0.");
                minHeight = 0.0;
                maxHeight = 0.0;
            }

            if (double.IsPositiveInfinity(minHeight)) minHeight = 0.0;
            if (double.IsNegativeInfinity(maxHeight)) maxHeight = 0.0;

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

                // Resolve attribute-driven 3D symbology per feature (when a
                // layer declares it) so the GLB carries baked color/opacity and
                // the tileset advertises the matching style-metadata contract.
                // Resolution is parallel to collected.Features and is null when
                // the layer has no symbology (legacy white-material output).
                var perFeatureSymbology = ResolvePerFeatureSymbology(symbology, collected.Features);

                // Emit the style-metadata contract sidecar and advertise it from
                // the tileset's extras so the JS client can apply the
                // attribute-driven 3D Tiles Styling expressions at runtime. The
                // sidecar is shared across all tiles in an LOD hierarchy.
                TilesetStyleReference? styleReference = null;
                if (symbology is not null)
                {
                    var styleSpec = TileStyleSpecWriter.Build(symbology, attributeSchemas);
                    var styleBytes = TileStyleSpecWriter.Serialize(styleSpec);
                    await File.WriteAllBytesAsync(
                        Path.Combine(stagingDirectory, TileStyleSpecDefaults.FileName),
                        styleBytes,
                        cancellationToken).ConfigureAwait(false);
                    styleReference = new TilesetStyleReference
                    {
                        Encoding = TileStyleSpecDefaults.Encoding,
                        Version = TileStyleSpecDefaults.Version,
                        Uri = TileStyleSpecDefaults.FileName
                    };
                }

                var geometricError = ComputeGeometricError(bounds, minHeight, maxHeight);

                // Choose single-tile (byte-stable v1 layout) or quadtree LOD
                // partitioning (#1200). The LOD path activates once the dataset
                // crosses the configured threshold so existing small/medium
                // datasets keep their deterministic single-tile output.
                int tileCount;
                if (collected.Features.Count >= serverOptions.LodFeatureThreshold)
                {
                    tileCount = await WriteLodTilesetAsync(
                        collected.Features,
                        attributeSchemas,
                        extrusion,
                        symbology,
                        bounds,
                        geometricError,
                        serverOptions,
                        styleReference,
                        stagingDirectory,
                        collected.Warnings,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var glb = GeometryTileBuilder.BuildGlb(
                        collected.Features,
                        attributeSchemas,
                        extrusion,
                        serverOptions.GeneratorTag,
                        collected.Warnings,
                        perFeatureSymbology);

                    var tileFileName = "tile_0000.glb";
                    await File.WriteAllBytesAsync(
                        Path.Combine(stagingDirectory, tileFileName),
                        glb,
                        cancellationToken).ConfigureAwait(false);

                    var tileset = TilesetDocumentWriter.Build(
                        bounds,
                        minHeight,
                        maxHeight,
                        geometricError,
                        tileContentUris: [tileFileName],
                        serverOptions.GeneratorTag,
                        styleReference);
                    var tilesetBytes = TilesetDocumentWriter.Serialize(tileset);
                    await File.WriteAllBytesAsync(
                        Path.Combine(stagingDirectory, "tileset.json"),
                        tilesetBytes,
                        cancellationToken).ConfigureAwait(false);
                    tileCount = 1;
                }

                var registeredDatasetId = await TryRegisterSceneAsync(
                    sceneId,
                    displayName,
                    description,
                    outputDirectory,
                    bounds,
                    generationOptions.CacheMaxAgeSeconds,
                    editionGate,
                    resource.AccessPolicy,
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
                activity?.SetTag("honua.scene.tile_count", tileCount);
                activity?.SetTag("honua.scene.id", sceneId);
                SceneGenerationLog.Completed(_logger, intent.IntentId, sceneId, collected.Features.Count, stopwatch.ElapsedMilliseconds);

                foreach (var warning in collected.Warnings)
                {
                    SceneGenerationLog.Warning(_logger, intent.IntentId, warning);
                }

                var summary = new SceneGenerationSummary
                {
                    FeatureCount = collected.Features.Count,
                    TileCount = tileCount,
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
            // Use a dedicated event id with an Exception parameter so the
            // server log captures the full stack trace and message; the
            // existing 8412 Failed event takes a string reason and would
            // otherwise drop everything except the exception type name.
            SceneGenerationLog.FailedUnexpected(_logger, intent.IntentId, intent.SourceId, ex);
            // Preserve the inner exception on the wrapping
            // ServiceUnavailableException so any catch site that inspects
            // .InnerException (or just calls ToString) still sees the
            // diagnostic detail; the endpoint sanitizes the response body.
            throw new ServiceUnavailableException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: Scene generation failed; see server logs for diagnostic detail.",
                ex);
        }
    }

    /// <summary>
    /// Partitions the collected features into a quadtree LOD hierarchy (#1200),
    /// writes one GLB per content-bearing node, and emits the multi-level
    /// <c>tileset.json</c> tree. Node content uris are assigned in a stable
    /// pre-order walk (<c>tile_0000.glb</c>, <c>tile_0001.glb</c>, ...) so the
    /// output is byte-identical across runs for identical input. Returns the
    /// number of GLB tiles written.
    /// </summary>
    private static async Task<int> WriteLodTilesetAsync(
        IReadOnlyList<SceneFeature> features,
        IReadOnlyList<SceneAttributeSchema> attributeSchemas,
        MetadataV2ExtrusionInfo? extrusion,
        Symbology3D? symbology,
        double[] bounds,
        double rootGeometricError,
        SceneGenerationServerOptions serverOptions,
        TilesetStyleReference? styleReference,
        string stagingDirectory,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var lodOptions = new SceneLodOptions
        {
            MaxFeaturesPerTile = serverOptions.MaxFeaturesPerTile,
            MaxDepth = serverOptions.MaxLodDepth,
            InteriorSampleCount = serverOptions.InteriorSampleCount
        };

        var tree = SceneQuadtreePartitioner.Partition(features, bounds, rootGeometricError, lodOptions);

        // Pre-order walk: collect content-bearing nodes in a stable order so
        // both the uri assignment and the GLB write order are deterministic.
        var contentNodes = new List<SceneTileNode>(tree.ContentNodeCount);
        CollectContentNodes(tree.Root, contentNodes);

        var content = new Dictionary<SceneTileNode, SceneTileContent>(contentNodes.Count);
        for (var i = 0; i < contentNodes.Count; i++)
        {
            var node = contentNodes[i];
            var perFeatureSymbology = ResolvePerFeatureSymbology(symbology, node.Features);
            var glb = GeometryTileBuilder.BuildGlb(
                node.Features,
                attributeSchemas,
                extrusion,
                serverOptions.GeneratorTag,
                warnings,
                perFeatureSymbology);

            var uri = string.Create(
                CultureInfo.InvariantCulture,
                $"tile_{i:0000}.glb");
            await File.WriteAllBytesAsync(
                Path.Combine(stagingDirectory, uri),
                glb,
                cancellationToken).ConfigureAwait(false);

            var (nodeMin, nodeMax) = ComputeNodeHeightExtent(node.Features, extrusion);
            content[node] = new SceneTileContent(uri, nodeMin, nodeMax);
        }

        var tileset = TilesetDocumentWriter.BuildTree(
            tree,
            content,
            serverOptions.GeneratorTag,
            styleReference);
        var tilesetBytes = TilesetDocumentWriter.Serialize(tileset);
        await File.WriteAllBytesAsync(
            Path.Combine(stagingDirectory, "tileset.json"),
            tilesetBytes,
            cancellationToken).ConfigureAwait(false);

        return contentNodes.Count;
    }

    private static void CollectContentNodes(SceneTileNode node, List<SceneTileNode> sink)
    {
        if (node.Features.Count > 0)
        {
            sink.Add(node);
        }
        foreach (var child in node.Children)
        {
            CollectContentNodes(child, sink);
        }
    }

    /// <summary>
    /// Computes a node's vertical extent (meters) for its bounding region.
    /// Mirrors the extrusion-aware min/max-Z accounting used for the whole
    /// dataset so each LOD tile's region encloses both prism faces.
    /// </summary>
    private static (double Min, double Max) ComputeNodeHeightExtent(
        IReadOnlyList<SceneFeature> features,
        MetadataV2ExtrusionInfo? extrusion)
    {
        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        var sawAny = false;

        foreach (var feature in features)
        {
            if (extrusion is not null && feature.Geometry.Kind == SceneGeometryKind.Polygon)
            {
                var baseHeight = ResolveExtrusionBase(feature, extrusion);
                var topZ = baseHeight + ResolveExtrusionMax(feature, extrusion);
                var fMin = Math.Min(baseHeight, topZ);
                var fMax = Math.Max(baseHeight, topZ);
                if (fMax > max) max = fMax;
                if (fMin < min) min = fMin;
                sawAny = true;
                continue;
            }

            foreach (var vertex in feature.Geometry.Vertices)
            {
                if (vertex.Height is { } z)
                {
                    sawAny = true;
                    if (z < min) min = z;
                    if (z > max) max = z;
                }
            }
        }

        if (!sawAny)
        {
            return (0.0, 0.0);
        }
        if (double.IsPositiveInfinity(min)) min = 0.0;
        if (double.IsNegativeInfinity(max)) max = 0.0;
        return (min, max);
    }

    private async Task<CollectedFeatures> CollectFeaturesAsync(
        MetadataV2Resource resource,
        int storageLayerId,
        IReadOnlyList<string> includeAttributes,
        int maxFeatures,
        CancellationToken cancellationToken)
    {
        var collected = new CollectedFeatures();
        SceneGeometryKind? expectedKind = null;
        var nonDegenerateCount = 0;

        await foreach (var feature in _featureSource
            .StreamAsync(resource, storageLayerId, includeAttributes, cancellationToken)
            .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (collected.Features.Count >= maxFeatures)
            {
                throw new ValidationException(
                    $"{SceneGenerationErrorCodes.FeatureLimitExceeded}: Layer {storageLayerId} exceeds the {maxFeatures}-feature v1 limit.");
            }

            if (!IsSupportedKind(feature.Geometry.Kind))
            {
                throw new ValidationException(
                    $"{SceneGenerationErrorCodes.UnsupportedGeometryType}: Geometry kind '{feature.Geometry.Kind}' is not supported in v1.");
            }

            // GeometryTileBuilder.BuildGlb requires every feature in a tile to
            // share a single SceneGeometryKind. The mixed-kind invariant is
            // enforced here so callers see SCENE_UNSUPPORTED_GEOMETRY_TYPE / 400
            // instead of the catch-all 503 wrapping an ArgumentException from
            // the build phase.
            if (expectedKind is null)
            {
                expectedKind = feature.Geometry.Kind;
            }
            else if (feature.Geometry.Kind != expectedKind.Value)
            {
                throw new ValidationException(
                    $"{SceneGenerationErrorCodes.UnsupportedGeometryType}: Layer {storageLayerId} mixes geometry kinds " +
                    $"('{expectedKind.Value}' and '{feature.Geometry.Kind}'); v1 requires a single kind per layer.");
            }

            if (WillProduceVertices(feature))
            {
                nonDegenerateCount++;
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
                $"{SceneGenerationErrorCodes.OptionsInvalid}: Layer {storageLayerId} contains no features to generate.");
        }

        // GeometryTileBuilder.BuildGlb throws InvalidOperationException("No
        // vertices produced for tile.") when every feature is degenerate
        // (e.g. polygons collapse to <3 distinct ring vertices, lines have
        // <2 vertices). Pre-validate here so the failure surfaces as the
        // documented SCENE_OPTIONS_INVALID / 400 instead of a 503.
        if (nonDegenerateCount == 0)
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: Layer {storageLayerId} contains only degenerate features; " +
                "no feature has enough vertices to produce geometry.");
        }

        return collected;
    }

    /// <summary>
    /// Mirrors <see cref="GeometryTileBuilder"/>'s per-kind vertex-emission
    /// criteria so the executor can short-circuit the all-degenerate case
    /// with a 400 instead of letting the build phase throw.
    /// </summary>
    private static bool WillProduceVertices(SceneFeature feature)
    {
        var vertices = feature.Geometry.Vertices;
        return feature.Geometry.Kind switch
        {
            SceneGeometryKind.Point => vertices.Count >= 1,
            SceneGeometryKind.LineString => vertices.Count >= 2,
            SceneGeometryKind.Polygon => CountDistinctRingVertices(vertices) >= 3,
            _ => false
        };
    }

    /// <summary>
    /// Returns the polygon ring's vertex count, dropping a duplicate closing
    /// vertex if the ring is closed — matching <see cref="GeometryTileBuilder"/>'s
    /// internal accounting so the executor's degeneracy check stays in sync
    /// with the build phase's silent-skip threshold.
    /// </summary>
    private static int CountDistinctRingVertices(IReadOnlyList<SceneVertex> ring)
    {
        if (ring.Count < 3)
        {
            return ring.Count;
        }
        var first = ring[0];
        var last = ring[ring.Count - 1];
        if (first.Longitude == last.Longitude
            && first.Latitude == last.Latitude
            && (first.Height ?? 0.0) == (last.Height ?? 0.0))
        {
            return ring.Count - 1;
        }
        return ring.Count;
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
        if (!string.IsNullOrEmpty(maxText))
        {
            // An explicit value that does not parse, is non-positive, or
            // exceeds reasonable bounds must surface as SCENE_OPTIONS_INVALID
            // rather than silently fall back to the server default — the
            // caller asked for a specific cap and getting the server cap
            // instead would produce a tileset that does not honor the
            // request contract.
            if (!int.TryParse(maxText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMax)
                || parsedMax <= 0)
            {
                throw new ValidationException(
                    $"{SceneGenerationErrorCodes.OptionsInvalid}: maxFeatureCount must be a positive integer.");
            }
            maxFeatures = Math.Min(parsedMax, serverOptions.MaxFeatureCount);
        }

        var cacheText = TryGetTargetConfig(intent, TargetConfigCacheMaxAge);
        var cacheMaxAge = 3600;
        if (!string.IsNullOrEmpty(cacheText))
        {
            // The docs bound cacheMaxAgeSeconds to [0, 86400] and require
            // SCENE_OPTIONS_INVALID for invalid values. The previous code
            // silently swallowed parse failures and negative values into the
            // 3600-second default, which then passed SceneDatasetValidator's
            // range check downstream. Reject the parse/negative case here so
            // the contract holds; SceneDatasetValidator continues to enforce
            // the upper bound after a successful parse.
            if (!int.TryParse(cacheText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCache)
                || parsedCache < 0)
            {
                throw new ValidationException(
                    $"{SceneGenerationErrorCodes.OptionsInvalid}: cacheMaxAgeSeconds must be a non-negative integer.");
            }
            cacheMaxAge = parsedCache;
        }

        return new SceneGenerationOptions
        {
            IncludeAttributes = includeList,
            MaxFeatureCount = maxFeatures,
            CacheMaxAgeSeconds = cacheMaxAge
        };
    }

    /// <summary>
    /// Resolves the per-feature 3D symbology (color / opacity / visibility) for
    /// every collected feature, indexed parallel to <paramref name="features"/>.
    /// Returns null when the layer declares no symbology so the GLB builder
    /// keeps its legacy single-material path.
    /// </summary>
    private static ResolvedSymbology[]? ResolvePerFeatureSymbology(
        Symbology3D? symbology,
        IReadOnlyList<SceneFeature> features)
    {
        if (symbology is null)
        {
            return null;
        }

        var resolved = new ResolvedSymbology[features.Count];
        for (var i = 0; i < features.Count; i++)
        {
            resolved[i] = Symbology3DResolver.Resolve(symbology, features[i].Attributes);
        }
        return resolved;
    }

    private static List<SceneAttributeSchema> BuildAttributeSchemas(
        MetadataV2Resource resource,
        IReadOnlyList<string> includeAttributes)
    {
        var allow = includeAttributes.Count == 0
            ? null
            : new HashSet<string>(includeAttributes, StringComparer.OrdinalIgnoreCase);

        var schemas = new List<SceneAttributeSchema>();
        // SanitizePropertyId collapses any non-alphanumeric character to '_',
        // so source field names like "a-b" and "a_b" both sanitize to "a_b"
        // and would collide in the EXT_structural_metadata schema (the
        // second column would shadow the first in the JSON dictionary).
        // De-duplicate deterministically by appending "_2", "_3", ... in
        // declaration order so the layout stays byte-identical across runs.
        var seenPropertyIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in resource.SchemaFields)
        {
            if (field.Hidden || field.Type is MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography)
            {
                continue;
            }

            if (allow is not null && !allow.Contains(field.Name))
            {
                continue;
            }

            var (schemaType, componentType) = MapFieldType(field.Type);
            if (schemaType is null)
            {
                continue;
            }

            var basePropertyId = SanitizePropertyId(field.Name);
            var propertyId = basePropertyId;
            var suffix = 2;
            while (!seenPropertyIds.Add(propertyId))
            {
                propertyId = basePropertyId + "_" + suffix.ToString(CultureInfo.InvariantCulture);
                suffix++;
            }

            schemas.Add(new SceneAttributeSchema
            {
                PropertyId = propertyId,
                FieldName = field.Name,
                SchemaType = schemaType,
                SchemaComponentType = componentType
            });
        }
        return schemas;
    }

    private static (string? Schema, string Component) MapFieldType(MetadataV2FieldType fieldType) => fieldType switch
    {
        MetadataV2FieldType.Integer => ("SCALAR", "INT32"),
        MetadataV2FieldType.BigInteger => ("SCALAR", "INT64"),
        MetadataV2FieldType.Double => ("SCALAR", "FLOAT32"),
        MetadataV2FieldType.Float => ("SCALAR", "FLOAT32"),
        MetadataV2FieldType.String => ("STRING", string.Empty),
        _ => (null, string.Empty)
    };

    private async Task<(MetadataV2Resource Resource, int StorageLayerId)> ResolveSourceResourceAsync(
        int layerId,
        CancellationToken cancellationToken)
    {
        var snapshot = await _metadataProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshot.Index.ResourcesByStorageLayerId.TryGetValue(layerId, out var resource))
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.LayerNotFound}: Layer {layerId} not found.");
        }

        var storageLayerId = snapshot.ResolveStorageLayerId(resource) ?? layerId;
        return (resource, storageLayerId);
    }

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

    private static double ResolveExtrusionMax(SceneFeature feature, MetadataV2ExtrusionInfo extrusion)
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

    private static double ResolveExtrusionBase(SceneFeature feature, MetadataV2ExtrusionInfo extrusion)
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
        MetadataV2VerticalUnits.TryNormalize(unit, out var normalized);
        return normalized switch
        {
            MetadataV2VerticalUnits.Feet => value * 0.3048,
            MetadataV2VerticalUnits.UsSurveyFeet => value * (1200.0 / 3937.0),
            _ => value
        };
    }

    private static string ResolveSceneId(PublishIntent intent, MetadataV2Resource resource)
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
        var slug = SlugifyName(resource.Metadata.Name, Math.Max(1, slugBudget));
        var candidate = $"{slug}-{suffix}".ToLowerInvariant();
        if (!SceneDatasetValidator.TryValidateSceneId(candidate, out var autoError))
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: derived sceneId '{candidate}' is invalid: {autoError}. Supply an explicit sceneId.");
        }
        return candidate;
    }

    private static string ResolveDisplayName(PublishIntent intent, MetadataV2Resource resource, string sceneId)
    {
        var name = TryGetTargetConfig(intent, TargetConfigDisplayName);
        if (!string.IsNullOrEmpty(name))
        {
            return name;
        }
        // The registry's name uniqueness constraint means defaulting to
        // resource name causes the second auto-generated scene from the same
        // layer to fail with a misleading id-conflict message (the conflict
        // is on name, not id). Suffix with the resolved sceneId so the
        // default is unique by construction; the operator can always
        // override via the displayName field.
        var layerName = FirstNonBlank(resource.Metadata.Title, resource.Metadata.Name) ?? "Scene";
        var suffix = $" ({sceneId})";
        var available = SceneDatasetValidator.MaxSceneNameLength - suffix.Length;
        if (available <= 0)
        {
            return sceneId;
        }
        if (layerName.Length > available)
        {
            layerName = layerName[..available];
        }
        return layerName + suffix;
    }

    private static string? FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
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
        AccessPolicy? layerAccessPolicy,
        string createdBy,
        CancellationToken cancellationToken)
    {
        if (_registration is null)
        {
            return null;
        }

        // Carry the source resource's access policy through to the registered
        // scene so a tileset materialised from a protected feature layer does
        // not silently expose its geometry and attributes to the anonymous
        // serving path.
        var (isPublic, requiresAuth, allowedRoles) = ResolveSceneAccess(layerAccessPolicy);

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
            RequiresAuth = requiresAuth,
            IsPublic = isPublic,
            AllowedRoles = allowedRoles,
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

    /// <summary>
    /// Maps the source layer's access policy onto the
    /// <see cref="SceneDatasetRecord"/> public/protected fields. The registry
    /// expects exactly one of <c>IsPublic</c> / <c>RequiresAuth</c> to be
    /// true — anonymous reads on the source layer fold into a public scene,
    /// while a non-anonymous source layer protects the generated tileset and
    /// forwards the role allow-list verbatim.
    /// </summary>
    private static (bool IsPublic, bool RequiresAuth, string[]? AllowedRoles) ResolveSceneAccess(
        AccessPolicy? layerAccessPolicy)
    {
        // No layer-level access policy ⇒ historic default (public scene).
        // Existing public layers continue to materialise public tilesets.
        if (layerAccessPolicy is null)
        {
            return (IsPublic: true, RequiresAuth: false, AllowedRoles: null);
        }

        // Anonymous reads accepted on the source ⇒ public scene. Even if a
        // role allow-list is set on the layer it is a *write* gate; the scene
        // tileset is read-only so the public branch is correct.
        if (layerAccessPolicy.AllowAnonymous)
        {
            return (IsPublic: true, RequiresAuth: false, AllowedRoles: null);
        }

        // Protected source ⇒ protected scene. AllowedRoles is forwarded; an
        // empty/null list keeps the dataset role-unrestricted but
        // authentication is still required, mirroring how the manual scene-
        // registry endpoint maps RequiresAuth = true with no roles.
        return (
            IsPublic: false,
            RequiresAuth: true,
            AllowedRoles: layerAccessPolicy.AllowedRoles is { Length: > 0 } roles ? roles : null);
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
        // Shared cos-lat-corrected 3D-diagonal geodesy (see GeodesicError). This is
        // the root LOD budget handed to the partitioner, so it now also carries the
        // positive floor the helper applies — ensuring the root tile (even a
        // root-leaf) declares a non-zero geometric error.
        => GeodesicError.RootGeometricError(bounds, minHeight, maxHeight);

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
        [LoggerMessage(EventId = 8430, Level = LogLevel.Information,
            Message = "Scene generation started: intent {IntentId}, source {SourceId}")]
        public static partial void Started(ILogger logger, string intentId, string sourceId);

        [LoggerMessage(EventId = 8431, Level = LogLevel.Information,
            Message = "Scene generation completed: intent {IntentId}, scene {SceneId}, features {FeatureCount}, elapsed {ElapsedMs}ms")]
        public static partial void Completed(ILogger logger, string intentId, string sceneId, int featureCount, long elapsedMs);

        [LoggerMessage(EventId = 8432, Level = LogLevel.Warning,
            Message = "Scene generation failed: intent {IntentId}, source {SourceId}, reason {Reason}")]
        public static partial void Failed(ILogger logger, string intentId, string sourceId, string reason);

        [LoggerMessage(EventId = 8433, Level = LogLevel.Information,
            Message = "Scene generation warning: intent {IntentId}, message {Message}")]
        public static partial void Warning(ILogger logger, string intentId, string message);

        [LoggerMessage(EventId = 8434, Level = LogLevel.Warning,
            Message = "Scene generation overwrote stale final directory {FinalDirectory} during staging promotion; the registry record now points at the new bytes.")]
        public static partial void PromotionOverwroteStaleFinalDir(ILogger logger, string finalDirectory);

        [LoggerMessage(EventId = 8435, Level = LogLevel.Warning,
            Message = "Scene generation could not delete staging directory {StagingDirectory}; subsequent generations are unaffected but the directory may need a manual sweep.")]
        public static partial void StagingCleanupFailed(ILogger logger, string stagingDirectory, Exception exception);

        [LoggerMessage(EventId = 8436, Level = LogLevel.Warning,
            Message = "Scene generation deactivated registry record for scene {SceneId} ({DatasetId}) after staging promotion failed; deactivated={Deactivated}.")]
        public static partial void RegistrationCompensated(ILogger logger, string sceneId, Guid datasetId, bool deactivated);

        [LoggerMessage(EventId = 8437, Level = LogLevel.Error,
            Message = "Scene generation could not deactivate registry record for scene {SceneId} ({DatasetId}) after staging promotion failed; record remains Active and the operator must clean it up via the admin scene CRUD path.")]
        public static partial void RegistrationCompensationFailed(ILogger logger, string sceneId, Guid datasetId, Exception exception);

        [LoggerMessage(EventId = 8438, Level = LogLevel.Error,
            Message = "Scene generation failed unexpectedly: intent {IntentId}, source {SourceId}")]
        public static partial void FailedUnexpected(ILogger logger, string intentId, string sourceId, Exception exception);
    }
}
