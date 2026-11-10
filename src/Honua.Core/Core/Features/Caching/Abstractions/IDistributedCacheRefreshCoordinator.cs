// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Caching.Abstractions;

public interface IDistributedCacheRefreshCoordinator : ICacheRefreshCoordinator
{
    bool IsDistributed { get; }

    Task NotifyInvalidationClusterWideAsync(string key, CancellationToken cancellationToken = default);
}
