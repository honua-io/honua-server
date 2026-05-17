// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Collaboration.FeatureLocks;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Collaboration.FeatureLocks;

internal static class FeatureLockServices
{
    public static IServiceCollection AddFeatureLockCollaboration(this IServiceCollection services)
    {
        services.TryAddSingleton<IFeatureLockService, InMemoryFeatureLockService>();
        services.TryAddSingleton<IFeatureLockAuthorizer, FailClosedFeatureLockAuthorizer>();
        return services;
    }
}

/// <summary>
/// Placeholder feature edit capability gate. Until saved-map and service-layer
/// edit ACLs land, feature locks deny writes by default so callers must wire a
/// real policy before production mutation paths can acquire leases.
/// </summary>
internal sealed class FailClosedFeatureLockAuthorizer : IFeatureLockAuthorizer
{
    public ValueTask<FeatureLockAuthorizationResult> AuthorizeAsync(
        string mapId,
        FeatureRef feature,
        ClaimsPrincipal principal,
        string operation,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(principal);

        return principal.Identity?.IsAuthenticated == true
            ? ValueTask.FromResult(FeatureLockAuthorizationResult.Forbid(
                "Feature lock authorization is not configured."))
            : ValueTask.FromResult(FeatureLockAuthorizationResult.RequireAuthentication(
                "Authentication is required to acquire a feature lock."));
    }
}
