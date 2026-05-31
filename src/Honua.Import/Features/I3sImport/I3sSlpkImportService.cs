// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Honua.Core.Features.Scene;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Scene.Generation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Import.Features.I3sImport;

/// <summary>
/// Orchestrates the I3S/.slpk → OGC 3D Tiles conversion pipeline:
/// 1. Open .slpk as a ZIP archive
/// 2. Parse <c>3dSceneLayer.json</c> and validate scope (layer type + CRS)
/// 3. Walk the NodePage hierarchy
/// 4. For each node, decode its geometry buffer and write a per-node GLB
/// 5. Emit <c>tileset.json</c> rooted at the output asset directory
/// 6. Register the dataset via <see cref="ISceneRegistrationService"/>
/// </summary>
internal sealed partial class I3sSlpkImportService
{
    private const string GeneratorTag = "honua-i3s-import";

    private readonly ISceneRegistrationService _registrationService;
    private readonly IOptions<I3sImportOptions> _options;
    private readonly ILogger<I3sSlpkImportService> _logger;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    public I3sSlpkImportService(
        ISceneRegistrationService registrationService,
        IOptions<I3sImportOptions> options,
        ILogger<I3sSlpkImportService> logger)
    {
        _registrationService = registrationService ?? throw new ArgumentNullException(nameof(registrationService));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Imports the .slpk file referenced by <paramref name="request"/>.
    /// </summary>
    public async Task<I3sSlpkImportResult> ImportAsync(
        I3sSlpkImportRequest request,
        string createdBy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(createdBy);

        if (string.IsNullOrWhiteSpace(request.SlpkPath))
        {
            return Failure("slpkPath is required.");
        }

        if (!File.Exists(request.SlpkPath))
        {
            return Failure($"I3S .slpk file not found at '{request.SlpkPath}'.");
        }

        var stopwatch = Stopwatch.StartNew();
        var warnings = new List<string>();

        try
        {
            using var slpk = I3sSlpkReader.Open(request.SlpkPath);

            var sceneLayer = ReadSceneLayer(slpk);
            if (!IsSupportedLayerType(sceneLayer.LayerType))
            {
                return Failure(
                    $"I3S layer type '{sceneLayer.LayerType}' is not supported. " +
                    "Initial slice supports only 3D Object layers; point cloud and integrated-mesh inputs are deferred.");
            }

            if (!IsSupportedCrs(sceneLayer.SpatialReference, out var crsError))
            {
                return Failure(crsError);
            }

            var store = sceneLayer.Store ?? throw new InvalidDataException("3dSceneLayer.json is missing the 'store' block.");
            var schema = store.DefaultGeometrySchema ?? throw new InvalidDataException(
                "3dSceneLayer.json is missing 'store.defaultGeometrySchema'.");

            var datasetId = ResolveDatasetId(request, slpkPath: request.SlpkPath);
            if (!SceneDatasetValidator.TryValidateSceneId(datasetId, out var idError))
            {
                return Failure(idError);
            }

            var displayName = request.Name ?? sceneLayer.Name ?? datasetId;
            if (!SceneDatasetValidator.TryValidateName(displayName, out var nameError))
            {
                return Failure(nameError);
            }

            var nodesPerPage = store.NodePages?.NodesPerPage ?? 64;
            var nodeTree = new I3sNodeTreeReader(slpk, nodesPerPage);

            I3sNodePageEntry root;
            try
            {
                root = nodeTree.GetRoot();
            }
            catch (FileNotFoundException)
            {
                return Failure(
                    "I3S .slpk is missing 'nodepages/0.json'. Legacy I3S layouts (<1.7) without NodePages are not supported by the initial slice.");
            }

            var rootDiameterMeters = ResolveRootDiameterMeters(root, sceneLayer.FullExtent);

            var isDryRun = request.DryRun == true;
            var contentUris = new Dictionary<int, string>();
            long totalTileBytes = 0;
            var nodeCount = 0;

            string? assetRoot = null;
            if (!isDryRun)
            {
                if (!TryResolveAssetRoot(request, datasetId, out assetRoot, out var assetRootError))
                {
                    return Failure(assetRootError);
                }
                if (!SceneDatasetValidator.TryValidateAssetRoot(NormalizeAssetRootForPersistence(assetRoot), out var assetRootValidationError))
                {
                    return Failure(assetRootValidationError);
                }
                Directory.CreateDirectory(Path.Combine(assetRoot, "nodes"));
            }

            foreach (var node in nodeTree.EnumerateAllNodes())
            {
                cancellationToken.ThrowIfCancellationRequested();
                nodeCount++;
                var geometryRef = node.Mesh?.Geometry;
                if (geometryRef is null)
                {
                    continue;
                }

                var geometryEntryPath = $"nodes/{node.Index}/geometries/{geometryRef.Resource}.bin";
                if (!slpk.ContainsEntry(geometryEntryPath))
                {
                    warnings.Add($"Skipped node {node.Index}: geometry entry '{geometryEntryPath}' missing from .slpk.");
                    continue;
                }

                if (node.Mbs is not { Length: 4 } mbs)
                {
                    warnings.Add($"Skipped node {node.Index}: missing MBS — cannot place geometry in world frame.");
                    continue;
                }

                byte[] glb;
                try
                {
                    var geometryBytes = slpk.ReadAllBytes(geometryEntryPath);
                    glb = I3sGeometryConverter.BuildGlbFromI3sGeometry(
                        geometryBytes,
                        schema,
                        mbsCenterLongitudeDegrees: mbs[0],
                        mbsCenterLatitudeDegrees: mbs[1],
                        mbsCenterHeightMeters: mbs[2],
                        GeneratorTag);
                }
                catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or InvalidOperationException)
                {
                    warnings.Add($"Skipped node {node.Index}: {ex.Message}");
                    continue;
                }

                var glbRelativePath = $"nodes/{node.Index}.glb";
                contentUris[node.Index] = glbRelativePath;
                totalTileBytes += glb.LongLength;

                if (!isDryRun && assetRoot is not null)
                {
                    var glbAbsolutePath = Path.Combine(assetRoot, glbRelativePath.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(glbAbsolutePath)!);
                    await File.WriteAllBytesAsync(glbAbsolutePath, glb, cancellationToken).ConfigureAwait(false);
                }
            }

            if (contentUris.Count == 0)
            {
                return Failure("No I3S nodes produced renderable geometry; nothing to register.");
            }

            var tilesetBuilder = new I3sTilesetBuilder(nodeTree, contentUris, rootDiameterMeters);
            var tilesetDocument = tilesetBuilder.Build(GeneratorTag);

            if (!isDryRun && assetRoot is not null)
            {
                var tilesetPath = Path.Combine(assetRoot, "tileset.json");
                var bytes = JsonSerializer.SerializeToUtf8Bytes(tilesetDocument, TilesetJsonContext.Default.TilesetDocument);
                await File.WriteAllBytesAsync(tilesetPath, bytes, cancellationToken).ConfigureAwait(false);
            }

            var extent = ResolveExtent(sceneLayer.FullExtent);

            if (!isDryRun && assetRoot is not null)
            {
                var record = new SceneDatasetRecord
                {
                    DatasetId = Guid.NewGuid(),
                    Id = datasetId,
                    Name = displayName,
                    Description = sceneLayer.Description,
                    AssetRoot = NormalizeAssetRootForPersistence(assetRoot),
                    TilesetFileName = "tileset.json",
                    DatasetType = SceneDatasetType.HostedTiles,
                    Extent = extent,
                    Crs = "EPSG:4979",
                    CachePolicy = SceneCachePolicy.Default,
                    EditionGate = string.IsNullOrWhiteSpace(request.EditionGate) ? null : request.EditionGate,
                    RequiresAuth = request.IsPublic == false,
                    IsPublic = request.IsPublic ?? true,
                    Status = SceneDatasetStatus.Active,
                    Revision = 1,
                    CreatedAt = DateTimeOffset.UtcNow,
                    CreatedBy = createdBy
                };

                await _registrationService.RegisterAsync(record, cancellationToken).ConfigureAwait(false);
            }

            stopwatch.Stop();
            Log.ImportSucceeded(_logger, datasetId, nodeCount, contentUris.Count, totalTileBytes, isDryRun);

            return new I3sSlpkImportResult
            {
                Success = true,
                DatasetId = datasetId,
                Name = displayName,
                LayerType = sceneLayer.LayerType,
                NodeCount = contentUris.Count,
                TileBytesWritten = totalTileBytes,
                AssetRoot = assetRoot,
                Duration = stopwatch.Elapsed,
                Warnings = warnings.Count == 0 ? null : warnings.ToArray()
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SceneDatasetAlreadyExistsException ex)
        {
            Log.ImportConflict(_logger, request.DatasetId ?? "<derived>", ex.Message);
            return Failure(ex.Message);
        }
        catch (InvalidDataException ex)
        {
            Log.ImportFailedParse(_logger, request.SlpkPath, ex.Message);
            return Failure(ex.Message);
        }
        catch (Exception ex)
        {
            Log.ImportFailed(_logger, request.SlpkPath, ex);
            return Failure("I3S .slpk import failed; see server logs for details.");
        }
    }

    private static I3sSceneLayer ReadSceneLayer(I3sSlpkReader slpk)
    {
        const string scenePath = "3dSceneLayer.json";
        if (!slpk.ContainsEntry(scenePath))
        {
            throw new InvalidDataException(
                $"I3S .slpk is missing '{scenePath}'. The supplied file is not a valid I3S Scene Layer Package.");
        }

        using var stream = slpk.OpenEntry(scenePath);
        return JsonSerializer.Deserialize(stream, I3sSceneLayerJsonContext.Default.I3sSceneLayer)
            ?? throw new InvalidDataException("3dSceneLayer.json deserialized to null.");
    }

    private static bool IsSupportedLayerType(string layerType)
        => string.Equals(layerType, "3DObject", StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportedCrs(I3sSpatialReference? spatialReference, out string error)
    {
        if (spatialReference is null)
        {
            error = "3dSceneLayer.json is missing spatialReference; cannot validate CRS.";
            return false;
        }

        var horizontalWkid = spatialReference.LatestWkid ?? spatialReference.Wkid;
        if (horizontalWkid is not (4326 or 4979))
        {
            error = $"I3S spatial reference WKID {horizontalWkid} is not supported. Only WGS-84 (4326/4979) is supported by the initial slice.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static double ResolveRootDiameterMeters(I3sNodePageEntry root, I3sFullExtent? extent)
    {
        if (root.Mbs is { Length: 4 } mbs)
        {
            return mbs[3] * 2.0;
        }

        if (extent is not null)
        {
            var (x1, y1, _) = EcefCoordinateTransform.ToEcef(extent.XMin, extent.YMin, 0);
            var (x2, y2, _) = EcefCoordinateTransform.ToEcef(extent.XMax, extent.YMax, 0);
            var dx = x2 - x1;
            var dy = y2 - y1;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // Fall back to a planet-scale guess; conservative but prevents zero error.
        return EcefCoordinateTransform.WgsSemiMajorAxis;
    }

    private static SceneExtent? ResolveExtent(I3sFullExtent? extent)
    {
        if (extent is null) return null;

        var xMin = Math.Clamp(extent.XMin, -180.0, 180.0);
        var xMax = Math.Clamp(extent.XMax, -180.0, 180.0);
        var yMin = Math.Clamp(extent.YMin, -90.0, 90.0);
        var yMax = Math.Clamp(extent.YMax, -90.0, 90.0);
        if (xMin > xMax || yMin > yMax) return null;

        return new SceneExtent(xMin, yMin, xMax, yMax);
    }

    private bool TryResolveAssetRoot(
        I3sSlpkImportRequest request,
        string datasetId,
        out string assetRoot,
        out string error)
    {
        var options = _options.Value;
        if (!string.IsNullOrWhiteSpace(request.AssetRoot))
        {
            if (!options.AllowExplicitAssetRoot)
            {
                assetRoot = string.Empty;
                error = "Explicit assetRoot override is disabled. Configure I3sImport:AllowExplicitAssetRoot=true to enable, or omit the field to use the configured base.";
                return false;
            }

            assetRoot = Path.GetFullPath(request.AssetRoot);
            error = string.Empty;
            return true;
        }

        if (string.IsNullOrWhiteSpace(options.AssetRootBase))
        {
            assetRoot = string.Empty;
            error = "I3sImport:AssetRootBase is not configured and no explicit assetRoot was supplied.";
            return false;
        }

        assetRoot = Path.GetFullPath(Path.Combine(options.AssetRootBase, datasetId));
        error = string.Empty;
        return true;
    }

    private static string ResolveDatasetId(I3sSlpkImportRequest request, string slpkPath)
    {
        if (!string.IsNullOrWhiteSpace(request.DatasetId))
        {
            return request.DatasetId.Trim().ToLowerInvariant();
        }

        var fileName = Path.GetFileNameWithoutExtension(slpkPath);
        var slug = SlugRegex().Replace(fileName.ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrEmpty(slug) ? "i3s-scene" : slug;
    }

    private static string NormalizeAssetRootForPersistence(string assetRoot)
        => assetRoot.Replace('\\', '/');

    private static I3sSlpkImportResult Failure(string errorMessage) => new()
    {
        Success = false,
        ErrorMessage = errorMessage
    };

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex SlugRegex();

    private static partial class Log
    {
        [LoggerMessage(EventId = 9117, Level = LogLevel.Information,
            Message = "I3S .slpk import succeeded for dataset '{DatasetId}' (nodesVisited={NodesVisited}, tilesWritten={TilesWritten}, bytes={Bytes}, dryRun={DryRun}).")]
        public static partial void ImportSucceeded(ILogger logger, string datasetId, int nodesVisited, int tilesWritten, long bytes, bool dryRun);

        [LoggerMessage(EventId = 9118, Level = LogLevel.Warning,
            Message = "I3S .slpk import rejected for '{SlpkPath}': {Reason}")]
        public static partial void ImportFailedParse(ILogger logger, string? slpkPath, string reason);

        [LoggerMessage(EventId = 9119, Level = LogLevel.Warning,
            Message = "I3S .slpk import conflict for dataset '{DatasetId}': {Reason}")]
        public static partial void ImportConflict(ILogger logger, string datasetId, string reason);

        [LoggerMessage(EventId = 9120, Level = LogLevel.Error,
            Message = "I3S .slpk import failed for '{SlpkPath}'.")]
        public static partial void ImportFailed(ILogger logger, string? slpkPath, Exception exception);
    }
}
