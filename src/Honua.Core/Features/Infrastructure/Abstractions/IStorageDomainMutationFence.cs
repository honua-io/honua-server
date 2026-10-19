// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Abstractions;

/// <summary>
/// Host-owned fence for physical mutations in one explicitly configured storage domain.
/// The lease covers the entire write or deletion, including its metadata publication.
/// It grants no request authority and must not be selected by callers.
/// </summary>
public interface IStorageDomainMutationFence
{
    /// <summary>
    /// Enters an initialized domain. Nested mutations must use independently held shared
    /// leases; no ambient flag may bypass initialization or the exclusive recovery fence.
    /// </summary>
    ValueTask<IAsyncDisposable> EnterMutationAsync(CancellationToken cancellationToken = default);
}
