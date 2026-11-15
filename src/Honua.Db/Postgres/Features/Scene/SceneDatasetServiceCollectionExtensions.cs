// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Db.Postgres.Features.Scene;

/// <summary>
/// DI registration helper for the Postgres-backed scene dataset registry
/// introduced in #844.
/// </summary>
internal static class SceneDatasetServiceCollectionExtensions
{
    public static IServiceCollection AddPostgresSceneRegistry(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!UsesPostgresProvider(configuration))
        {
            return services;
        }

        // Promote any prior ISceneDatasetRegistry registration (typically
        // ConfigurationSceneDatasetRegistry from AddScene) to the
        // IConfigurationSceneDatasetRegistry alias so the composite below can
        // delegate to it without colliding on the primary interface.
        for (var i = services.Count - 1; i >= 0; i--)
        {
            var descriptor = services[i];
            if (descriptor.ServiceType != typeof(ISceneDatasetRegistry))
            {
                continue;
            }

            services.RemoveAt(i);
            services.Add(new ServiceDescriptor(
                typeof(IConfigurationSceneDatasetRegistry),
                sp => new ConfigurationSceneDatasetRegistryAdapter(
                    (ISceneDatasetRegistry)CreateInstance(sp, descriptor)),
                descriptor.Lifetime));
        }

        services.AddScoped<PostgresSceneDatasetRegistry>();
        services.AddScoped<ISceneRegistrationService>(sp => sp.GetRequiredService<PostgresSceneDatasetRegistry>());

        services.AddScoped<ISceneDatasetRegistry>(sp =>
        {
            var primary = sp.GetRequiredService<PostgresSceneDatasetRegistry>();
            var fallback = sp.GetService<IConfigurationSceneDatasetRegistry>();
            return fallback is null
                ? primary
                : new CompositeSceneDatasetRegistry(primary, primary, fallback);
        });

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

    private static object CreateInstance(IServiceProvider provider, ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is { } instance)
        {
            return instance;
        }

        if (descriptor.ImplementationFactory is { } factory)
        {
            return factory(provider);
        }

        if (descriptor.ImplementationType is { } implementationType)
        {
            return ActivatorUtilities.CreateInstance(provider, implementationType);
        }

        throw new InvalidOperationException(
            "Unsupported ServiceDescriptor shape for ISceneDatasetRegistry promotion.");
    }
}

internal interface IConfigurationSceneDatasetRegistry : ISceneDatasetRegistry;

internal sealed class ConfigurationSceneDatasetRegistryAdapter : IConfigurationSceneDatasetRegistry
{
    private readonly ISceneDatasetRegistry _inner;

    public ConfigurationSceneDatasetRegistryAdapter(ISceneDatasetRegistry inner)
    {
        _inner = inner;
    }

    public ValueTask<SceneDataset?> FindAsync(string id, CancellationToken cancellationToken = default)
        => _inner.FindAsync(id, cancellationToken);
}

internal sealed class CompositeSceneDatasetRegistry : ISceneDatasetRegistry
{
    private readonly ISceneDatasetRegistry _primary;
    private readonly ISceneRegistrationService _primaryOwnership;
    private readonly ISceneDatasetRegistry _fallback;

    public CompositeSceneDatasetRegistry(
        ISceneDatasetRegistry primary,
        ISceneRegistrationService primaryOwnership,
        ISceneDatasetRegistry fallback)
    {
        _primary = primary;
        _primaryOwnership = primaryOwnership;
        _fallback = fallback;
    }

    public async ValueTask<SceneDataset?> FindAsync(string id, CancellationToken cancellationToken = default)
    {
        var hit = await _primary.FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (hit is not null)
        {
            return hit;
        }

        // Don't let the configuration-backed fallback resurrect a slug that
        // is owned by the primary store but inactive — deactivation must
        // remain authoritative regardless of any matching configuration entry.
        var owned = await _primaryOwnership.GetBySceneIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (owned is not null)
        {
            return null;
        }

        return await _fallback.FindAsync(id, cancellationToken).ConfigureAwait(false);
    }
}
