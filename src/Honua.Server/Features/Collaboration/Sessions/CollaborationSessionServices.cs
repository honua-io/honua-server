// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Linq;
using System.Security.Claims;
using Honua.Server.Features.Collaboration.FeatureLocks;
using Honua.Server.Features.Collaboration.Operations;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Honua.Server.Features.Collaboration.Sessions;

internal static class CollaborationSessionServices
{
    public static IServiceCollection AddCollaborationSessionTransport(this IServiceCollection services)
    {
        services.TryAddSingleton<ICollaborationSessionClock, SystemCollaborationSessionClock>();
        services.TryAddSingleton<ISavedMapCollaborationAuthorizer, FailClosedSavedMapCollaborationAuthorizer>();

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

        services.TryAddSingleton<InMemoryCollaborationSessionService>();
        // The transport has no leave/heartbeat HTTP surface yet; the background sweep is what
        // keeps the singleton presence/outbox state bounded when participants stop polling.
        services.AddHostedService<CollaborationSessionPruneService>();
        services.AddFeatureLockCollaboration();
        services.AddSavedMapOperationLog();
        return services;
    }
}

internal sealed class SystemCollaborationSessionClock : ICollaborationSessionClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>
/// Placeholder saved-map capability gate. Until durable saved-map ACLs land, the
/// collaboration transport denies joins by default so tests and future features must
/// explicitly provide the real authorizer or a narrow fixture authorizer.
/// </summary>
internal sealed class FailClosedSavedMapCollaborationAuthorizer : ISavedMapCollaborationAuthorizer
{
    public ValueTask<SavedMapCollaborationAuthorizationResult> AuthorizeJoinAsync(
        string mapId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        ArgumentNullException.ThrowIfNull(principal);

        return principal.Identity?.IsAuthenticated == true
            ? ValueTask.FromResult(SavedMapCollaborationAuthorizationResult.Forbid(
                "Saved-map collaboration authorization is not configured."))
            : ValueTask.FromResult(SavedMapCollaborationAuthorizationResult.RequireAuthentication(
                "Authentication is required to join a saved-map collaboration session."));
    }
}
