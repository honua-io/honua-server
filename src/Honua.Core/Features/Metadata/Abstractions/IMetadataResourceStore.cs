// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain;

namespace Honua.Core.Features.Metadata.Abstractions;

/// <summary>
/// Abstraction for storing metadata resources and derived artifacts.
/// </summary>
public interface IMetadataResourceStore
{
    /// <summary>
    /// Gets a resource by kind, namespace, and name.
    /// </summary>
    Task<MetadataResource?> GetAsync(MetadataResourceIdentifier identifier, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists resources filtered by kind and namespace.
    /// </summary>
    Task<IReadOnlyList<MetadataResource>> ListAsync(
        string? kind = null,
        string? @namespace = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new resource in the store.
    /// </summary>
    Task<MetadataResourceWriteResult> CreateAsync(
        MetadataResource resource,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing resource in the store using optimistic concurrency.
    /// </summary>
    Task<MetadataResourceWriteResult> UpdateAsync(
        MetadataResource resource,
        long expectedResourceVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a resource from the store using optimistic concurrency.
    /// </summary>
    Task<MetadataResourceWriteResult> DeleteAsync(
        MetadataResourceIdentifier identifier,
        long expectedResourceVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores or updates a compiled artifact for a resource version.
    /// </summary>
    Task StoreCompiledArtifactAsync(
        CompiledMetadataArtifact artifact,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a compiled artifact for a resource version.
    /// </summary>
    Task<CompiledMetadataArtifact?> GetCompiledArtifactAsync(
        string resourceId,
        string resourceVersion,
        CancellationToken cancellationToken = default);
}
