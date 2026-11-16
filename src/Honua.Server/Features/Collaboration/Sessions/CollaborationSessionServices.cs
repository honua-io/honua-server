// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Linq;
using Honua.Core.Features.Collaboration.Operations;
using Honua.Server.Features.Collaboration.FeatureLocks;
using Honua.Server.Features.Collaboration.Operations;
using Honua.Server.Features.Realtime;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Honua.Core.Features.Realtime;
using Npgsql;

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

        // The built-in fanout requires the database even on hosts configured without one.
        // Preserve standalone fake providers used by database-free collaboration tests.
        var usesDatabase = services.Any(static d =>
            d.ServiceType == typeof(NpgsqlDataSource) || d.ServiceType == typeof(PostgresRealtimeEventLog));
        IRealtimeFanoutProvider? ResolveFanout(IServiceProvider sp) =>
            usesDatabase && sp.GetService<NpgsqlDataSource>() is null
                ? null
                : sp.GetService<IRealtimeFanoutProvider>();

        if (services.Any(static d => d.ServiceType == typeof(IRealtimeFanoutProvider)) && usesDatabase)
        {
            services.TryAddSingleton<CollaborationRealtimeRelay>();
            services.AddHostedService<CollaborationRealtimeRelayHost>();
        }

        // Deployment topology drives the collaboration fail-closed rules; it is deliberately an
        // operator declaration (Deployment:Mode / Collaboration:MultiReplica) rather than an
        // inference from Redis presence, since a single instance commonly uses Redis for
        // cache/jobs and must keep full live co-editing (honua-server#2999 review).
        services.TryAddSingleton<SavedMapCollaborationTopology>();
        // The advertised session capabilities must match what the endpoints will accept: when
        // edits fail closed, the stream advertises operations/replay as unavailable. Computed on
        // demand rather than snapshotted at construction — see CollaborationCapabilitySource.
        services.TryAddSingleton(sp => new CollaborationCapabilitySource(
            sp.GetRequiredService<SavedMapCollaborationTopology>(),
            sp.GetRequiredService<ISavedMapOperationLogRepository>(),
            ResolveFanout(sp)));
        services.TryAddSingleton(sp => new CollaborationSessionRealtimeServices(
            ResolveFanout(sp),
            sp.GetRequiredService<CollaborationCapabilitySource>(),
            sp.GetService<NpgsqlDataSource>() is null ? null : sp.GetService<CollaborationRealtimeRelay>()));
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

// Test hosts may remove the database after application registration. Resolve the relay only
// after the final service collection is built, just like the realtime tail and retention hosts.
internal sealed class CollaborationRealtimeRelayHost(IServiceProvider services) : BackgroundService
{
    private CollaborationRealtimeRelay? _relay;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (services.GetService<NpgsqlDataSource>() is null)
        {
            return;
        }

        _relay = services.GetRequiredService<CollaborationRealtimeRelay>();
        await _relay.StartAsync(stoppingToken).ConfigureAwait(false);
        // Keep the relay task under Generic Host supervision so a fatal consumer failure
        // still follows the configured BackgroundServiceExceptionBehavior.
        if (_relay.ExecuteTask is { } execution)
        {
            await execution.ConfigureAwait(false);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_relay is not null)
            {
                await _relay.StopAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
