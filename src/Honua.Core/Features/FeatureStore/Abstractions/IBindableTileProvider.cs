// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Services;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Optional provider seam for native tile implementations that need the secure connection and
/// physical storage mapping selected for a Metadata v2 publication.
/// </summary>
public interface IBindableTileProvider
{
    /// <summary>
    /// Creates a native tile provider scoped to the supplied feature-provider binding.
    /// Implementations must not fall back to an unbound/default connection when the binding
    /// identifies a distinct source.
    /// </summary>
    /// <param name="binding">Resolved publication, storage mapping, provider, and connection.</param>
    /// <returns>A tile provider bound to the selected source.</returns>
    ITileProvider CreateTileProviderForBinding(FeatureProviderBinding binding);
}
