// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Server.Features.Console.Models;

namespace Honua.Server.Features.Console.Services;

public sealed class ConfigCatalogDiscoveryRegistryStore : ICatalogDiscoveryRegistryStore
{
    private readonly ConcurrentDictionary<string, CatalogDiscoveryRegistry> _registriesByWorkspace;
    private readonly ConcurrentDictionary<(string Workspace, string Endpoint), CatalogEndpointDetail> _details;
    private readonly ConcurrentDictionary<(string Workspace, string Endpoint, string Item), CatalogItem> _items;

    public ConfigCatalogDiscoveryRegistryStore(IEnumerable<CatalogDiscoveryWorkspaceSeed>? seeds = null)
    {
        _registriesByWorkspace = new ConcurrentDictionary<string, CatalogDiscoveryRegistry>(StringComparer.OrdinalIgnoreCase);
        _details = new ConcurrentDictionary<(string, string), CatalogEndpointDetail>(WorkspaceEndpointComparer.Instance);
        _items = new ConcurrentDictionary<(string, string, string), CatalogItem>(WorkspaceEndpointItemComparer.Instance);

        if (seeds is null)
        {
            return;
        }

        foreach (var seed in seeds)
        {
            Ingest(seed);
        }
    }

    public Task<CatalogDiscoveryRegistry?> GetRegistryAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        _registriesByWorkspace.TryGetValue(workspaceId, out var registry);
        return Task.FromResult(registry);
    }

    public Task<CatalogEndpointDetail?> GetEndpointAsync(string workspaceId, string endpointKey, CancellationToken cancellationToken = default)
    {
        if (!_registriesByWorkspace.ContainsKey(workspaceId))
        {
            return Task.FromResult<CatalogEndpointDetail?>(null);
        }

        _details.TryGetValue((workspaceId, endpointKey), out var detail);
        return Task.FromResult(detail);
    }

    public Task<CatalogItem?> GetItemAsync(string workspaceId, string endpointKey, string itemId, CancellationToken cancellationToken = default)
    {
        if (!_details.ContainsKey((workspaceId, endpointKey)))
        {
            return Task.FromResult<CatalogItem?>(null);
        }

        _items.TryGetValue((workspaceId, endpointKey, itemId), out var item);
        return Task.FromResult(item);
    }

    private void Ingest(CatalogDiscoveryWorkspaceSeed seed)
    {
        var endpoints = seed.Endpoints.Select(e => e.Endpoint).ToList();
        var registry = new CatalogDiscoveryRegistry
        {
            WorkspaceId = seed.WorkspaceId,
            WorkspaceName = seed.WorkspaceName,
            PublicHost = seed.PublicHost,
            Endpoints = endpoints,
            AutoDefaultCount = endpoints.Count(e => e.AutoDefault),
            OptInCount = endpoints.Count(e => !e.AutoDefault),
        };
        _registriesByWorkspace[seed.WorkspaceId] = registry;

        foreach (var endpoint in seed.Endpoints)
        {
            _details[(seed.WorkspaceId, endpoint.Endpoint.Key)] = new CatalogEndpointDetail
            {
                Endpoint = endpoint.Endpoint,
                LastRebuild = endpoint.LastRebuild,
                AutoMirror = endpoint.Endpoint.AutoDefault,
                Items = endpoint.Items.Select(i => i.Row).ToList(),
            };

            foreach (var item in endpoint.Items)
            {
                _items[(seed.WorkspaceId, endpoint.Endpoint.Key, item.Row.Id)] = item.Detail;
            }
        }
    }

    private sealed class WorkspaceEndpointComparer : IEqualityComparer<(string Workspace, string Endpoint)>
    {
        public static readonly WorkspaceEndpointComparer Instance = new();

        public bool Equals((string Workspace, string Endpoint) x, (string Workspace, string Endpoint) y)
            => string.Equals(x.Workspace, y.Workspace, StringComparison.OrdinalIgnoreCase)
               && string.Equals(x.Endpoint, y.Endpoint, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Workspace, string Endpoint) obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Workspace),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Endpoint));
    }

    private sealed class WorkspaceEndpointItemComparer : IEqualityComparer<(string Workspace, string Endpoint, string Item)>
    {
        public static readonly WorkspaceEndpointItemComparer Instance = new();

        public bool Equals((string Workspace, string Endpoint, string Item) x, (string Workspace, string Endpoint, string Item) y)
            => string.Equals(x.Workspace, y.Workspace, StringComparison.OrdinalIgnoreCase)
               && string.Equals(x.Endpoint, y.Endpoint, StringComparison.OrdinalIgnoreCase)
               && string.Equals(x.Item, y.Item, StringComparison.Ordinal);

        public int GetHashCode((string Workspace, string Endpoint, string Item) obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Workspace),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Endpoint),
                StringComparer.Ordinal.GetHashCode(obj.Item));
    }
}

public sealed record CatalogDiscoveryWorkspaceSeed
{
    public required string WorkspaceId { get; init; }

    public string? WorkspaceName { get; init; }

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
