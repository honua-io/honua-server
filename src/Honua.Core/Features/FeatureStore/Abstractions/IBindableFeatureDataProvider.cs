// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Services;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Optional provider seam for backends that need binding-scoped state (for example, the
/// resolved <see cref="Honua.Core.Features.Security.Domain.DataConnection"/>) when servicing
/// a request. Providers that do not need per-binding state should not implement this interface;
/// the router falls back to <see cref="IFeatureDataProvider.Reader"/>.
/// </summary>
public interface IBindableFeatureDataProvider
{
    /// <summary>
    /// Returns a reader bound to the supplied <see cref="FeatureProviderBinding"/>. The binding
    /// carries the resolved <see cref="Honua.Core.Features.Security.Domain.DataConnection"/> for
    /// services that route through a per-tenant or secure connection. Implementations must not
    /// mutate shared state; each call should produce a reader scoped to the binding.
    /// </summary>
    /// <param name="binding">Resolved provider binding.</param>
    /// <returns>Reader bound to the supplied binding.</returns>
    IFeatureReader CreateReaderForBinding(FeatureProviderBinding binding);
}
