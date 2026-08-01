// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Linq;
using Honua.Server.Features.Collaboration.FeatureLocks;
using Honua.Server.Features.Collaboration.Operations;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Honua.Server.Features.Collaboration.Sessions;

internal static class CollaborationSessionServices
{
    public static IServiceCollection AddCollaborationSessionTransport(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.TryAddSingleton<ICollaborationSessionClock, SystemCollaborationSessionClock>();
        // Studio-lifecycle-backed authorization (#2999): joins/edits/checkpoints are scoped to a
        // Studio draft or content item and follow the Studio identity model (admin family plus
        // owner semantics under the end-user flag). Unresolvable map ids stay fail-closed.
        services.TryAddSingleton<ISavedMapCollaborationAuthorizer, StudioSavedMapCollaborationAuthorizer>();

        // Redis backplane for multi-replica presence/cursor fan-out (#971/#1290). Registered only
        // when a multiplexer is configured so single-node and Redis-less deployments fall back to
        // local-only delivery via the default no-op backplane. The same instance is the publish
        // seam and the hosted subscriber, mirroring the feature-stream cluster-broadcast pattern.
        if (services.Any(static d => d.ServiceType == typeof(IConnectionMultiplexer)))
        {
            services.TryAddSingleton<RedisCollaborationSessionBackplane>();
            services.TryAddSingleton<ICollaborationSessionBackplane>(
                static sp => sp.GetRequiredService<RedisCollaborationSessionBackplane>());
            services.AddHostedService(static sp => sp.GetRequiredService<RedisCollaborationSessionBackplane>());
        }
        else
        {
            services.TryAddSingleton<ICollaborationSessionBackplane>(NullCollaborationSessionBackplane.Instance);
        }

        // Deployment topology drives the collaboration fail-closed rules; it is deliberately an
        // operator declaration (Deployment:Mode / Collaboration:MultiReplica) rather than an
        // inference from Redis presence, since a single instance commonly uses Redis for
        // cache/jobs and must keep full live co-editing (honua-server#2999 review).
        services.TryAddSingleton<SavedMapCollaborationTopology>();
        // The advertised session capabilities must match what the endpoints will accept: when
        // edits fail closed, the stream advertises operations/replay as unavailable.
        services.TryAddSingleton(static sp =>
        {
            var topology = sp.GetRequiredService<SavedMapCollaborationTopology>();
            var log = sp.GetRequiredService<Core.Features.Collaboration.Operations.ISavedMapOperationLogRepository>();
            var backplane = sp.GetRequiredService<ICollaborationSessionBackplane>();
            // Live operation delivery needs BOTH: the shared log lets a peer replica replay an
            // operation, but SavedMapOperationAppendCoordinator.PublishOperation reaches other
            // replicas exclusively through the backplane, so with the no-op backplane a
            // participant on another replica never receives a committed operation even though
            // the log is shared. Replay stays dependent on the log alone, which is the seam it
            // actually uses (honua-server#2999 review).
            var sharedLog = !topology.IsMultiReplica || log.SupportsReplicaSharedReplay;
            var operationsAvailable = sharedLog && (!topology.IsMultiReplica || backplane.SupportsCrossReplicaDelivery);

            // Presence (cursors/selections/follow) rides the BACKPLANE, not the operation log,
            // so it is advertised only when one actually reaches peer replicas. MultiReplica can
            // be declared without Redis — configuration validation does not require it for that
            // override alone — and the no-op backplane would otherwise promise live presence
            // that participants on other replicas never receive (honua-server#2999 review).
            var presenceAvailable = !topology.IsMultiReplica || backplane.SupportsCrossReplicaDelivery;
            // Checkpointing additionally needs a restart-durable log: it mints an immutable
            // version and must not claim completeness it cannot prove. It proves that from the
            // LOG, so it keys on the shared-log condition rather than on live delivery
            // (honua-server#2999 review).
            var checkpointsAvailable = sharedLog && log.SupportsRestartDurableReplay;
            var capabilities = CollaborationCapabilities.Default with
            {
                Checkpoints = checkpointsAvailable,
                Cursors = presenceAvailable,
                Selections = presenceAvailable,
                Follow = presenceAvailable,
            };

            return capabilities with
            {
                Operations = operationsAvailable,
                Replay = sharedLog,
            };
        });
        services.TryAddSingleton<InMemoryCollaborationSessionService>();
        // The background sweep keeps the singleton presence/outbox state bounded when
        // participants stop polling or a socket dies without a close frame.
        services.AddHostedService<CollaborationSessionPruneService>();
        services.AddFeatureLockCollaboration();
        services.AddSavedMapOperationLog();
        // Checkpoint-facing facade pairing the full-log replay with the replica-continuity
        // proof that gates it (honua-server#2999); keeps the endpoint within DI limits.
        services.TryAddSingleton<Checkpoints.SavedMapCheckpointOperationLog>();
        return services;
    }
}

internal sealed class SystemCollaborationSessionClock : ICollaborationSessionClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
