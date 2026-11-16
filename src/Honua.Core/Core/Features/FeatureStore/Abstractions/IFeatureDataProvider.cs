// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

public interface IFeatureDataProvider
{
    string ProviderName { get; }

    FeatureProviderCapabilities Capabilities { get; }

    IFeatureReader Reader { get; }

    IFeatureWriter? Writer { get; }
}

public interface IFeatureDataProviderRegistry
{
    IReadOnlyCollection<IFeatureDataProvider> Providers { get; }

    bool TryGetProvider(string providerName, out IFeatureDataProvider provider);
}
