// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Collaboration.FeatureLocks;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Collaboration.FeatureLocks;

internal static class FeatureLockServices
{
    public static IServiceCollection AddFeatureLockCollaboration(this IServiceCollection services)
    {
        services.TryAddSingleton<IFeatureLockService, InMemoryFeatureLockService>();
        services.TryAddSingleton<IFeatureEditGuard, FeatureEditGuard>();
        services.TryAddSingleton<IFeatureLockAuthorizer, FailClosedFeatureLockAuthorizer>();
        return services;
    }
}

// Until real edit ACLs exist, feature locks deny writes so a placeholder cannot acquire a production lease.
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
