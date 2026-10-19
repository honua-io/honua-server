// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Ai.Protocols.Mcp.Models;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;

namespace Honua.Ai.Protocols.Mcp.Resources;

/// <summary>
/// Projects ordinary layer publications from the persisted Metadata v2 graph. A
/// promotion record has different provenance and remains owned by its own store.
/// </summary>
internal static class PublishedMetadataCatalogProjection
{
    internal sealed record ServiceView(
        MetadataV2Service Service,
        IReadOnlyList<int> LayerIds,
        IReadOnlyList<int> ServingLayerIds,
        bool FeatureServerEnabled,
        bool Serving,
        DateTimeOffset? PublishedAt,
        DateTimeOffset? UpdatedAt,
        string Etag)
    {
        public string ServiceName => Service.Metadata.Name;

        public McpPublishedServiceView ToDetail() => new()
        {
            ServiceId = ServiceName,
            ResourceUri = McpResourceUris.PublishedServiceUri(ServiceName),
            MetadataServiceId = Service.Metadata.Id,
            LayerIds = LayerIds,
            ServingLayerIds = ServingLayerIds,
            FeatureServerEnabled = FeatureServerEnabled,
            Serving = Serving,
            Status = Service.Status?.Lifecycle.ToString() ?? MetadataV2LifecycleStatus.Active.ToString(),
            SourceKind = "MetadataV2Graph",
            SourceId = Service.Metadata.Id,
            TargetKind = "FeatureService",
            Endpoint = Service.Route,
            PublishedAt = PublishedAt,
            UpdatedAt = UpdatedAt,
            Etag = Etag,
            Provenance = new McpHostedProvenance
            {
                PublishedServiceResourceUri = McpResourceUris.PublishedServiceUri(ServiceName)
            }
        };

        public McpPublishedServiceSummary ToSummary() => new()
        {
            ServiceId = ServiceName,
            ResourceUri = McpResourceUris.PublishedServiceUri(ServiceName),
            Status = Service.Status?.Lifecycle.ToString() ?? MetadataV2LifecycleStatus.Active.ToString(),
            TargetKind = "FeatureService",
            UpdatedAt = UpdatedAt,
            Etag = Etag
        };
    }

    public static async Task<ServiceView?> FindAsync(
        HttpContext context,
        MetadataV2GraphSnapshot snapshot,
        string serviceName,
        bool refuseDenied,
        CancellationToken cancellationToken)
    {
        ServiceView? found = null;
        foreach (var service in snapshot.Graph.Services.Where(service =>
                     string.Equals(service.Metadata.Name, serviceName, StringComparison.OrdinalIgnoreCase)))
        {
            var projected = await ProjectAsync(context, snapshot, service, refuseDenied, cancellationToken)
                .ConfigureAwait(false);
            if (projected is null)
            {
                continue;
            }

            if (found is not null)
            {
                throw new GeoprocessingValidationException(
                    $"Published service identity '{serviceName}' is ambiguous in the metadata catalog.");
            }

            found = projected;
        }

        return found;
    }

    public static async Task<IReadOnlyList<ServiceView>> ListReadableAsync(
        HttpContext context,
        MetadataV2GraphSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var results = new List<ServiceView>();
        // The canonical graph index resolves service names without case, so a
        // case-only duplicate cannot be exposed as two readable URIs.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var service in snapshot.Graph.Services)
        {
            var projected = await ProjectAsync(context, snapshot, service, refuseDenied: false, cancellationToken)
                .ConfigureAwait(false);
            if (projected is null)
            {
                continue;
            }

            if (!names.Add(projected.ServiceName))
            {
                throw new GeoprocessingValidationException(
                    $"Published service identity '{projected.ServiceName}' is ambiguous in the metadata catalog.");
            }

            results.Add(projected);
        }

        return results;
    }

    private static async Task<ServiceView?> ProjectAsync(
        HttpContext context,
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service service,
        bool refuseDenied,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(service.Metadata.Name))
        {
            return null;
        }

        // A completed publish may leave the pre-existing service intentionally
        // stopped or FeatureServer-disabled. Existence is structural; serving
        // state is reported separately and only controls the active index.
        var publications = snapshot.Graph.Publications
            .Where(publication =>
                string.Equals(publication.ServiceId, service.Metadata.Id, StringComparison.Ordinal) &&
                publication.PublicationType == MetadataV2PublicationType.EsriFeatureLayer &&
                publication.LayerIndex is >= 0)
            .Select(publication =>
            {
                var resource = snapshot.ResolveResource(publication);
                var binding = snapshot.ResolveStorageBinding(publication);
                return (publication, resource, binding);
            })
            .Where(entry => entry.resource is { } resource &&
                entry.binding is { StorageLayerId: >= 0 } binding &&
                string.Equals(binding.ResourceId, resource.Metadata.Id, StringComparison.Ordinal) &&
                (string.IsNullOrEmpty(entry.publication.StorageBindingId) ||
                    string.Equals(entry.publication.StorageBindingId, binding.Metadata.Id, StringComparison.Ordinal)))
            .ToArray();
        if (publications.Length == 0)
        {
            return null;
        }

        if (!TenantScopeHelpers.IsTenantVisible(context, resource: null, service))
        {
            return null;
        }

        if (await AccessPolicyHelpers.RequireServiceAccessAsync(
                context, service, AuthorizationOperation.Metadata, cancellationToken).ConfigureAwait(false) is not null)
        {
            if (refuseDenied)
            {
                throw new GeoprocessingAuthorizationException(requiresAuthentication: false);
            }

            return null;
        }

        var allowed = new List<MetadataV2Publication>();
        var denied = false;
        foreach (var (publication, resource, _) in publications)
        {
            if (!TenantScopeHelpers.IsPublicationVisible(context, publication, resource, service))
            {
                continue;
            }

            var decision = await AccessPolicyHelpers.EvaluateResourceAccessAsync(
                context, resource!, service, AuthorizationOperation.Metadata, cancellationToken).ConfigureAwait(false);
            if (decision.IsAllowed)
            {
                allowed.Add(publication);
            }
            else
            {
                denied = true;
            }
        }

        if (allowed.Count == 0)
        {
            if (refuseDenied && denied)
            {
                throw new GeoprocessingAuthorizationException(requiresAuthentication: false);
            }

            return null;
        }

        // Check ambiguity only within the caller's visible and authorized
        // publications. A hidden tenant's graph shape is never disclosed.
        if (allowed.GroupBy(publication => publication.LayerIndex).Any(group => group.Count() > 1))
        {
            throw new GeoprocessingValidationException(
                $"Published service '{service.Metadata.Name}' has ambiguous public layer identifiers.");
        }

        var featureServerEnabled = ServiceProtocols.IsProtocolEnabled(service, ServiceProtocols.FeatureServer);
        var servingLayerIds = featureServerEnabled
            ? allowed.Where(snapshot.IsRoutable)
                .Select(publication => publication.LayerIndex!.Value).Order().ToArray()
            : Array.Empty<int>();
        var serving = servingLayerIds.Length > 0;
        var created = allowed.Select(publication => publication.Metadata.CreatedAt)
            .Where(value => value.HasValue).Min();
        var updated = allowed.Select(publication => publication.Metadata.UpdatedAt)
            .Append(service.Metadata.UpdatedAt)
            .Where(value => value.HasValue).Max();
        return new ServiceView(
            service,
            allowed.Select(publication => publication.LayerIndex!.Value).Order().ToArray(),
            servingLayerIds,
            featureServerEnabled,
            serving,
            created,
            updated,
            snapshot.Etag);
    }
}
