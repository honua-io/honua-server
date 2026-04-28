// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Provider-level feature access seam with capability reporting.
/// </summary>
public interface IFeatureDataProvider
{
    /// <summary>
    /// Canonical provider name, for example <c>postgis</c> or <c>duckdb</c>.
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Capabilities advertised by this provider.
    /// </summary>
    FeatureProviderCapabilities Capabilities { get; }

    /// <summary>
    /// Read interface for query, count, extent, and statistics paths.
    /// </summary>
    IFeatureReader Reader { get; }

    /// <summary>
    /// Optional write interface for edit paths. Null means the provider is read-only.
    /// </summary>
    IFeatureWriter? Writer { get; }
}

/// <summary>
/// Registry of feature data providers available in the current runtime.
/// </summary>
public interface IFeatureDataProviderRegistry
{
    /// <summary>
    /// Providers registered in the runtime.
    /// </summary>
    IReadOnlyCollection<IFeatureDataProvider> Providers { get; }

    /// <summary>
    /// Resolves a provider by canonical name or alias.
    /// </summary>
    /// <param name="providerName">Provider name or alias.</param>
    /// <param name="provider">Resolved provider when found.</param>
    /// <returns>True when a matching provider is registered.</returns>
    bool TryGetProvider(string providerName, out IFeatureDataProvider provider);
}
