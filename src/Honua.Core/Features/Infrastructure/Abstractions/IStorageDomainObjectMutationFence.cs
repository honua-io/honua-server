// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Abstractions;

/// <summary>
/// Serializes physical mutations of one immutable provider object across processes,
/// validates its reservation, and participates in the domain-wide census fence.
/// The implementation is configured by the host, never by request data.
/// </summary>
public interface IStorageDomainObjectMutationFence : IStorageDomainMutationFence
{
    /// <summary>Enters a reserved object's complete write or deletion lifetime.</summary>
    /// <param name="objectKey">Exact provider-relative object identity.</param>
    /// <param name="maximumBytes">Exact reserved upper bound for a new write; null for deletion.</param>
    /// <param name="cancellationToken">Cancellation while waiting for admission.</param>
    /// <returns>An independently owned physical mutation lease.</returns>
    ValueTask<IAsyncDisposable> EnterObjectMutationAsync(string objectKey, long? maximumBytes,
        CancellationToken cancellationToken = default);
}
