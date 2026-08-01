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
        // edits fail closed, the stream advertises operations/replay as unavailable. Computed on
        // demand rather than snapshotted at construction — see CollaborationCapabilitySource.
        services.TryAddSingleton<CollaborationCapabilitySource>();
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
