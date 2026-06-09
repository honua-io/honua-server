// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Plugins.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Plugins;

/// <summary>
/// DI registration for the Honua plugin/extension SDK (issue #347).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the plugin edit pipeline and any compile-time plugins supplied via
    /// <paramref name="configure"/>. The pipeline is always registered (as a no-op when no
    /// plugins are present or the Enterprise <c>plugin.sdk</c> entitlement is inactive) so
    /// protocol handlers can depend on <see cref="IPluginEditPipeline"/> unconditionally.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration (binds the <c>Plugins</c> section).</param>
    /// <param name="configure">Optional callback to register plugin types via the builder.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddHonuaPlugins(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<IHonuaPluginBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<PluginOptions>(configuration.GetSection(PluginOptions.SectionName));

        var builder = new HonuaPluginBuilder(services);
        configure?.Invoke(builder);

        services.TryAddSingleton(new PluginCatalog(builder.Registrations));
        services.TryAddSingleton<IPluginEditPipeline, PluginEditPipeline>();

        return services;
    }
}
