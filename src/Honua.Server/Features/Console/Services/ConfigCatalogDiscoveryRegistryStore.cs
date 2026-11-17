// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Server.Features.Console.Models;

namespace Honua.Server.Features.Console.Services;

public sealed class ConfigCatalogDiscoveryRegistryStore : ICatalogDiscoveryRegistryStore
{
    private readonly ConcurrentDictionary<string, CatalogDiscoveryRegistry> _registriesByEnvironment;
    private readonly ConcurrentDictionary<(string Environment, string Endpoint), CatalogEndpointDetail> _details;
    private readonly ConcurrentDictionary<(string Environment, string Endpoint, string Item), CatalogItem> _items;

    public ConfigCatalogDiscoveryRegistryStore(IEnumerable<CatalogDiscoveryEnvironmentSeed>? seeds = null)
    {
        _registriesByEnvironment = new ConcurrentDictionary<string, CatalogDiscoveryRegistry>(StringComparer.OrdinalIgnoreCase);
        _details = new ConcurrentDictionary<(string, string), CatalogEndpointDetail>(EnvironmentEndpointComparer.Instance);
        _items = new ConcurrentDictionary<(string, string, string), CatalogItem>(EnvironmentEndpointItemComparer.Instance);

        if (seeds is null)
        {
            return;
        }

        foreach (var seed in seeds)
        {
            Ingest(seed);
        }
    }

    public Task<CatalogDiscoveryRegistry?> GetRegistryAsync(string environmentId, CancellationToken cancellationToken = default)
    {
        _registriesByEnvironment.TryGetValue(environmentId, out var registry);
        return Task.FromResult(registry);
    }

    public Task<CatalogEndpointDetail?> GetEndpointAsync(string environmentId, string endpointKey, CancellationToken cancellationToken = default)
    {
        if (!_registriesByEnvironment.ContainsKey(environmentId))
        {
            return Task.FromResult<CatalogEndpointDetail?>(null);
        }

        _details.TryGetValue((environmentId, endpointKey), out var detail);
        return Task.FromResult(detail);
    }

    public Task<CatalogItem?> GetItemAsync(string environmentId, string endpointKey, string itemId, CancellationToken cancellationToken = default)
    {
        if (!_details.ContainsKey((environmentId, endpointKey)))
        {
            return Task.FromResult<CatalogItem?>(null);
        }

        _items.TryGetValue((environmentId, endpointKey, itemId), out var item);
        return Task.FromResult(item);
    }

    private void Ingest(CatalogDiscoveryEnvironmentSeed seed)
    {
        var endpoints = seed.Endpoints.Select(e => e.Endpoint).ToList();
        var registry = new CatalogDiscoveryRegistry
        {
            EnvironmentId = seed.EnvironmentId,
            EnvironmentName = seed.EnvironmentName,
            PublicHost = seed.PublicHost,
            Endpoints = endpoints,
            AutoDefaultCount = endpoints.Count(e => e.AutoDefault),
            OptInCount = endpoints.Count(e => !e.AutoDefault),
        };
        _registriesByEnvironment[seed.EnvironmentId] = registry;

        foreach (var endpoint in seed.Endpoints)
        {
            _details[(seed.EnvironmentId, endpoint.Endpoint.Key)] = new CatalogEndpointDetail
            {
                Endpoint = endpoint.Endpoint,
                LastRebuild = endpoint.LastRebuild,
                AutoMirror = endpoint.Endpoint.AutoDefault,
                Items = endpoint.Items.Select(i => i.Row).ToList(),
            };

            foreach (var item in endpoint.Items)
            {
                _items[(seed.EnvironmentId, endpoint.Endpoint.Key, item.Row.Id)] = item.Detail;
            }
        }
    }

    private sealed class EnvironmentEndpointComparer : IEqualityComparer<(string Environment, string Endpoint)>
    {
        public static readonly EnvironmentEndpointComparer Instance = new();

        public bool Equals((string Environment, string Endpoint) x, (string Environment, string Endpoint) y)
            => string.Equals(x.Environment, y.Environment, StringComparison.OrdinalIgnoreCase)
               && string.Equals(x.Endpoint, y.Endpoint, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Environment, string Endpoint) obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Environment),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Endpoint));
    }

    private sealed class EnvironmentEndpointItemComparer : IEqualityComparer<(string Environment, string Endpoint, string Item)>
    {
        public static readonly EnvironmentEndpointItemComparer Instance = new();

        public bool Equals((string Environment, string Endpoint, string Item) x, (string Environment, string Endpoint, string Item) y)
            => string.Equals(x.Environment, y.Environment, StringComparison.OrdinalIgnoreCase)
               && string.Equals(x.Endpoint, y.Endpoint, StringComparison.OrdinalIgnoreCase)
               && string.Equals(x.Item, y.Item, StringComparison.Ordinal);

        public int GetHashCode((string Environment, string Endpoint, string Item) obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Environment),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Endpoint),
                StringComparer.Ordinal.GetHashCode(obj.Item));
    }
}

public sealed record CatalogDiscoveryEnvironmentSeed
{
    public required string EnvironmentId { get; init; }

    public string? EnvironmentName { get; init; }

    public string? PublicHost { get; init; }

    public IReadOnlyList<CatalogEndpointSeed> Endpoints { get; init; } = Array.Empty<CatalogEndpointSeed>();
}

public sealed record CatalogEndpointSeed
{
    public required CatalogEndpoint Endpoint { get; init; }

    public string? LastRebuild { get; init; }

    public IReadOnlyList<CatalogItemSeed> Items { get; init; } = Array.Empty<CatalogItemSeed>();
}

public sealed record CatalogItemSeed
{
    public required CatalogEndpointItem Row { get; init; }

    public required CatalogItem Detail { get; init; }
}
