// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Console.Models;

namespace Honua.Server.Features.Console.Services;

/// <summary>
/// Read-model store for the Console catalog discovery-endpoints registry
/// (honua-server#1279). Describes which discovery dialects (Esri catalog, OGC
/// API Records, OData, STAC, DCAT) a workspace publishes to consumers, each
/// endpoint's on/off and auto-default-vs-opt-in state, its feeders, and the
/// per-endpoint/per-item drill-down. This is a read-only projection: the
/// authoritative endpoint configuration is owned by server config/metadata, so
/// implementations are not expected to support writes.
/// </summary>
public interface ICatalogDiscoveryRegistryStore
{
    /// <summary>
    /// Returns the discovery-endpoints registry for a workspace. A workspace
    /// with no published dialects — including a fresh/unconfigured deployment —
    /// returns a registry with an empty endpoint list (and zero aggregate
    /// counts) rather than <c>null</c>: "which dialects does this workspace
    /// publish?" has the honest answer "none", which the Console renders as an
    /// empty state. The nullable return is retained for implementations that
    /// genuinely cannot resolve the workspace, but the config-backed projection
    /// never returns <c>null</c> here.
    /// </summary>
    Task<CatalogDiscoveryRegistry?> GetRegistryAsync(string workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one discovery endpoint with its mirrored items, or <c>null</c>
    /// when the workspace or endpoint key is unknown.
    /// </summary>
    Task<CatalogEndpointDetail?> GetEndpointAsync(string workspaceId, string endpointKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one catalog item with its field groups, or <c>null</c> when the
    /// workspace, endpoint key, or item id is unknown.
    /// </summary>
    Task<CatalogItem?> GetItemAsync(string workspaceId, string endpointKey, string itemId, CancellationToken cancellationToken = default);
}
