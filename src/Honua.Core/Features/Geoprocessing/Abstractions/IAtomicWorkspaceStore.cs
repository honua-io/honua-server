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
    /// Creates a workspace while atomically enforcing the owner's active workspace
    /// count limit. Null uses the built-in limit. Concurrent creation shares the gate.
    /// </summary>
    Task<Workspace> CreateWithQuotaAsync(Workspace proposal, int? maxWorkspaceCount = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns an unexpired active workspace matching the proposed owner, scope and
    /// label, or atomically creates the proposal. Concurrent callers share one result.
    /// A new proposal enforces the owner's active workspace count limit; an existing
    /// match remains usable at the limit. Null uses the built-in limit.
    /// </summary>
    Task<Workspace> GetOrCreateNamedAsync(Workspace proposal, int? maxWorkspaceCount = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds or atomically replaces an available artifact with the same case-insensitive
    /// label. Returns null on a collision when overwrite is disabled. Failure preserves
    /// the previous artifact. The workspace must still be active and unexpired at write time.
    /// </summary>
    Task<Artifact?> AddOrReplaceAsync(Artifact artifact, bool overwrite, CancellationToken cancellationToken = default);
}
