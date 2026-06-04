// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Scene.Assets;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Logging;
using Domain = Honua.Core.Features.Scene.Domain;
using Proto = Geospatial.V1;
using AccessPolicy = Honua.Core.Features.Security.Domain.AccessPolicy;

namespace Honua.Scene.Grpc;

/// <summary>
/// gRPC <c>TileService</c> implementation (honua-server#1194 / #1195). Serves
/// 3D Tiles payloads for a hosted scene from its on-disk tileset, addressing
/// nodes by the deterministic ids assigned by <see cref="SceneTileCatalog"/>.
/// <see cref="GetTile"/> fetches one node; <see cref="StreamTiles"/> delivers a
/// scene progressively, filtered by level of detail, geometric error, and
/// spatial extent.
/// </summary>
internal sealed partial class HonuaTileGrpcService : Proto.TileService.TileServiceBase
{
    /// <summary>
    /// Maximum number of distinct tileset catalogs held in-process. A modest cap
    /// keeps the footprint flat across many scenes; on overflow the cache is
    /// cleared and repopulated lazily.
    /// </summary>
    private const int CatalogCacheCapacity = 64;

    /// <summary>
    /// Hard ceiling on a single tile content payload (256 MiB). A unary
    /// <see cref="GetTile"/> response — and each <see cref="StreamTiles"/> tile —
    /// is fully materialized in memory (read into a managed array, then copied
    /// again by <see cref="ByteString.CopyFrom(byte[])"/>), so an oversized or
    /// corrupted on-disk tile would otherwise drive an unbounded ~2x allocation
    /// spike. The file size is checked cheaply via <see cref="FileInfo"/> before
    /// reading and a payload above this ceiling is rejected with
    /// <see cref="StatusCode.ResourceExhausted"/> rather than buffered. The cap is
    /// also comfortably above the typical few-MiB b3dm/pnts tile while staying
    /// within a sane gRPC send-message budget for legitimate large tiles.
    /// </summary>
    private const long MaxTileContentBytes = 256L * 1024 * 1024;

    /// <summary>
    /// In-process catalog cache keyed by resolved tileset path. Each value pins
    /// the last-write-time it was built from so a tileset rewrite invalidates the
    /// entry (see <see cref="LoadCatalogAsync"/>). Static so it is shared across
    /// the transient gRPC service instances the DI container creates per call.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SceneTileCatalogCacheEntry> CatalogCache = new(StringComparer.Ordinal);

    private readonly ISceneDatasetRegistry _registry;
    private readonly ILogger<HonuaTileGrpcService> _logger;

    public HonuaTileGrpcService(
        ISceneDatasetRegistry registry,
        ILogger<HonuaTileGrpcService> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(logger);

        _registry = registry;
        _logger = logger;
    }

    public override async Task<Proto.GetTileResponse> GetTile(
        Proto.GetTileRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(request.NodeId))
        {
            Log.InvalidArgument(_logger, SceneGrpcTelemetry.GetTileOperation, request.SceneId, "node_id is required.");
            throw new RpcException(new Status(StatusCode.InvalidArgument, "node_id is required."));
        }

        using var activity = HonuaTelemetry.StartActivity(SceneGrpcTelemetry.GetTileActivity);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Grpc);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, SceneGrpcTelemetry.GetTileOperation);
        activity?.SetTag(SceneGrpcTelemetry.SceneIdTag, request.SceneId);
        activity?.SetTag(SceneGrpcTelemetry.NodeIdTag, request.NodeId);

        var location = await ResolveSceneAsync(request.SceneId, context).ConfigureAwait(false);
        SceneAccessGuard.EnforceReadAccess(context, location.AccessPolicy);
        var catalog = await LoadCatalogAsync(location, context.CancellationToken).ConfigureAwait(false);

        if (!catalog.ById.TryGetValue(request.NodeId, out var entry))
        {
            Log.TileNodeNotFound(_logger, SceneGrpcTelemetry.GetTileOperation, request.SceneId, request.NodeId);
            throw new RpcException(new Status(StatusCode.NotFound, $"Tile node '{request.NodeId}' was not found in scene '{request.SceneId}'."));
        }

        var tile = await BuildTileAsync(entry, location.AssetRoot, context.CancellationToken).ConfigureAwait(false);
        activity?.SetTag(SceneGrpcTelemetry.TileBytesTag, tile.Content.Length);
        return new Proto.GetTileResponse { Tile = tile };
    }

    /// <summary>
    /// Streams a scene's tiles, filtered by level of detail, geometric error, and
    /// spatial extent.
    /// </summary>
    /// <remarks>
    /// Intentional contract divergence: the proto documents
    /// <c>max_geometric_error = 0</c> as "server default", but Honua applies no
    /// server-side default cap. A value of <c>0</c> (the default for an unset
    /// field) therefore means "unbounded" — no geometric-error filtering — exactly
    /// like <c>max_lod = 0</c> means "all LODs". Callers that want a cap must send
    /// an explicit positive <c>max_geometric_error</c>.
    /// </remarks>
    public override async Task StreamTiles(
        Proto.StreamTilesRequest request,
        IServerStreamWriter<Proto.Tile> responseStream,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(responseStream);
        ArgumentNullException.ThrowIfNull(context);

        using var activity = HonuaTelemetry.StartActivity(SceneGrpcTelemetry.StreamTilesActivity);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Grpc);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, SceneGrpcTelemetry.StreamTilesOperation);
        activity?.SetTag(SceneGrpcTelemetry.SceneIdTag, request.SceneId);

        var location = await ResolveSceneAsync(request.SceneId, context).ConfigureAwait(false);
        SceneAccessGuard.EnforceReadAccess(context, location.AccessPolicy);
        var catalog = await LoadCatalogAsync(location, context.CancellationToken).ConfigureAwait(false);

        var streamedTiles = 0;
        var streamedBytes = 0L;
        foreach (var entry in catalog.Entries)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            // Only nodes that carry a payload are streamable.
            if (string.IsNullOrEmpty(entry.Node.Content?.Uri))
            {
                continue;
            }

            if (entry.Lod < request.MinLod)
            {
                continue;
            }

            if (request.MaxLod > 0 && entry.Lod > request.MaxLod)
            {
                continue;
            }

            if (request.MaxGeometricError > 0 && entry.Node.GeometricError > request.MaxGeometricError)
            {
                continue;
            }

            if (!SceneTileCatalog.IntersectsExtent(entry.Node, request.Extent))
            {
                continue;
            }

            var tile = await BuildTileAsync(entry, location.AssetRoot, context.CancellationToken).ConfigureAwait(false);
            await responseStream.WriteAsync(tile).ConfigureAwait(false);
            streamedTiles++;
            streamedBytes += tile.Content.Length;
        }

        activity?.SetTag(SceneGrpcTelemetry.TileCountTag, streamedTiles);
        activity?.SetTag(SceneGrpcTelemetry.TileBytesTag, streamedBytes);
    }

    /// <summary>
    /// Reads a node's content payload from disk and wraps it in a
    /// <see cref="Proto.Tile"/>, inferring the content type from the uri
    /// extension.
    /// </summary>
    /// <remarks>
    /// External sub-tileset contract: a node whose content uri is an external
    /// <c>.json</c> sub-tileset is streamed as OPAQUE bytes with
    /// <see cref="Proto.TileContentType.Unspecified"/> (the extension is not
    /// glb/b3dm/i3dm/pnts). This slice does NOT expand external tilesets
    /// server-side; it serves the referenced <c>.json</c> verbatim and the client
    /// is responsible for parsing it and following its nested content/children.
    /// The deterministic node ids assigned by <see cref="SceneTileCatalog"/> are
    /// confined to a single tileset document, so an external tileset's nodes are
    /// only addressable after the client re-issues requests against that
    /// expanded tree (a tracked follow-up if server-side expansion is needed).
    /// </remarks>
    private static async Task<Proto.Tile> BuildTileAsync(SceneTileEntry entry, string assetRoot, CancellationToken cancellationToken)
    {
        var tile = new Proto.Tile
        {
            Node = SceneTileCatalog.ToProtoNode(entry),
            ContentType = Proto.TileContentType.Unspecified,
            Content = ByteString.Empty,
        };

        var uri = entry.Node.Content?.Uri;
        if (string.IsNullOrEmpty(uri))
        {
            return tile;
        }

        if (!TryResolveAssetFile(assetRoot, uri, out var fullPath))
        {
            throw new RpcException(new Status(
                StatusCode.NotFound,
                $"Tile content '{uri}' for node '{entry.NodeId}' was not found."));
        }

        // Stat the file before reading: the whole payload is buffered (and copied
        // again by ByteString.CopyFrom), so cap it to avoid an unbounded ~2x
        // allocation from an oversized/corrupted on-disk tile.
        var info = new FileInfo(fullPath);
        if (info.Exists && info.Length > MaxTileContentBytes)
        {
            throw new RpcException(new Status(
                StatusCode.ResourceExhausted,
                $"Tile content '{uri}' for node '{entry.NodeId}' exceeds the maximum tile size of {MaxTileContentBytes} bytes."));
        }

        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        tile.ContentType = SceneTileCatalog.ContentTypeFromUri(uri);
        tile.Content = ByteString.CopyFrom(bytes);
        return tile;
    }

    private async Task<SceneLocation> ResolveSceneAsync(string sceneId, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(sceneId))
        {
            Log.InvalidArgument(_logger, SceneGrpcTelemetry.GetTileOperation, sceneId, "scene_id is required.");
            throw new RpcException(new Status(StatusCode.InvalidArgument, "scene_id is required."));
        }

        // Resolve exclusively through the registry. ISceneDatasetRegistry.FindAsync
        // returns the already-projected SceneDataset for BOTH configuration- and
        // database-backed scenes: it applies active-status filtering, derives the
        // canonical AccessPolicy (SceneServingProjection), and canonicalizes the
        // AssetRoot. The gRPC adapter therefore consumes that projection directly
        // instead of re-deriving authorization or re-canonicalizing the root.
        var scene = await _registry.FindAsync(sceneId, context.CancellationToken).ConfigureAwait(false);
        if (scene is null)
        {
            Log.SceneNotFound(_logger, SceneGrpcTelemetry.GetTileOperation, sceneId);
            throw new RpcException(new Status(StatusCode.NotFound, $"Scene '{sceneId}' was not found."));
        }

        return new SceneLocation(scene.AssetRoot, scene.TilesetFileName, scene.AccessPolicy);
    }

    /// <summary>
    /// Returns the flattened, addressable node catalog for a scene's tileset.
    /// </summary>
    /// <remarks>
    /// The parsed catalog is cached in-process keyed by (resolved tileset path +
    /// last-write-time) so a client fetching N nodes does not re-read, re-parse,
    /// and re-walk the whole tileset N times — GetTile previously paid that full
    /// cost per node. The catalog is a pure, deterministic projection of an
    /// immutable on-disk document (<see cref="SceneTileCatalog.Build"/>), so it is
    /// safe to share across calls; a write to <c>tileset.json</c> bumps the
    /// last-write-time and invalidates the entry. The cache is bounded
    /// (<see cref="CatalogCacheCapacity"/>) and falls back to a fresh build on
    /// eviction, so it never grows unbounded across many scenes.
    /// </remarks>
    private static async Task<SceneTileCatalogCacheEntry> LoadCatalogAsync(SceneLocation location, CancellationToken cancellationToken)
    {
        if (!TryResolveAssetFile(location.AssetRoot, location.TilesetFileName, out var tilesetPath))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Scene tileset was not found."));
        }

        DateTime lastWriteUtc;
        try
        {
            lastWriteUtc = File.GetLastWriteTimeUtc(tilesetPath);
        }
        catch (IOException)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Scene tileset was not found."));
        }

        if (CatalogCache.TryGetValue(tilesetPath, out var cached)
            && cached.LastWriteUtc == lastWriteUtc)
        {
            return cached;
        }

        var document = await LoadTilesetAsync(tilesetPath, cancellationToken).ConfigureAwait(false);
        var entries = SceneTileCatalog.Build(document);
        var byId = new Dictionary<string, SceneTileEntry>(entries.Count, StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            // Node ids are deterministic and unique by tree position, so the first
            // (and only) writer wins; the indexer keeps the dictionary build O(n).
            byId[entry.NodeId] = entry;
        }

        var built = new SceneTileCatalogCacheEntry(entries, byId, lastWriteUtc);

        // Bound the cache: when over capacity, clear it rather than implementing a
        // full LRU — scenes are few and a cold rebuild is cheap relative to the
        // per-node savings, so a simple bounded-clear keeps the footprint flat
        // without risking stale/incorrect entries.
        if (CatalogCache.Count >= CatalogCacheCapacity)
        {
            CatalogCache.Clear();
        }

        CatalogCache[tilesetPath] = built;
        return built;
    }

    private static async Task<Domain.TilesetDocument> LoadTilesetAsync(string tilesetPath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(tilesetPath);
        var document = await JsonSerializer
            .DeserializeAsync(stream, Domain.TilesetJsonContext.Default.TilesetDocument, cancellationToken)
            .ConfigureAwait(false);

        if (document is null)
        {
            throw new RpcException(new Status(StatusCode.Internal, "Scene tileset could not be parsed."));
        }

        return document;
    }

    /// <summary>
    /// Canonicalizes an asset-relative path under the scene's asset root and
    /// rejects anything that escapes the root or hints at traversal. Delegates
    /// to the shared <see cref="SceneAssetResolver"/> so the gRPC path enforces
    /// the exact same path-safety contract (percent-encoded traversal, null
    /// bytes, drive-letter/UNC prefixes, collapsed segments, and symlink /
    /// reparse-point redirection) as the HTTP/I3S scene asset endpoints.
    /// </summary>
    /// <remarks>
    /// The <paramref name="assetRoot"/> is already canonicalized by
    /// <see cref="ISceneDatasetRegistry.FindAsync"/> (which applies the same
    /// absolute/trimmed normalization the dataset registries use), so this method
    /// does not re-canonicalize it; <see cref="SceneAssetResolver"/> tolerates any
    /// root shape regardless.
    /// </remarks>
    private static bool TryResolveAssetFile(string assetRoot, string relativePath, out string fullPath)
    {
        if (SceneAssetResolver.TryResolve(assetRoot, relativePath, out var resolved, out _))
        {
            fullPath = resolved.File.FullName;
            return true;
        }

        fullPath = string.Empty;
        return false;
    }

    private readonly record struct SceneLocation(string AssetRoot, string TilesetFileName, AccessPolicy? AccessPolicy);

    /// <summary>
    /// Cached projection of a single tileset document: the ordered (root-first)
    /// entry list used by <see cref="StreamTiles"/> plus a node-id index used by
    /// <see cref="GetTile"/>, pinned to the <paramref name="LastWriteUtc"/> the
    /// source <c>tileset.json</c> carried when it was built.
    /// </summary>
    private sealed record SceneTileCatalogCacheEntry(
        IReadOnlyList<SceneTileEntry> Entries,
        IReadOnlyDictionary<string, SceneTileEntry> ById,
        DateTime LastWriteUtc);

    private static partial class Log
    {
        [LoggerMessage(EventId = 8445, Level = LogLevel.Debug,
            Message = "gRPC tile {Operation} rejected scene {SceneId}: {Reason}")]
        public static partial void InvalidArgument(ILogger logger, string operation, string sceneId, string reason);

        [LoggerMessage(EventId = 8446, Level = LogLevel.Debug,
            Message = "gRPC tile {Operation} did not find scene {SceneId}.")]
        public static partial void SceneNotFound(ILogger logger, string operation, string sceneId);

        [LoggerMessage(EventId = 8447, Level = LogLevel.Debug,
            Message = "gRPC tile {Operation} did not find node {NodeId} in scene {SceneId}.")]
        public static partial void TileNodeNotFound(ILogger logger, string operation, string sceneId, string nodeId);
    }
}
