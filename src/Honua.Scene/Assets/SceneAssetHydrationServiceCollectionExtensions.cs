// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Scene.Abstractions;
using Honua.Scene.Assets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Infrastructure.Scene;

/// <summary>
/// DI registration for the scene asset read-through materialization cache
/// (#2459, ADR-0060).
/// </summary>
public static class SceneAssetHydrationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="ISceneAssetHydrator"/> that hydrates a node-local
    /// scene asset cache from the shared object store before serving. Registered as
    /// a singleton so the per-scene download locks are shared across requests. The
    /// scene dataset registry resolves it optionally, so this registration is inert
    /// for datasets with no storage prefix.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddSceneAssetHydration(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ISceneAssetHydrator, SceneAssetHydrator>();
        return services;
    }
}
