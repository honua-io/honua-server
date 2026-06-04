// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Scene.Assets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
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

        var location = await ResolveSceneAsync(request.SceneId, context).ConfigureAwait(false);
        SceneAccessGuard.EnforceReadAccess(context, location.AccessPolicy);
        var document = await LoadTilesetAsync(location, context.CancellationToken).ConfigureAwait(false);
        var entries = SceneTileCatalog.Build(document);

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
        }
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

        var registration = context.GetHttpContext()?.RequestServices.GetService<ISceneRegistrationService>();
        if (registration is not null)
        {
            var record = await registration.GetBySceneIdAsync(sceneId, context.CancellationToken).ConfigureAwait(false);
            if (record is not null)
            {
                if (record.Status != Domain.SceneDatasetStatus.Active)
                {
                    throw new RpcException(new Status(StatusCode.NotFound, $"Scene '{sceneId}' was not found."));
                }

                // Mirror PostgresSceneDatasetRegistry.ProjectToServing: a public
                // record carries no policy; a non-public record maps RequiresAuth
                // (AllowAnonymous=false) plus its allowed roles into an
                // AccessPolicy the shared evaluator understands.
                var recordPolicy = record.IsPublic
                    ? null
                    : new AccessPolicy
                    {
                        AllowAnonymous = false,
                        AllowedRoles = record.AllowedRoles?.ToArray(),
                    };

                return new SceneLocation(record.AssetRoot, record.TilesetFileName, recordPolicy);
            }
        }

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
    private static bool TryResolveAssetFile(string assetRoot, string relativePath, out string fullPath)
    {
        // Canonicalize + trim the asset root the same way the dataset registries
        // do so the resolver's lexical under-root check sees a normalized root
        // for record-backed scenes whose stored root may be relative.
        string canonicalRoot;
        try
        {
            canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(assetRoot));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            fullPath = string.Empty;
            return false;
        }

        if (SceneAssetResolver.TryResolve(canonicalRoot, relativePath, out var resolved, out _))
        {
            fullPath = resolved.File.FullName;
            return true;
        }

        fullPath = string.Empty;
        return false;
    }

    private readonly record struct SceneLocation(string AssetRoot, string TilesetFileName, AccessPolicy? AccessPolicy);
}
