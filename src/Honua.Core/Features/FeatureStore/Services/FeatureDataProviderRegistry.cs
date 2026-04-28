// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Services;

/// <summary>
/// In-memory registry of feature data providers registered by the composition root.
/// </summary>
public sealed class FeatureDataProviderRegistry : IFeatureDataProviderRegistry
{
    private readonly Dictionary<string, IFeatureDataProvider> _providers;

    /// <summary>
    /// Creates a registry from the provided feature data providers.
    /// </summary>
    /// <param name="providers">Registered provider implementations.</param>
    public FeatureDataProviderRegistry(IEnumerable<IFeatureDataProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        _providers = new Dictionary<string, IFeatureDataProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            var providerName = DataProviderNames.Normalize(provider.ProviderName);
            _providers[providerName] = provider;

            if (providerName.Equals(DataProviderNames.Postgis, StringComparison.OrdinalIgnoreCase))
            {
                _providers[DataProviderNames.PostgreSql] = provider;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<IFeatureDataProvider> Providers => _providers.Values;

    /// <inheritdoc />
    public bool TryGetProvider(string providerName, out IFeatureDataProvider provider)
        => _providers.TryGetValue(DataProviderNames.Normalize(providerName), out provider!);
}
