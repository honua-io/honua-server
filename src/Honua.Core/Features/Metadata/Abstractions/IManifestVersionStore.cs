// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain;

namespace Honua.Core.Features.Metadata.Abstractions;

/// <summary>
/// Abstraction for storing and retrieving manifest version snapshots.
/// </summary>
public interface IManifestVersionStore
{
    /// <summary>
    /// Stores an immutable manifest version snapshot.
    /// </summary>
    Task StoreAsync(ManifestVersionEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists manifest versions ordered by applied time descending.
    /// </summary>
    Task<IReadOnlyList<ManifestVersionSummary>> ListAsync(int limit = 50, int offset = 0, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a manifest version by its unique identifier.
    /// </summary>
    Task<ManifestVersionEntry?> GetAsync(string versionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the most recently applied manifest version, or null if none exists.
    /// </summary>
    Task<ManifestVersionEntry?> GetLatestAsync(CancellationToken cancellationToken = default);
}
