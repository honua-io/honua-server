// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Geoprocessing.Abstractions;

/// <summary>
/// Transactional workspace operations shared by production storage providers.
/// </summary>
public interface IAtomicWorkspaceStore
{
    /// <summary>
    /// Returns an unexpired active workspace matching the proposed owner, scope and
    /// label, or atomically creates the proposal. Concurrent callers share one result.
    /// </summary>
    Task<Workspace> GetOrCreateNamedAsync(Workspace proposal, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds or atomically replaces an available artifact with the same case-insensitive
    /// label. Returns null on a collision when overwrite is disabled. Failure preserves
    /// the previous artifact. The workspace must still be active and unexpired at write time.
    /// </summary>
    Task<Artifact?> AddOrReplaceAsync(Artifact artifact, bool overwrite, CancellationToken cancellationToken = default);
}
