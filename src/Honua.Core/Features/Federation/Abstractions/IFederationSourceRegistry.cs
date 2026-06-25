// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Federation.Domain;

namespace Honua.Core.Features.Federation.Abstractions;

/// <summary>
/// Read access to the configured federated data sources for a deployment. Implementations
/// resolve sources from configuration (or, in a later increment, from the metadata-resource
/// store) and never expose transport credentials.
/// </summary>
public interface IFederationSourceRegistry
{
    /// <summary>
    /// Gets all configured federated sources, including disabled ones.
    /// </summary>
    ImmutableArray<FederatedSourceDescriptor> GetSources();

    /// <summary>
    /// Attempts to resolve a single source by its identifier.
    /// </summary>
    /// <param name="id">The source identifier (case-insensitive).</param>
    /// <param name="descriptor">The resolved descriptor when found.</param>
    /// <returns><see langword="true"/> when a source with the identifier exists.</returns>
    bool TryGetSource(string id, out FederatedSourceDescriptor descriptor);
}
