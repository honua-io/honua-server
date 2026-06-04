// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Grpc.Core;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Catalog;
using Honua.Core.Features.Scene.Domain;
using Honua.Infrastructure.Scene;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Proto = Geospatial.V1;

namespace Honua.Scene.Grpc;

/// <summary>
/// gRPC <c>SceneService</c> implementation (honua-server#1194 / #1195). Mirrors
/// the public HTTP scene discovery surface (<c>/api/scenes</c>): it merges
/// database-backed scene records (when a registration service is present) with
/// configuration-backed hosted-tile datasets, returning the same catalog over
/// the typed gRPC contract.
/// </summary>
internal sealed partial class HonuaSceneGrpcService : Proto.SceneService.SceneServiceBase
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

        using var activity = HonuaTelemetry.StartActivity(SceneGrpcTelemetry.ListScenesActivity);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Grpc);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, SceneGrpcTelemetry.ListScenesOperation);

        var registration = ResolveRegistrationService(context);

        // ListScenes intentionally mirrors the public HTTP scene discovery
        // catalog (SceneDiscoveryEndpoints), which lists every active scene —
        // including protected ones — and advertises their auth posture rather
        // than gating the listing itself. Per-scene authorization is enforced
        // when metadata or tiles are actually fetched (GetScene / GetTile /
        // StreamTiles), so the discovery surface stays at parity across
        // protocols. Keep this method ungated to preserve that parity.
        //
        // The active, deduped, ordered catalog merge (database-over-config
        // precedence) is shared with the HTTP discovery surface via SceneCatalog;
        // this adapter only maps to the proto DTO and applies the gRPC-specific
        // spatial-extent filter.
        var catalog = await SceneCatalog
            .ListActiveAsync(registration, _registry, _options, context.CancellationToken)
            .ConfigureAwait(false);

        var scenes = new List<Proto.SceneMetadata>(catalog.Count);
        foreach (var resolved in catalog)
        {
            scenes.Add(resolved.Record is not null
                ? SceneGrpcMapping.ToSceneMetadata(resolved.Record)
                : SceneGrpcMapping.ToSceneMetadata(resolved.Scene!));
        }

        // Apply the proto's optional spatial constraint: when an extent filter is
        // supplied, include only scenes whose advertised extent intersects it.
        // Scenes that carry no extent are included (their footprint is unknown, so
        // they cannot be excluded). Mirrors SceneTileCatalog.IntersectsExtent.
        // SceneCatalog already orders by id (Ordinal), so preserve that order.
        var filter = request.Extent?.Extent;
        var ordered = filter is null
            ? scenes
            : scenes.Where(scene => SceneIntersectsExtent(scene, filter)).ToList();

        var offset = Math.Max(0, request.ResultOffset);
        var count = request.ResultRecordCount;

        // Intentional protocol divergence (documented per CLAUDE.md "If a protocol
        // intentionally diverges ... document why in code and add direct
        // endpoint-level tests"): the proto comments result_record_count's zero as
        // "server default", but the scene catalog is a small, bounded set of
        // published scenes (not an unbounded feature table), so a zero/unset count
        // returns the entire catalog rather than imposing an arbitrary default page
        // size. This keeps a typical "list all my scenes" call complete in one round
        // trip. Covered by ListScenes_ZeroResultRecordCount_ReturnsEntireCatalog.
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
        activity?.SetTag(SceneGrpcTelemetry.SceneCountTag, response.Scenes.Count);
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
            Log.InvalidArgument(_logger, SceneGrpcTelemetry.GetSceneOperation, "scene_id is required.");
            throw new RpcException(new Status(StatusCode.InvalidArgument, "scene_id is required."));
        }

        using var activity = HonuaTelemetry.StartActivity(SceneGrpcTelemetry.GetSceneActivity);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Grpc);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, SceneGrpcTelemetry.GetSceneOperation);
        activity?.SetTag(SceneGrpcTelemetry.SceneIdTag, request.SceneId);

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
                    Log.SceneNotFound(_logger, SceneGrpcTelemetry.GetSceneOperation, request.SceneId);
                    throw new RpcException(new Status(StatusCode.NotFound, $"Scene '{request.SceneId}' was not found."));
                }

                // Enforce the same per-scene authorization the HTTP/I3S surfaces
                // apply before returning metadata for a protected scene, deriving
                // the policy through the shared canonical projection so the gRPC
                // surface cannot drift from the registry's mapping.
                SceneAccessGuard.EnforceReadAccess(context, SceneServingProjection.ToAccessPolicy(record));

                return new Proto.GetSceneResponse { Scene = SceneGrpcMapping.ToSceneMetadata(record) };
            }
        }

        var scene = await _registry.FindAsync(request.SceneId, context.CancellationToken).ConfigureAwait(false);
        if (scene is null)
        {
            Log.SceneNotFound(_logger, SceneGrpcTelemetry.GetSceneOperation, request.SceneId);
            throw new RpcException(new Status(StatusCode.NotFound, $"Scene '{request.SceneId}' was not found."));
        }

        SceneAccessGuard.EnforceReadAccess(context, scene.AccessPolicy);

        return new Proto.GetSceneResponse { Scene = SceneGrpcMapping.ToSceneMetadata(scene) };
    }

    /// <summary>
    /// Tests whether a scene's advertised 2D extent intersects the request's
    /// horizontal filter box. Scenes without an extent are treated as a match
    /// (their footprint is unknown and cannot be excluded).
    /// </summary>
    private static bool SceneIntersectsExtent(Proto.SceneMetadata scene, Proto.Extent filter)
    {
        var sceneExtent = scene.Extent?.Extent;
        if (sceneExtent is null)
        {
            return true;
        }

        return SceneTileCatalog.EnvelopesIntersect(
            sceneExtent.Xmin, sceneExtent.Ymin, sceneExtent.Xmax, sceneExtent.Ymax,
            filter.Xmin, filter.Ymin, filter.Xmax, filter.Ymax);
    }

    /// <summary>
    /// Resolves the optional <see cref="ISceneRegistrationService"/> from the
    /// request scope. The registration service is only present on Postgres
    /// profiles; configuration-only deployments fall back to the registry.
    /// </summary>
    private static ISceneRegistrationService? ResolveRegistrationService(ServerCallContext context)
        => context.GetHttpContext()?.RequestServices.GetService<ISceneRegistrationService>();

    private static partial class Log
    {
        [LoggerMessage(EventId = 8443, Level = LogLevel.Debug,
            Message = "gRPC scene {Operation} rejected: {Reason}")]
        public static partial void InvalidArgument(ILogger logger, string operation, string reason);

        [LoggerMessage(EventId = 8444, Level = LogLevel.Debug,
            Message = "gRPC scene {Operation} did not find scene {SceneId}.")]
        public static partial void SceneNotFound(ILogger logger, string operation, string sceneId);
    }
}
