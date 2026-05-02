// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Postgres.Features.Scene;

/// <summary>
/// DI registration helper for the Postgres-backed scene dataset registry
/// introduced in #844.
/// </summary>
internal static class SceneDatasetServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Postgres-backed <see cref="ISceneDatasetRegistry"/> and
    /// <see cref="ISceneRegistrationService"/>, falling back to a
    /// configuration-backed registry for entries that are not persisted in
    /// Postgres.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Order with respect to <c>AddScene(...)</c> is irrelevant. The
    /// configuration-backed registry registered by <c>AddScene</c> is preserved
    /// as <see cref="IConfigurationSceneDatasetRegistry"/> so it can be used as
    /// a fallback for local-dev/test scenarios where datasets are declared via
    /// the <c>Scenes:Datasets</c> configuration section.
    /// </para>
    /// <para>
    /// When the active <c>DataSource:Provider</c> is not Postgres (e.g. DuckDB),
    /// this method is a no-op: the configuration-backed registry stays the
    /// authoritative <see cref="ISceneDatasetRegistry"/>, and
    /// <see cref="ISceneRegistrationService"/> is left unregistered so the
    /// admin endpoints decline to map themselves.
    /// </para>
    /// </remarks>
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
                : new CompositeSceneDatasetRegistry(primary, fallback);
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

/// <summary>
/// Marker interface used to chain a non-Postgres scene registry behind the
/// Postgres-backed primary. The configuration-backed registry installed by
/// <c>AddScene</c> is promoted to this alias so it remains available as a
/// fallback without conflicting on <see cref="ISceneDatasetRegistry"/>.
/// </summary>
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
    private readonly ISceneDatasetRegistry _fallback;

    public CompositeSceneDatasetRegistry(ISceneDatasetRegistry primary, ISceneDatasetRegistry fallback)
    {
        _primary = primary;
        _fallback = fallback;
    }

    public async ValueTask<SceneDataset?> FindAsync(string id, CancellationToken cancellationToken = default)
    {
        var hit = await _primary.FindAsync(id, cancellationToken).ConfigureAwait(false);
        return hit ?? await _fallback.FindAsync(id, cancellationToken).ConfigureAwait(false);
    }
}
