// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Plugins.Abstractions;

public interface IFeatureOutputFormatRegistry
{
    bool HasFormats { get; }

    IReadOnlyCollection<PluginOutputFormatDescriptor> AdvertisedFormats { get; }

    bool TryGetFormat(string formatId, out IFeatureOutputFormat? format);
}
