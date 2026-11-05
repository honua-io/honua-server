// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Plugins;

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
    public string Id => Manifest.Id;

    public string Version => Manifest.VersionString();

    public string? Description => Manifest.Description;
}

public sealed class PluginCatalog
{
    public static PluginCatalog Empty { get; } = new([]);

    public PluginCatalog(IReadOnlyList<PluginRegistration> plugins)
    {
        Plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
    }

    public IReadOnlyList<PluginRegistration> Plugins { get; }
}
