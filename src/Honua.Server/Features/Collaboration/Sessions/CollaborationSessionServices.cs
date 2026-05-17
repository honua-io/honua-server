// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Server.Features.Collaboration.FeatureLocks;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Collaboration.Sessions;

internal static class CollaborationSessionServices
{
    public static IServiceCollection AddCollaborationSessionTransport(this IServiceCollection services)
    {
        services.TryAddSingleton<ICollaborationSessionClock, SystemCollaborationSessionClock>();
        services.TryAddSingleton<ISavedMapCollaborationAuthorizer, FailClosedSavedMapCollaborationAuthorizer>();
        services.TryAddSingleton<InMemoryCollaborationSessionService>();
        services.AddFeatureLockCollaboration();
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
