// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Portal.Abstractions;
using Honua.Core.Features.Portal.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Core.Features.Portal;

/// <summary>
/// Service collection extensions for registering the ArcGIS Portal projection
/// services consumed by the Portal/Sharing REST read surface (#1243).
/// </summary>
public static class PortalServiceCollectionExtensions
{
    /// <summary>
    /// Registers the RBAC-aware <see cref="IPortalItemProjector"/>. The projector
    /// depends on the shared <c>IAccessPolicyEvaluator</c>, which the host registers
    /// as part of the authentication/RBAC pipeline.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddPortalItemProjection(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IPortalItemProjector, PortalItemProjector>();
        return services;
    }
}
