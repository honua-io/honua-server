// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Scene.Abstractions;
using Honua.Server.Features.Infrastructure.Scene;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Protocols.Scene;

/// <summary>
/// DI registration for the hosted 3D Tiles scene serving feature.
/// </summary>
internal static class SceneServiceCollectionExtensions
{
    /// <summary>
    /// Registers the scene dataset registry and binds <see cref="SceneDatasetOptions"/>
    /// from the <c>Scenes</c> configuration section.
    /// </summary>
    public static IServiceCollection AddScene(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<SceneDatasetOptions>(configuration.GetSection(SceneDatasetOptions.SectionName));
        services.TryAddSingleton<ISceneDatasetRegistry, ConfigurationSceneDatasetRegistry>();

        return services;
    }
}
