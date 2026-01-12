// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.Infrastructure.Abstractions;

/// <summary>
/// Resolves CRS identifiers into canonical definitions backed by the spatial reference registry.
/// </summary>
public interface ICrsRegistry
{
    /// <summary>
    /// Resolves a CRS identifier (URI, EPSG code, or URN) into a canonical definition.
    /// </summary>
    /// <param name="crsIdentifier">CRS identifier (URI, EPSG code, or URN). Null yields the default CRS.</param>
    /// <param name="cancellationToken">Cancellation token for async resolution.</param>
    /// <returns>Resolved CRS definition or null if not supported.</returns>
    ValueTask<CrsDefinition?> ResolveAsync(string? crsIdentifier, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a CRS definition by SRID.
    /// </summary>
    /// <param name="srid">EPSG SRID.</param>
    /// <param name="cancellationToken">Cancellation token for async resolution.</param>
    /// <returns>Resolved CRS definition or null if not supported.</returns>
    ValueTask<CrsDefinition?> ResolveBySridAsync(int srid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if the SRID is supported by the registry.
    /// </summary>
    /// <param name="srid">EPSG SRID.</param>
    /// <param name="cancellationToken">Cancellation token for async resolution.</param>
    /// <returns>True if supported; otherwise false.</returns>
    ValueTask<bool> IsSridSupportedAsync(int srid, CancellationToken cancellationToken = default);
}
