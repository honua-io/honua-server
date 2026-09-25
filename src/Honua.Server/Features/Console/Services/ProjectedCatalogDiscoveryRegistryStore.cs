// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Helpers;
using Honua.Protocols.GeoServices.Catalog;
using Honua.Server.Features.Console.Models;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Console.Services;

/// <summary>Request-scoped discovery derived from explicitly mapped, authorized published services.</summary>
internal sealed class ProjectedCatalogDiscoveryRegistryStore(
    IOptions<CatalogDiscoveryOptions> options,
    IMetadataV2GraphProvider graphProvider,
    ITenantContext tenantContext,
    IHttpContextAccessor httpContextAccessor) : ICatalogDiscoveryRegistryStore
{
    public async Task<CatalogDiscoveryRegistry?> GetRegistryAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        var projection = await ReadAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        return projection is null ? null : new CatalogDiscoveryRegistry
        {
            WorkspaceId = projection.Workspace.Id,
            WorkspaceName = projection.Workspace.DisplayName ?? projection.Workspace.Id,
            PublicHost = projection.BaseUrl,
            Endpoints = projection.Entries.Count == 0 ? [] : [Endpoint(projection)],
            AutoDefaultCount = projection.Entries.Count == 0 ? 0 : 1,
        };
    }

    public async Task<CatalogEndpointDetail?> GetEndpointAsync(string workspaceId, string endpointKey, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(endpointKey, CatalogDialects.Esri, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var projection = await ReadAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        return projection is null || projection.Entries.Count == 0 ? null : new CatalogEndpointDetail
        {
            Endpoint = Endpoint(projection),
            AutoMirror = true,
            Items = projection.Entries.Select(entry => new CatalogEndpointItem
            {
                Id = ItemId(projection.Workspace, entry),
                Title = entry.Name,
                FromService = entry.Name,
                Resource = entry.Url,
            }).ToArray(),
        };
    }

    public async Task<CatalogItem?> GetItemAsync(string workspaceId, string endpointKey, string itemId, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(endpointKey, CatalogDialects.Esri, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var projection = await ReadAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        if (projection is null)
        {
            return null;
        }

        var entry = projection.Entries.FirstOrDefault(entry => string.Equals(ItemId(projection.Workspace, entry), itemId, StringComparison.OrdinalIgnoreCase));
        return entry is null ? null : new CatalogItem
        {
            Id = ItemId(projection.Workspace, entry),
            Title = entry.Name,
            AutoMirror = true,
            Live = true,
            BackingServiceCount = 1,
            Groups =
            [
                new CatalogItemFieldGroup
                {
                    Title = "Identity",
                    Scope = "resource-derived",
                    Fields =
                    [
                        new CatalogItemField { Label = "Service", State = CatalogFieldStates.System, Value = entry.Name },
                        new CatalogItemField { Label = "Protocol", State = CatalogFieldStates.Calculated, Value = entry.Type, DerivedFrom = entry.Url },
                        new CatalogItemField { Label = "URL", State = CatalogFieldStates.Calculated, Value = entry.Url, DerivedFrom = entry.Name },
                    ],
                },
            ],
        };
    }

    private async Task<WorkspaceProjection?> ReadAsync(string workspaceId, CancellationToken cancellationToken)
    {
        var workspace = options.Value.Workspaces.FirstOrDefault(value => string.Equals(value.Id, workspaceId, StringComparison.OrdinalIgnoreCase));
        var context = httpContextAccessor.HttpContext;
        if (workspace is null || context is null ||
            !tenantContext.RequireTenantId(out var tenantId, out _) ||
            !string.Equals(tenantId, workspace.TenantId, StringComparison.Ordinal))
        {
            return null;
        }

        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var scoped = FilterSnapshot(snapshot, workspace);
        var entries = await GeoServicesCatalogProjection.ReadFeatureMapAsync(context, scoped).ConfigureAwait(false);
        return new WorkspaceProjection(workspace, BaseUrlResolver.GetBaseUrl(context), entries);
    }

    internal static MetadataV2GraphSnapshot FilterSnapshot(MetadataV2GraphSnapshot snapshot, CatalogDiscoveryWorkspaceOptions workspace)
    {
        bool Visible(MetadataV2ObjectMetadata metadata) =>
            string.Equals(metadata.Namespace, workspace.Namespace, StringComparison.Ordinal) &&
            MetadataV2TenantVisibility.IsVisibleToTenant(metadata.Tenant, workspace.TenantId);

        var services = snapshot.Graph.Services.Where(service => Visible(service.Metadata)).ToArray();
        var resources = snapshot.Graph.Resources.Where(resource => Visible(resource.Metadata)).ToArray();
        var serviceIds = services.Select(static service => service.Metadata.Id).ToHashSet(StringComparer.Ordinal);
        var resourceIds = resources.Select(static resource => resource.Metadata.Id).ToHashSet(StringComparer.Ordinal);
        var publications = snapshot.Graph.Publications.Where(publication =>
            Visible(publication.Metadata) && serviceIds.Contains(publication.ServiceId) && resourceIds.Contains(publication.ResourceId)).ToArray();
        return new MetadataV2GraphSnapshot(snapshot.Graph with
        {
            Services = services,
            Resources = resources,
            Publications = publications,
        }, snapshot.Etag, snapshot.LoadedAt);
    }

    private static CatalogEndpoint Endpoint(WorkspaceProjection projection) => new()
    {
        Key = CatalogDialects.Esri,
        Title = "GeoServices catalog",
        Dialect = CatalogDialects.Esri,
        Enabled = true,
        AutoDefault = true,
        Url = projection.BaseUrl + "/rest/services",
        Entries = projection.Entries.Count,
        Feeders = projection.Entries.Select(static entry => new CatalogFeeder
        {
            Kind = entry.Type == "FeatureServer" ? "feature-server" : "map-server",
            Label = entry.Name,
        }).ToArray(),
    };

    private static string ItemId(CatalogDiscoveryWorkspaceOptions workspace, GeoServicesCatalogEntry entry) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            workspace.Id.ToUpperInvariant() + "\0" + workspace.TenantId + "\0" + workspace.Namespace + "\0" + entry.Name + "\0" + entry.Type))).ToLowerInvariant();

    private sealed record WorkspaceProjection(
        CatalogDiscoveryWorkspaceOptions Workspace, string BaseUrl, IReadOnlyList<GeoServicesCatalogEntry> Entries);
}
