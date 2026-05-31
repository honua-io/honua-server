// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Server.Features.Console.Models;

namespace Honua.Server.Features.Console.Services;

/// <summary>
/// Configuration-backed read model for the Console catalog discovery-endpoints
/// registry (honua-server#1279). The discovery dialects a server publishes are
/// a server-wide concern owned by config/metadata; this store materialises that
/// configuration into the Console projection. The default seed mirrors the five
/// supported dialects so the Console Catalogs surface binds immediately, while
/// the per-workspace map can be replaced/extended by configuration without
/// touching the endpoints.
/// </summary>
public sealed class ConfigCatalogDiscoveryRegistryStore : ICatalogDiscoveryRegistryStore
{
    private readonly ConcurrentDictionary<string, CatalogDiscoveryRegistry> _registriesByWorkspace;
    private readonly ConcurrentDictionary<(string Workspace, string Endpoint), CatalogEndpointDetail> _details;
    private readonly ConcurrentDictionary<(string Workspace, string Endpoint, string Item), CatalogItem> _items;

    /// <summary>
    /// Creates a store seeded with the supplied per-workspace registries. When
    /// no seed is supplied a single default workspace is materialised.
    /// </summary>
    public ConfigCatalogDiscoveryRegistryStore(IEnumerable<CatalogDiscoveryWorkspaceSeed>? seeds = null)
    {
        _registriesByWorkspace = new ConcurrentDictionary<string, CatalogDiscoveryRegistry>(StringComparer.OrdinalIgnoreCase);
        _details = new ConcurrentDictionary<(string, string), CatalogEndpointDetail>(WorkspaceEndpointComparer.Instance);
        _items = new ConcurrentDictionary<(string, string, string), CatalogItem>(WorkspaceEndpointItemComparer.Instance);

        var materialised = (seeds ?? DefaultSeeds()).ToList();
        foreach (var seed in materialised)
        {
            Ingest(seed);
        }
    }

    /// <inheritdoc />
    public Task<CatalogDiscoveryRegistry?> GetRegistryAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        _registriesByWorkspace.TryGetValue(workspaceId, out var registry);
        return Task.FromResult(registry);
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

    private static IEnumerable<CatalogDiscoveryWorkspaceSeed> DefaultSeeds()
    {
        yield return new CatalogDiscoveryWorkspaceSeed
        {
            WorkspaceId = "default",
            WorkspaceName = "Default workspace",
            PublicHost = "https://localhost:8080",
            Endpoints =
            [
                BuildEsriEndpoint(),
                BuildOgcEndpoint(),
                BuildODataEndpoint(),
                BuildStacEndpoint(),
                BuildDcatEndpoint(),
            ],
        };
    }

    private static CatalogEndpointSeed BuildEsriEndpoint()
    {
        var itemDetail = new CatalogItem
        {
            Id = "parcels",
            Title = "Parcels",
            AutoMirror = true,
            Live = true,
            BackingServiceCount = 1,
            TagCount = 2,
            IssueCount = 0,
            Groups =
            [
                new CatalogItemFieldGroup
                {
                    Title = "Identity",
                    Subtitle = "Resource-derived",
                    Scope = "resource-derived",
                    Fields =
                    [
                        new CatalogItemField { Label = "Item id", State = CatalogFieldStates.System, Value = "parcels" },
                        new CatalogItemField { Label = "Type", State = CatalogFieldStates.Calculated, Value = "Feature Layer", DerivedFrom = "Parcels FeatureServer" },
                    ],
                },
                new CatalogItemFieldGroup
                {
                    Title = "Catalog presentation",
                    Subtitle = "Catalog-only input",
                    Scope = "catalog-only",
                    Fields =
                    [
                        new CatalogItemField { Label = "Title", State = CatalogFieldStates.Input, Value = "Parcels" },
                        new CatalogItemField { Label = "Tags", State = CatalogFieldStates.Input, Value = "parcels, cadastre" },
                    ],
                },
                new CatalogItemFieldGroup
                {
                    Title = "Service bindings",
                    Scope = "resource-derived",
                    Fields =
                    [
                        new CatalogItemField { Label = "FeatureServer", State = CatalogFieldStates.Calculated, Value = "/rest/services/Parcels/FeatureServer", DerivedFrom = "Parcels FeatureServer" },
                    ],
                },
                new CatalogItemFieldGroup
                {
                    Title = "Standards mapping",
                    Scope = "resource-derived",
                    Fields =
                    [
                        new CatalogItemField { Label = "ISO 19115", State = CatalogFieldStates.Calculated, Value = "dataset", DerivedFrom = "Parcels FeatureServer" },
                    ],
                },
            ],
        };

        return new CatalogEndpointSeed
        {
            Endpoint = new CatalogEndpoint
            {
                Key = "esri",
                Title = "Esri catalog",
                Description = "ArcGIS-compatible portal item discovery.",
                Dialect = CatalogDialects.Esri,
                Enabled = true,
                AutoDefault = true,
                Url = "/rest/portal",
                Entries = 1,
                FedBy = "FeatureServer, MapServer, ImageServer",
                Feeders =
                [
                    new CatalogFeeder { Kind = "feature-server", Label = "Parcels FeatureServer" },
                ],
                IssueCount = 0,
            },
            LastRebuild = "2026-05-01T00:00:00Z",
            Items =
            [
                new CatalogItemSeed
                {
                    Row = new CatalogEndpointItem
                    {
                        Id = "parcels",
                        Title = "Parcels",
                        FromService = "Parcels FeatureServer",
                        Resource = "/rest/services/Parcels/FeatureServer",
                        Tags = ["parcels", "cadastre"],
                        HasThumbnail = false,
                        License = "CC-BY-4.0",
                        IssueCount = 0,
                        Updated = "2026-05-01T00:00:00Z",
                    },
                    Detail = itemDetail,
                },
            ],
        };
    }

    private static CatalogEndpointSeed BuildOgcEndpoint() => new()
    {
        Endpoint = new CatalogEndpoint
        {
            Key = "ogc",
            Title = "OGC API Records",
            Description = "OGC API - Records catalogue of published collections.",
            Dialect = CatalogDialects.Ogc,
            Enabled = true,
            AutoDefault = true,
            Url = "/ogc/features/collections",
            Entries = 0,
            FedBy = "OGC API Features collections",
            Feeders = [new CatalogFeeder { Kind = "ogc-features", Label = "OGC API Features" }],
            IssueCount = 0,
        },
        LastRebuild = "2026-05-01T00:00:00Z",
        Items = [],
    };

    private static CatalogEndpointSeed BuildODataEndpoint() => new()
    {
        Endpoint = new CatalogEndpoint
        {
            Key = "odata",
            Title = "OData v4",
            Description = "OData v4 service document of exposed entity sets.",
            Dialect = CatalogDialects.ODataV4,
            Enabled = false,
            AutoDefault = false,
            Url = "/odata",
            Entries = 0,
            FedBy = "OData entity sets",
            Feeders = [new CatalogFeeder { Kind = "odata-entity-set", Label = "OData entity sets" }],
            IssueCount = 0,
        },
        LastRebuild = null,
        Items = [],
    };

    private static CatalogEndpointSeed BuildStacEndpoint() => new()
    {
        Endpoint = new CatalogEndpoint
        {
            Key = "stac",
            Title = "STAC",
            Description = "SpatioTemporal Asset Catalog of raster/coverage assets.",
            Dialect = CatalogDialects.Stac,
            Enabled = true,
            AutoDefault = false,
            Url = "/stac",
            Entries = 0,
            FedBy = "ImageServer, coverages",
            Feeders = [new CatalogFeeder { Kind = "image-server", Label = "ImageServer" }],
            IssueCount = 0,
        },
        LastRebuild = "2026-05-01T00:00:00Z",
        Items = [],
    };

    private static CatalogEndpointSeed BuildDcatEndpoint() => new()
    {
        Endpoint = new CatalogEndpoint
        {
            Key = "dcat",
            Title = "DCAT",
            Description = "DCAT data-catalog feed for open-data portals.",
            Dialect = CatalogDialects.Dcat,
            Enabled = false,
            AutoDefault = false,
            Url = "/dcat/catalog.json",
            Entries = 0,
            FedBy = "Published open-data items",
            Feeders = [new CatalogFeeder { Kind = "open-data", Label = "Open-data items" }],
            IssueCount = 0,
        },
        LastRebuild = null,
        Items = [],
    };

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
