// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Plugins.Abstractions;

public sealed class NoOpFeatureOutputFormatRegistry : IFeatureOutputFormatRegistry
{
    public static NoOpFeatureOutputFormatRegistry Instance { get; } = new();

    public bool HasFormats => false;

    public IReadOnlyCollection<PluginOutputFormatDescriptor> AdvertisedFormats { get; } = [];

    public bool TryGetFormat(string formatId, out IFeatureOutputFormat? format)
    {
        format = null;
        return false;
    }
}
