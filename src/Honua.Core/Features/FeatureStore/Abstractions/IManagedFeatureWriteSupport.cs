// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Abstractions;

public interface IManagedFeatureWriteSupport
{
    Task<bool> SupportsManagedWritesAsync(int storageLayerId, CancellationToken cancellationToken = default);
}
