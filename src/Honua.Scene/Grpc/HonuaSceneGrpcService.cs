// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Grpc.Core;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Honua.Infrastructure.Scene;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Proto = Geospatial.V1;
using SecurityDomain = Honua.Core.Features.Security.Domain;

namespace Honua.Scene.Grpc;

/// <summary>
/// gRPC <c>SceneService</c> implementation (honua-server#1194 / #1195). Mirrors
/// the public HTTP scene discovery surface (<c>/api/scenes</c>): it merges
/// database-backed scene records (when a registration service is present) with
/// configuration-backed hosted-tile datasets, returning the same catalog over
/// the typed gRPC contract.
/// </summary>
internal sealed class HonuaSceneGrpcService : Proto.SceneService.SceneServiceBase
{
    private readonly ISceneDatasetRegistry _registry;
    private readonly SceneDatasetOptions _options;
    private readonly ILogger<HonuaSceneGrpcService> _logger;

    public HonuaSceneGrpcService(
        ISceneDatasetRegistry registry,
        IOptions<SceneDatasetOptions> options,
        ILogger<HonuaSceneGrpcService> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _registry = registry;
        _options = options.Value;
        _logger = logger;
    }

    public override async Task<Proto.ListScenesResponse> ListScenes(
        Proto.ListScenesRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var registration = ResolveRegistrationService(context);
        var scenes = new List<Proto.SceneMetadata>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // ListScenes intentionally mirrors the public HTTP scene discovery
        // catalog (SceneDiscoveryEndpoints), which lists every active scene —
        // including protected ones — and advertises their auth posture rather
        // than gating the listing itself. Per-scene authorization is enforced
        // when metadata or tiles are actually fetched (GetScene / GetTile /
        // StreamTiles), so the discovery surface stays at parity across
        // protocols. Keep this method ungated to preserve that parity.
        if (registration is not null)
        {
            var records = await registration
                .ListAsync(includeInactive: true, context.CancellationToken)
                .ConfigureAwait(false);

            foreach (var record in records)
            {
                seen.Add(record.Id);
                if (record.Status == SceneDatasetStatus.Active)
                {
                    scenes.Add(SceneGrpcMapping.ToSceneMetadata(record));
                }
            }
        }

        foreach (var entry in _options.Datasets ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry.Id) || !seen.Add(entry.Id))
            {
                continue;
            }

            var scene = await _registry.FindAsync(entry.Id, context.CancellationToken).ConfigureAwait(false);
            if (scene is not null)
            {
                scenes.Add(SceneGrpcMapping.ToSceneMetadata(scene));
            }
        }

        var ordered = scenes
            .OrderBy(scene => scene.SceneId, StringComparer.Ordinal)
            .ToList();

        var offset = Math.Max(0, request.ResultOffset);
        var count = request.ResultRecordCount;

        IEnumerable<Proto.SceneMetadata> page = ordered.Skip(offset);
        if (count > 0)
        {
            page = page.Take(count);
        }

        var response = new Proto.ListScenesResponse
        {
            ExceededTransferLimit = count > 0 && ordered.Count > offset + count,
        };
        response.Scenes.AddRange(page);
        return response;
    }

    public override async Task<Proto.GetSceneResponse> GetScene(
        Proto.GetSceneRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(request.SceneId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "scene_id is required."));
        }

        var registration = ResolveRegistrationService(context);
        if (registration is not null)
        {
            var record = await registration
                .GetBySceneIdAsync(request.SceneId, context.CancellationToken)
                .ConfigureAwait(false);
            if (record is not null)
            {
                if (record.Status != SceneDatasetStatus.Active)
                {
                    throw new RpcException(new Status(StatusCode.NotFound, $"Scene '{request.SceneId}' was not found."));
                }

                // Enforce the same per-scene authorization the HTTP/I3S surfaces
                // apply before returning metadata for a protected scene. Mirrors
                // PostgresSceneDatasetRegistry.ProjectToServing's policy mapping.
                var recordPolicy = record.IsPublic
                    ? null
                    : new SecurityDomain.AccessPolicy
                    {
                        AllowAnonymous = false,
                        AllowedRoles = record.AllowedRoles?.ToArray(),
                    };
                SceneAccessGuard.EnforceReadAccess(context, recordPolicy);

                return new Proto.GetSceneResponse { Scene = SceneGrpcMapping.ToSceneMetadata(record) };
            }
        }

        var scene = await _registry.FindAsync(request.SceneId, context.CancellationToken).ConfigureAwait(false);
        if (scene is null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"Scene '{request.SceneId}' was not found."));
        }

        SceneAccessGuard.EnforceReadAccess(context, scene.AccessPolicy);

        return new Proto.GetSceneResponse { Scene = SceneGrpcMapping.ToSceneMetadata(scene) };
    }

    /// <summary>
    /// Resolves the optional <see cref="ISceneRegistrationService"/> from the
    /// request scope. The registration service is only present on Postgres
    /// profiles; configuration-only deployments fall back to the registry.
    /// </summary>
    private static ISceneRegistrationService? ResolveRegistrationService(ServerCallContext context)
        => context.GetHttpContext()?.RequestServices.GetService<ISceneRegistrationService>();
}
