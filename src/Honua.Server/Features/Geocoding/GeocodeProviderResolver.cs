// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Geocoding;

internal interface IGeocodeProviderResolver
{
    IGeocodeProvider Resolve(string? providerName = null);
}

internal sealed class GeocodeProviderResolver(
    IEnumerable<IGeocodeProvider> providers,
    IOptions<GeocodingOptions> options) : IGeocodeProviderResolver
{
    private readonly GeocodingOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly Dictionary<string, IGeocodeProvider> _providers = (providers ?? throw new ArgumentNullException(nameof(providers)))
        .GroupBy(static provider => provider.Name, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.OrdinalIgnoreCase);

    public IGeocodeProvider Resolve(string? providerName = null)
    {
        if (_providers.Count == 0)
        {
            throw new InvalidOperationException("No geocode providers are registered.");
        }

        var resolvedName = string.IsNullOrWhiteSpace(providerName)
            ? _options.DefaultProvider
            : providerName.Trim();

        if (_providers.TryGetValue(resolvedName, out var provider))
        {
            return provider;
        }

        throw new InvalidOperationException(
            $"Geocode provider '{resolvedName}' is not registered. Available providers: {string.Join(", ", _providers.Keys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase))}");
    }
}
