// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
internal sealed class HonuaTileGrpcService : Proto.TileService.TileServiceBase
{
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
            throw new RpcException(new Status(StatusCode.InvalidArgument, "node_id is required."));
        }

        using var activity = HonuaTelemetry.StartActivity(SceneGrpcTelemetry.GetTileActivity);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Grpc);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, SceneGrpcTelemetry.GetTileOperation);
        activity?.SetTag(SceneGrpcTelemetry.SceneIdTag, request.SceneId);
        activity?.SetTag(SceneGrpcTelemetry.NodeIdTag, request.NodeId);

        var location = await ResolveSceneAsync(request.SceneId, context).ConfigureAwait(false);
        SceneAccessGuard.EnforceReadAccess(context, location.AccessPolicy);
        var document = await LoadTilesetAsync(location, context.CancellationToken).ConfigureAwait(false);
        var entries = SceneTileCatalog.Build(document);

        var entry = entries.FirstOrDefault(candidate => string.Equals(candidate.NodeId, request.NodeId, StringComparison.Ordinal));
        if (entry is null)
        {
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
        var document = await LoadTilesetAsync(location, context.CancellationToken).ConfigureAwait(false);
        var entries = SceneTileCatalog.Build(document);

        var streamedTiles = 0;
        var streamedBytes = 0L;
        foreach (var entry in entries)
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

        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        tile.ContentType = SceneTileCatalog.ContentTypeFromUri(uri);
        tile.Content = ByteString.CopyFrom(bytes);
        return tile;
    }

    private async Task<SceneLocation> ResolveSceneAsync(string sceneId, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(sceneId))
        {
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
            throw new RpcException(new Status(StatusCode.NotFound, $"Scene '{sceneId}' was not found."));
        }

        return new SceneLocation(scene.AssetRoot, scene.TilesetFileName, scene.AccessPolicy);
    }

    private static async Task<Domain.TilesetDocument> LoadTilesetAsync(SceneLocation location, CancellationToken cancellationToken)
    {
        if (!TryResolveAssetFile(location.AssetRoot, location.TilesetFileName, out var tilesetPath))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Scene tileset was not found."));
        }

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
}
