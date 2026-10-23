// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Abstractions;

/// <summary>
/// Supports recovery of a progress store's distributed backplane without enumerating jobs.
/// </summary>
public interface IProgressStoreRecovery
{
    /// <summary>
    /// Probes an unavailable backplane when its retry interval permits. Healthy stores
    /// perform no work; probe cost must not depend on the number of stored jobs.
    /// Consumers must recheck their coordination health before accepting work.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ProbeRecoveryAsync(CancellationToken cancellationToken = default);
}
