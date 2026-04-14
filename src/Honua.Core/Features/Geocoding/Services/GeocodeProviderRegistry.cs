// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Geocoding.Abstractions;
using Honua.Core.Features.Geocoding.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Geocoding.Services;

internal sealed class GeocodeProviderRegistry : IGeocodeProviderRegistry, IGeocodeProviderFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IOptions<GeocodingConfiguration> _configuration;
    private readonly ConcurrentDictionary<string, IGeocodeProvider> _providers;
    private readonly ConcurrentDictionary<string, Func<IServiceProvider, IGeocodeProvider>> _factories =
        new(StringComparer.OrdinalIgnoreCase);

    public GeocodeProviderRegistry(
        IServiceProvider serviceProvider,
        IEnumerable<IGeocodeProvider> providers,
        IOptions<GeocodingConfiguration> configuration)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(configuration);

        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _providers = new ConcurrentDictionary<string, IGeocodeProvider>(
            providers.ToDictionary(provider => provider.Name, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
    }

    public void RegisterProvider(IGeocodeProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _providers[provider.Name] = provider;
    }

    public void RegisterProvider(string providerName, Func<IServiceProvider, IGeocodeProvider> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentNullException.ThrowIfNull(factory);

        _factories[providerName] = factory;
    }

    public IGeocodeProvider? GetProvider(string providerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        if (_providers.TryGetValue(providerName, out var provider))
        {
            return provider;
        }

        if (_factories.TryGetValue(providerName, out var factory))
        {
            provider = factory(_serviceProvider);
            _providers[providerName] = provider;
            return provider;
        }

        return null;
    }

    public IReadOnlyList<IGeocodeProvider> GetAllProviders()
    {
        foreach (var providerName in _factories.Keys)
        {
            _ = GetProvider(providerName);
        }

        return OrderProviders(_providers.Values).ToArray();
    }

    public IReadOnlyList<string> GetProvidersByPriority()
    {
        return GetAllProviders()
            .Select(provider => provider.Name)
            .ToArray();
    }

    public bool IsProviderRegistered(string providerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        return _providers.ContainsKey(providerName) || _factories.ContainsKey(providerName);
    }

    public bool UnregisterProvider(string providerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        var removedProvider = _providers.TryRemove(providerName, out _);
        var removedFactory = _factories.TryRemove(providerName, out _);
        return removedProvider || removedFactory;
    }

    public IGeocodeProvider? CreateProvider(string providerName)
    {
        return GetProvider(providerName);
    }

    public IReadOnlyList<string> GetAvailableProviders()
    {
        return GetProvidersByPriority();
    }

    public IGeocodeProvider GetDefaultProvider()
    {
        var defaultProviderName = _configuration.Value.DefaultProvider;
        var provider = GetProvider(defaultProviderName);
        if (provider != null)
        {
            return provider;
        }

        return GetAllProviders().FirstOrDefault()
            ?? throw new InvalidOperationException("No geocoding providers are registered.");
    }

    private IEnumerable<IGeocodeProvider> OrderProviders(IEnumerable<IGeocodeProvider> providers)
    {
        return providers
            .OrderByDescending(provider => GetProviderPriority(provider.Name))
            .ThenBy(provider => provider.Name, StringComparer.OrdinalIgnoreCase);
    }

    private int GetProviderPriority(string providerName)
    {
        var providers = _configuration.Value.Providers;
        return providerName.ToLowerInvariant() switch
        {
            GeocodeProviderNames.Nominatim => providers.Nominatim.Priority,
            GeocodeProviderNames.AmazonLocation => providers.AmazonLocation.Priority,
            GeocodeProviderNames.AzureMaps => providers.AzureMaps.Priority,
            _ => 0
        };
    }
}
