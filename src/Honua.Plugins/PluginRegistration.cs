// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Plugins;

/// <summary>
/// Manifest entry for a single registered plugin, captured at startup from its
/// <c>[Plugin]</c> attribute and the extension points it implements. Used for health
/// reporting, dependency resolution, and diagnostics.
/// </summary>
/// <param name="Manifest">The parsed plugin manifest (identity, capabilities, dependencies).</param>
/// <param name="ImplementationType">The concrete plugin type.</param>
/// <param name="ProvidesValidator">Whether the plugin implements <c>IFeatureValidator</c>.</param>
/// <param name="ProvidesFieldValidator">Whether the plugin implements <c>IFieldValidator</c>.</param>
/// <param name="ProvidesEditHook">Whether the plugin implements <c>IEditHook</c>.</param>
/// <param name="ProvidesComputedField">Whether the plugin implements <c>IComputedFieldProvider</c>.</param>
/// <param name="ProvidesBackgroundService">Whether the plugin implements <c>IPluginBackgroundService</c>.</param>
/// <param name="ProvidesCustomEndpoint">Whether the plugin implements <c>ICustomEndpoint</c>.</param>
/// <param name="ProvidesOutputFormat">Whether the plugin implements <c>IFeatureOutputFormat</c>.</param>
/// <param name="ProvidesDataStore">Whether the plugin implements <c>IFeatureDataProvider</c> (read-only data store).</param>
public sealed record PluginRegistration(
    PluginManifest Manifest,
    Type ImplementationType,
    bool ProvidesValidator,
    bool ProvidesFieldValidator,
    bool ProvidesEditHook,
    bool ProvidesComputedField,
    bool ProvidesBackgroundService,
    bool ProvidesCustomEndpoint,
    bool ProvidesOutputFormat,
    bool ProvidesDataStore)
{
    /// <summary>Gets the stable plugin identifier.</summary>
    public string Id => Manifest.Id;

    /// <summary>Gets the plugin semantic version string.</summary>
    public string Version => Manifest.VersionString();

    /// <summary>Gets the optional human-readable description.</summary>
    public string? Description => Manifest.Description;
}

/// <summary>
/// Immutable inventory of the plugins registered at startup. Registered as a singleton so
/// health checks and diagnostics can report what is loaded without touching the DI graph.
/// </summary>
public sealed class PluginCatalog
{
    /// <summary>An empty catalog (no plugins registered).</summary>
    public static PluginCatalog Empty { get; } = new([]);

    /// <summary>Initializes a new instance of the <see cref="PluginCatalog"/> class.</summary>
    /// <param name="plugins">The registered plugin manifests.</param>
    public PluginCatalog(IReadOnlyList<PluginRegistration> plugins)
    {
        Plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
    }

    /// <summary>Gets the registered plugin manifests.</summary>
    public IReadOnlyList<PluginRegistration> Plugins { get; }
}
