// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Plugins;

/// <summary>
/// Operator configuration for the plugin/extension SDK, bound from the <c>Plugins</c>
/// configuration section.
/// </summary>
public sealed class PluginOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Plugins";

    /// <summary>
    /// Operator kill-switch. When <see langword="false"/> the plugin edit pipeline runs as a
    /// no-op regardless of which plugins are compiled in or whether the SDK is licensed.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
