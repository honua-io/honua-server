// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Plugins;

/// <summary>
/// Manifest entry for a single registered plugin, captured at startup from its
/// <c>[Plugin]</c> attribute and the extension points it implements. Used for health
/// reporting and diagnostics.
/// </summary>
/// <param name="Id">Stable plugin identifier.</param>
/// <param name="Version">Plugin semantic version.</param>
/// <param name="Description">Optional human-readable description.</param>
/// <param name="ImplementationType">The concrete plugin type.</param>
/// <param name="ProvidesValidator">Whether the plugin implements <c>IFeatureValidator</c>.</param>
/// <param name="ProvidesEditHook">Whether the plugin implements <c>IEditHook</c>.</param>
public sealed record PluginRegistration(
    string Id,
    string Version,
    string? Description,
    Type ImplementationType,
    bool ProvidesValidator,
    bool ProvidesEditHook);

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
