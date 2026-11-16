// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Scene.Assets;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Domain = Honua.Core.Features.Scene.Domain;
using Proto = Geospatial.V1;
using AccessPolicy = Honua.Core.Features.Security.Domain.AccessPolicy;

namespace Honua.Scene.Grpc;

internal sealed partial class HonuaTileGrpcService : Proto.TileService.TileServiceBase
{
    private const int CatalogCacheCapacity = 64;

    private const int DefaultGrpcSendMessageSize = 16 * 1024 * 1024;

    private const long AbsoluteMaxTileContentBytes = 256L * 1024 * 1024;

    private static readonly ConcurrentDictionary<string, SceneTileCatalogCacheEntry> CatalogCache = new(StringComparer.Ordinal);

    private readonly ISceneDatasetRegistry _registry;
    private readonly ILogger<HonuaTileGrpcService> _logger;

    private readonly long _maxTileContentBytes;

    public HonuaTileGrpcService(
        ISceneDatasetRegistry registry,
        ILogger<HonuaTileGrpcService> logger,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(logger);

        _registry = registry;
        _logger = logger;

        // Clamp the tile pre-read size check to the actual gRPC send budget so
        // the FileInfo guard rejects tiles that the gRPC layer would reject
        // anyway, rather than reading up to ~512 MiB (2x 256 MiB) into managed
        // memory only to get ResourceExhausted at send time.
        var configuredSendSize = configuration?.GetValue<int?>("Grpc:MaxSendMessageSize")
            ?? DefaultGrpcSendMessageSize;
        _maxTileContentBytes = Math.Min(AbsoluteMaxTileContentBytes, configuredSendSize);
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

        var location = await ResolveSceneAsync(request.SceneId, SceneGrpcTelemetry.GetTileOperation, context).ConfigureAwait(false);
        SceneAccessGuard.EnforceReadAccess(context, location.AccessPolicy);
        var catalog = await LoadCatalogAsync(location, context.CancellationToken).ConfigureAwait(false);

        if (!catalog.ById.TryGetValue(request.NodeId, out var entry))
        {
            Log.TileNodeNotFound(_logger, SceneGrpcTelemetry.GetTileOperation, request.SceneId, request.NodeId);
            throw new RpcException(new Status(StatusCode.NotFound, $"Tile node '{request.NodeId}' was not found in scene '{request.SceneId}'."));
        }

        var tile = await BuildTileAsync(entry, location.AssetRoot, _maxTileContentBytes, context.CancellationToken).ConfigureAwait(false);
        activity?.SetTag(SceneGrpcTelemetry.TileBytesTag, tile.Content.Length);
        return new Proto.GetTileResponse { Tile = tile };
    }

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

        var location = await ResolveSceneAsync(request.SceneId, SceneGrpcTelemetry.StreamTilesOperation, context).ConfigureAwait(false);
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

            var tile = await BuildTileAsync(entry, location.AssetRoot, _maxTileContentBytes, context.CancellationToken).ConfigureAwait(false);
            await responseStream.WriteAsync(tile).ConfigureAwait(false);
            streamedTiles++;
            streamedBytes += tile.Content.Length;
        }

        activity?.SetTag(SceneGrpcTelemetry.TileCountTag, streamedTiles);
        activity?.SetTag(SceneGrpcTelemetry.TileBytesTag, streamedBytes);
    }

    private static async Task<Proto.Tile> BuildTileAsync(SceneTileEntry entry, string assetRoot, long maxContentBytes, CancellationToken cancellationToken)
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
        // again by ByteString.CopyFrom), so cap it to the effective gRPC send
        // budget to avoid an unbounded ~2x allocation from an oversized or
        // corrupted on-disk tile that would fail at send time anyway.
        var info = new FileInfo(fullPath);
        if (info.Exists && info.Length > maxContentBytes)
        {
            throw new RpcException(new Status(
                StatusCode.ResourceExhausted,
                $"Tile content '{uri}' for node '{entry.NodeId}' exceeds the maximum tile size of {maxContentBytes} bytes."));
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // The file was removed between the FileInfo stat and the read (e.g.
            // a publish promotion cleaned up a stale directory). Return NotFound
            // so the status code matches the resolve-failure path rather than
            // mapping to an opaque 5xx via the gRPC exception interceptor.
            throw new RpcException(new Status(
                StatusCode.NotFound,
                $"Tile content '{uri}' for node '{entry.NodeId}' was not found."));
        }

        tile.ContentType = SceneTileCatalog.ContentTypeFromUri(uri);
        tile.Content = ByteString.CopyFrom(bytes);
        return tile;
    }

    private async Task<SceneLocation> ResolveSceneAsync(string sceneId, string operation, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(sceneId))
        {
            Log.InvalidArgument(_logger, operation, sceneId, "scene_id is required.");
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
            Log.SceneNotFound(_logger, operation, sceneId);
            throw new RpcException(new Status(StatusCode.NotFound, $"Scene '{sceneId}' was not found."));
        }

        return new SceneLocation(scene.AssetRoot, scene.TilesetFileName, scene.AccessPolicy);
    }

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
