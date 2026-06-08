// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Server.Features.Console.Models;

namespace Honua.Server.Features.Console.Services;

/// <summary>
/// Configuration-backed read model for the Console catalog discovery-endpoints
/// registry (honua-server#1279). The discovery dialects a server publishes are
/// a server-wide concern owned by config/metadata; this store materialises that
/// configuration into the Console projection. When no configuration is supplied
/// the store is empty: an unconfigured deployment publishes no discovery
/// endpoints rather than fabricated sample data (no-fabrication principle,
/// server analog of Console Charter §11). Real endpoints come only from actual
/// config/metadata seeds.
/// </summary>
public sealed class ConfigCatalogDiscoveryRegistryStore : ICatalogDiscoveryRegistryStore
{
    private readonly ConcurrentDictionary<string, CatalogDiscoveryRegistry> _registriesByWorkspace;
    private readonly ConcurrentDictionary<(string Workspace, string Endpoint), CatalogEndpointDetail> _details;
    private readonly ConcurrentDictionary<(string Workspace, string Endpoint, string Item), CatalogItem> _items;

    /// <summary>
    /// Creates a store seeded with the supplied per-workspace registries. When
    /// no seed is supplied the store is empty (no workspaces, no endpoints):
    /// an unconfigured deployment exposes no discovery endpoints rather than
    /// fabricated sample data.
    /// </summary>
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

    /// <inheritdoc />
    public Task<CatalogDiscoveryRegistry?> GetRegistryAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        // The discovery-endpoints registry is a workspace-scoped projection of a
        // server-wide concern: "which discovery dialects does this workspace
        // publish?". The honest answer for a workspace that has none configured
        // (including a fresh/unseeded deployment, where no workspace is seeded)
        // is an empty registry, not "workspace unknown". Returning an empty-but-
        // valid 200 lets the Console render an honest empty state instead of
        // misreading a 404 as "contract not bound". We therefore synthesise an
        // empty registry for any workspace that has no configured endpoints
        // rather than returning null. (Drill-down sub-resources keyed by
        // endpointKey/itemId still return null/404 for genuinely unknown keys.)
        if (!_registriesByWorkspace.TryGetValue(workspaceId, out var registry))
        {
            registry = new CatalogDiscoveryRegistry { WorkspaceId = workspaceId };
        }

        return Task.FromResult<CatalogDiscoveryRegistry?>(registry);
    }

    /// <inheritdoc />
    public Task<CatalogEndpointDetail?> GetEndpointAsync(string workspaceId, string endpointKey, CancellationToken cancellationToken = default)
    {
        if (!_registriesByWorkspace.ContainsKey(workspaceId))
        {
            return Task.FromResult<CatalogEndpointDetail?>(null);
        }

        _details.TryGetValue((workspaceId, endpointKey), out var detail);
        return Task.FromResult(detail);
    }

    /// <inheritdoc />
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

/// <summary>Configuration seed for one workspace's discovery-endpoints registry.</summary>
public sealed record CatalogDiscoveryWorkspaceSeed
{
    /// <summary>Workspace id.</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>Workspace display name.</summary>
    public string? WorkspaceName { get; init; }

    /// <summary>Public host that serves the discovery endpoints.</summary>
    public string? PublicHost { get; init; }

    /// <summary>The endpoint seeds.</summary>
    public IReadOnlyList<CatalogEndpointSeed> Endpoints { get; init; } = Array.Empty<CatalogEndpointSeed>();
}

/// <summary>Configuration seed for one discovery endpoint, including its detail and items.</summary>
public sealed record CatalogEndpointSeed
{
    /// <summary>The endpoint card.</summary>
    public required CatalogEndpoint Endpoint { get; init; }

    /// <summary>Last rebuild timestamp (ISO-8601), when known.</summary>
    public string? LastRebuild { get; init; }

    /// <summary>The endpoint's items.</summary>
    public IReadOnlyList<CatalogItemSeed> Items { get; init; } = Array.Empty<CatalogItemSeed>();
}

/// <summary>Configuration seed for one catalog item: its table row plus its editor detail.</summary>
public sealed record CatalogItemSeed
{
    /// <summary>The item row shown in the endpoint-detail items table.</summary>
    public required CatalogEndpointItem Row { get; init; }

    /// <summary>The item editor detail (field groups).</summary>
    public required CatalogItem Detail { get; init; }
}
