// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Honua.Plugins.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Plugins;

/// <summary>
/// Default <see cref="IHonuaPluginBuilder"/>. Registers each plugin as a singleton and routes
/// the extension-point interfaces it implements to that same instance so a plugin that is both
/// a validator and an edit hook is constructed once.
/// </summary>
internal sealed class HonuaPluginBuilder(IServiceCollection services) : IHonuaPluginBuilder
{
    private readonly IServiceCollection _services = services ?? throw new ArgumentNullException(nameof(services));
    private readonly List<PluginRegistration> _registrations = [];

    public IReadOnlyList<PluginRegistration> Registrations => _registrations;

    public IHonuaPluginBuilder Add<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TPlugin>()
        where TPlugin : class
    {
        var type = typeof(TPlugin);

        var attribute = type.GetCustomAttribute<PluginAttribute>()
            ?? throw new InvalidOperationException(
                $"Plugin type '{type.FullName}' must be annotated with [Plugin(id, version)].");

        var providesValidator = typeof(IFeatureValidator).IsAssignableFrom(type);
        var providesEditHook = typeof(IEditHook).IsAssignableFrom(type);
        if (!providesValidator && !providesEditHook)
        {
            throw new InvalidOperationException(
                $"Plugin '{attribute.Id}' ({type.FullName}) must implement IFeatureValidator and/or IEditHook.");
        }

        if (_registrations.Exists(r => string.Equals(r.Id, attribute.Id, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"A plugin with id '{attribute.Id}' is already registered.");
        }

        // One concrete instance, shared across the extension-point interfaces it implements.
        _services.TryAddSingleton<TPlugin>();
        if (providesValidator)
        {
            _services.AddSingleton<IFeatureValidator>(sp => (IFeatureValidator)sp.GetRequiredService<TPlugin>());
        }

        if (providesEditHook)
        {
            _services.AddSingleton<IEditHook>(sp => (IEditHook)sp.GetRequiredService<TPlugin>());
        }

        _registrations.Add(new PluginRegistration(
            attribute.Id,
            attribute.Version,
            attribute.Description,
            type,
            providesValidator,
            providesEditHook));

        return this;
    }
}
