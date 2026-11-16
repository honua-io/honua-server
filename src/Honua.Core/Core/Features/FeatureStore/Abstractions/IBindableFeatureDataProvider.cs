// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Services;

namespace Honua.Core.Features.FeatureStore.Abstractions;

public interface IBindableFeatureDataProvider
{
    IFeatureReader CreateReaderForBinding(FeatureProviderBinding binding);
}
