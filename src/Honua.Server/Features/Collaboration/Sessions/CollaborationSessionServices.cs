// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Server.Features.Collaboration.FeatureLocks;
using Honua.Server.Features.Collaboration.Operations;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

        // The operator-declared topology (Deployment:Mode / Collaboration:MultiReplica) drives the
        // collaboration fail-closed rules.
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
