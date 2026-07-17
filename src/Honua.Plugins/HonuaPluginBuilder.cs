// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Plugins.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Plugins;

/// <summary>
/// Default <see cref="IHonuaPluginBuilder"/>. Registers each plugin as a singleton and routes
/// the extension-point interfaces it implements to that same instance so a plugin that is, say,
/// both a validator and an edit hook is constructed once. Enforces the declarative capability
/// model: a plugin that implements a capability-gated extension point must declare the matching
/// <see cref="PluginCapability"/> on its <c>[Plugin]</c> manifest, or registration fails fast.
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

        var manifest = PluginManifest.FromAttribute(attribute);

        var providesValidator = typeof(IFeatureValidator).IsAssignableFrom(type);
        var providesFieldValidator = typeof(IFieldValidator).IsAssignableFrom(type);
        var providesEditHook = typeof(IEditHook).IsAssignableFrom(type);
        var providesComputedField = typeof(IComputedFieldProvider).IsAssignableFrom(type);
        var providesBackgroundService = typeof(IPluginBackgroundService).IsAssignableFrom(type);
        var providesCustomEndpoint = typeof(ICustomEndpoint).IsAssignableFrom(type);
        var providesOutputFormat = typeof(IFeatureOutputFormat).IsAssignableFrom(type);
        var providesDataStore = typeof(IFeatureDataProvider).IsAssignableFrom(type);

        if (!providesValidator && !providesFieldValidator && !providesEditHook
            && !providesComputedField && !providesBackgroundService && !providesCustomEndpoint
            && !providesOutputFormat && !providesDataStore)
        {
            throw new InvalidOperationException(
                $"Plugin '{manifest.Id}' ({type.FullName}) must implement at least one extension point "
                + "(IFeatureValidator, IFieldValidator, IEditHook, IComputedFieldProvider, "
                + "IPluginBackgroundService, ICustomEndpoint, IFeatureOutputFormat, or "
                + "IFeatureDataProvider).");
        }

        EnforceCapabilities(manifest, providesBackgroundService, providesCustomEndpoint, providesOutputFormat, providesDataStore);

        if (_registrations.Exists(r => string.Equals(r.Id, manifest.Id, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"A plugin with id '{manifest.Id}' is already registered.");
        }

        // One concrete instance, shared across the extension-point interfaces it implements.
        _services.TryAddSingleton<TPlugin>();
        if (providesValidator)
        {
            _services.AddSingleton<IFeatureValidator>(sp => (IFeatureValidator)sp.GetRequiredService<TPlugin>());
        }

        if (providesFieldValidator)
        {
            _services.AddSingleton<IFieldValidator>(sp => (IFieldValidator)sp.GetRequiredService<TPlugin>());
        }

        if (providesEditHook)
        {
            _services.AddSingleton<IEditHook>(sp => (IEditHook)sp.GetRequiredService<TPlugin>());
        }

        if (providesComputedField)
        {
            _services.AddSingleton<IComputedFieldProvider>(sp => (IComputedFieldProvider)sp.GetRequiredService<TPlugin>());
        }

        if (providesBackgroundService)
        {
            _services.AddSingleton<IPluginBackgroundService>(sp => (IPluginBackgroundService)sp.GetRequiredService<TPlugin>());
        }

        if (providesCustomEndpoint)
        {
            _services.AddSingleton<ICustomEndpoint>(sp => (ICustomEndpoint)sp.GetRequiredService<TPlugin>());
        }

        if (providesOutputFormat)
        {
            _services.AddSingleton<IFeatureOutputFormat>(sp => (IFeatureOutputFormat)sp.GetRequiredService<TPlugin>());
        }

        if (providesDataStore)
        {
            // Register the plugin as an additional IFeatureDataProvider. The existing
            // FeatureDataProviderRegistry (composed in the server's InfrastructureCompositionRoot)
            // enumerates GetServices<IFeatureDataProvider>() lazily, so the plugin provider is
            // routed by provider name with no router changes and independent of registration order
            // (issue #2856, ADR-0066). The provider shares the plugin's singleton instance, so — like
            // every other extension point — a data-store plugin must be thread-safe.
            _services.AddSingleton<IFeatureDataProvider>(sp => (IFeatureDataProvider)sp.GetRequiredService<TPlugin>());
        }

        _registrations.Add(new PluginRegistration(
            manifest,
            type,
            providesValidator,
            providesFieldValidator,
            providesEditHook,
            providesComputedField,
            providesBackgroundService,
            providesCustomEndpoint,
            providesOutputFormat,
            providesDataStore));

        return this;
    }

    private static void EnforceCapabilities(
        PluginManifest manifest,
        bool providesBackgroundService,
        bool providesCustomEndpoint,
        bool providesOutputFormat,
        bool providesDataStore)
    {
        if (providesBackgroundService
            && !manifest.Capabilities.HasFlag(PluginCapability.BackgroundExecution))
        {
            throw new InvalidOperationException(
                $"Plugin '{manifest.Id}' implements IPluginBackgroundService but does not declare the "
                + "BackgroundExecution capability. Add Capabilities = PluginCapability.BackgroundExecution "
                + "to its [Plugin] attribute.");
        }

        if (providesCustomEndpoint
            && !manifest.Capabilities.HasFlag(PluginCapability.CustomEndpoints))
        {
            throw new InvalidOperationException(
                $"Plugin '{manifest.Id}' implements ICustomEndpoint but does not declare the "
                + "CustomEndpoints capability. Add Capabilities = PluginCapability.CustomEndpoints "
                + "to its [Plugin] attribute.");
        }

        if (providesOutputFormat
            && !manifest.Capabilities.HasFlag(PluginCapability.OutputFormats))
        {
            throw new InvalidOperationException(
                $"Plugin '{manifest.Id}' implements IFeatureOutputFormat but does not declare the "
                + "OutputFormats capability. Add Capabilities = PluginCapability.OutputFormats "
                + "to its [Plugin] attribute.");
        }

        if (providesDataStore
            && !manifest.Capabilities.HasFlag(PluginCapability.DataStore))
        {
            throw new InvalidOperationException(
                $"Plugin '{manifest.Id}' implements IFeatureDataProvider but does not declare the "
                + "DataStore capability. Add Capabilities = PluginCapability.DataStore "
                + "to its [Plugin] attribute.");
        }
    }
}
