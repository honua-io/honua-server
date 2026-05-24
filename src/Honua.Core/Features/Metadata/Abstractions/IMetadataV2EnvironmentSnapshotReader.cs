// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Metadata.Abstractions;

/// <summary>
/// Environment-aware read access to Metadata v2 graph snapshots for admin and release workflows.
/// </summary>
public interface IMetadataV2EnvironmentSnapshotReader
{
    /// <summary>
    /// Returns the current Metadata v2 graph snapshot for an environment, or null when unavailable.
    /// </summary>
    ValueTask<MetadataV2GraphSnapshot?> GetCurrentAsync(
        string environment,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a specific Metadata v2 graph revision for an environment, or null when unavailable.
    /// </summary>
    ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(
        string environment,
        long revision,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists current revision metadata for requested environments.
    /// </summary>
    IAsyncEnumerable<MetadataV2EnvironmentRevision> ListCurrentRevisionsAsync(
        IReadOnlyList<string> environments,
        CancellationToken cancellationToken = default);
}
