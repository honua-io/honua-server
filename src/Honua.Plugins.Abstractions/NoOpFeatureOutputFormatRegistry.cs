// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Plugins.Abstractions;

/// <summary>
/// The zero-overhead <see cref="IFeatureOutputFormatRegistry"/> used when the plugin SDK is not
/// licensed or no output-format plugins are registered. It advertises nothing and resolves nothing —
/// the common production path, and a convenient default for tests that construct an
/// export/negotiation seam directly.
/// </summary>
public sealed class NoOpFeatureOutputFormatRegistry : IFeatureOutputFormatRegistry
{
    /// <summary>A shared singleton instance.</summary>
    public static NoOpFeatureOutputFormatRegistry Instance { get; } = new();

    /// <inheritdoc />
    public bool HasFormats => false;

    /// <inheritdoc />
    public IReadOnlyCollection<PluginOutputFormatDescriptor> AdvertisedFormats { get; } = [];

    /// <inheritdoc />
    public bool TryGetFormat(string formatId, out IFeatureOutputFormat? format)
    {
        format = null;
        return false;
    }
}
