// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Console.Models;
using Honua.Server.Features.Console.Services;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Console;

/// <summary>
/// Unit tests for <see cref="ConfigCatalogDiscoveryRegistryStore"/> (honua-server#1279,
/// #1283 follow-up): an unconfigured store fabricates nothing, and configured seeds are
/// projected verbatim. The store no longer ships hard-coded synthetic dialect endpoints.
/// </summary>
public sealed class ConfigCatalogDiscoveryRegistryStoreTests
{
    [UnitTest]
    public async Task GetRegistry_WhenUnconfigured_ReturnsNullForEveryWorkspace()
    {
        // No seeds supplied: an unconfigured deployment must publish no discovery
        // endpoints rather than fabricated sample data (no-fabrication principle).
        var store = new ConfigCatalogDiscoveryRegistryStore();

        Assert.Null(await store.GetRegistryAsync("default"));
        Assert.Null(await store.GetRegistryAsync("any-workspace"));
        Assert.Null(await store.GetEndpointAsync("default", "ogc"));
        Assert.Null(await store.GetItemAsync("default", "esri", "parcels"));
    }

    [UnitTest]
    public async Task GetRegistry_WithEmptySeedList_ReturnsNoWorkspaces()
    {
        // An explicit empty seed list is still "configured but with nothing" — no
        // synthetic fallback may slip in.
        var store = new ConfigCatalogDiscoveryRegistryStore(Array.Empty<CatalogDiscoveryWorkspaceSeed>());

        Assert.Null(await store.GetRegistryAsync("default"));
    }

    [UnitTest]
    public async Task GetRegistry_WithSeed_ProjectsConfiguredEndpointsOnly()
    {
        // The OGC API Records card must point at the real records endpoint, not the
        // OGC API Features collections path (the old synthetic seed used the wrong URL).
        const string recordsCollectionsUrl = "/ogc/records/collections";
        var store = new ConfigCatalogDiscoveryRegistryStore(new[]
        {
            new CatalogDiscoveryWorkspaceSeed
            {
                WorkspaceId = "ws",
                WorkspaceName = "Workspace",
                PublicHost = "https://example.test",
                Endpoints =
                [
                    new CatalogEndpointSeed
                    {
                        Endpoint = new CatalogEndpoint
                        {
                            Key = "ogc",
                            Title = "OGC API Records",
                            Dialect = CatalogDialects.Ogc,
                            Enabled = true,
                            AutoDefault = true,
                            Url = recordsCollectionsUrl,
                            FedBy = recordsCollectionsUrl,
                            Entries = 0,
                            IssueCount = 0,
                        },
                        Items = [],
                    },
                ],
            },
        });

        var registry = await store.GetRegistryAsync("ws");
        Assert.NotNull(registry);
        var ogc = Assert.Single(registry!.Endpoints);
        Assert.Equal("ogc", ogc.Key);
        Assert.Equal(recordsCollectionsUrl, ogc.Url);
        Assert.Equal(recordsCollectionsUrl, ogc.FedBy);
        Assert.DoesNotContain("/ogc/features/collections", ogc.Url, StringComparison.Ordinal);
    }
}
