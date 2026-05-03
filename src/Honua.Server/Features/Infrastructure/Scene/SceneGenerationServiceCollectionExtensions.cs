// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Publishing.Abstractions;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Postgres.Features.Scene;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Infrastructure.Scene;

/// <summary>
/// DI registration for the v1 3D Tiles generation pipeline (#842).
/// </summary>
internal static class SceneGenerationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the scene generation executor and its Postgres-backed
    /// feature source. The executor is published as both itself (for direct
    /// invocation by the admin endpoint) and as <see cref="IPublishExecutor"/>
    /// for future dispatch wiring.
    /// </summary>
    /// <remarks>
    /// No-op when the active <c>DataSource:Provider</c> is not Postgres; the
    /// admin endpoint mapping is gated by the same condition through
    /// <c>IServiceProviderIsService</c> so non-Postgres profiles see no scene
    /// generation surface.
    /// </remarks>
    public static IServiceCollection AddSceneGeneration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!UsesPostgresProvider(configuration))
        {
            return services;
        }

        services.Configure<SceneGenerationServerOptions>(
            configuration.GetSection(SceneGenerationServerOptions.SectionName));

        services.TryAddScoped<ISceneFeatureSource, PostgresSceneFeatureSource>();
        services.TryAddScoped<SceneTilesPublishExecutor>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IPublishExecutor, SceneTilesPublishExecutor>());

        return services;
    }

    private static bool UsesPostgresProvider(IConfiguration configuration)
    {
        var provider = configuration.GetValue<string>("DataSource:Provider");
        return string.IsNullOrWhiteSpace(provider)
            || provider.Equals("postgres", StringComparison.OrdinalIgnoreCase)
            || provider.Equals("postgresql", StringComparison.OrdinalIgnoreCase)
            || provider.Equals("postgis", StringComparison.OrdinalIgnoreCase);
    }
}
