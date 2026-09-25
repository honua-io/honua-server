// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Db.Postgres.Features.Admin;

internal sealed partial class PostgreSqlLayerPublishingService
{
    internal sealed record PublicationScope(string? Namespace, string? Tenant);

    internal static PublicationScope ResolvePublicationScope(string? publicationNamespace, string? trustedTenant)
    {
        if (!LayerPublicationNamespace.IsValid(publicationNamespace))
        {
            throw new LayerPublishingException(LayerPublishingErrorKind.Validation,
                "Namespace must contain 1-128 ASCII letters, digits, '.', '_' or '-'.");
        }

        if (publicationNamespace is not null && string.IsNullOrWhiteSpace(trustedTenant))
        {
            throw new LayerPublishingException(LayerPublishingErrorKind.Validation,
                "Namespaced publication requires a resolved tenant.");
        }

        return new PublicationScope(publicationNamespace, publicationNamespace is null ? null : trustedTenant);
    }

    internal static void ValidatePublicationScope(
        MetadataV2Graph graph,
        string serviceName,
        PublicationScope scope,
        bool requireExistingScopedService = false)
    {
        var existing = graph.Services.Where(service =>
            string.Equals(service.Metadata.Name, serviceName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(service.Metadata.Id, serviceName, StringComparison.Ordinal)).ToArray();

        // An existing SQL service without persisted metadata is a legacy shared service;
        // it cannot be adopted by a tenant merely by supplying a new namespace.
        if ((requireExistingScopedService && existing.Length == 0) || existing.Any(service =>
                !string.Equals(service.Metadata.Namespace, scope.Namespace, StringComparison.Ordinal) ||
                !string.Equals(service.Metadata.Tenant, scope.Tenant, StringComparison.Ordinal)))
        {
            throw new LayerPublishingException(LayerPublishingErrorKind.Conflict,
                "The existing service belongs to a different publication scope.");
        }
    }

    private static MetadataV2ObjectMetadata ApplyPublicationScope(MetadataV2ObjectMetadata metadata, PublicationScope scope) =>
        metadata with { Namespace = scope.Namespace, Tenant = scope.Tenant };

    internal static MetadataV2ObjectMetadata PreserveDependencyMetadata(
        MetadataV2ObjectMetadata? existing, MetadataV2ObjectMetadata created, PublicationScope scope)
    {
        if (existing is null)
        {
            return created;
        }

        if (scope.Namespace is not null && !MetadataV2TenantVisibility.IsVisibleToTenant(existing.Tenant, scope.Tenant))
        {
            throw new LayerPublishingException(LayerPublishingErrorKind.NotFound, "The requested resource was not found.");
        }

        // Connections and styles can be shared by unrelated publications. Reusing them
        // must not erase their identity, ownership, namespace, or other metadata.
        return existing;
    }

    private async Task ValidateTenantAccessAsync(string? serviceName, IReadOnlySet<int>? layerIds, CancellationToken cancellationToken)
    {
        var (graph, _) = await LoadCurrentOrEmptyGraphAsync(cancellationToken).ConfigureAwait(false);
        ValidateTenantAccess(graph, serviceName, layerIds, _tenantContext?.TenantId);
    }

    internal static void ValidateTenantAccess(
        MetadataV2Graph graph, string? serviceName, IReadOnlySet<int>? layerIds, string? trustedTenant)
    {
        if (SelectPublicationMetadata(graph, serviceName, layerIds).Any(metadata =>
                !MetadataV2TenantVisibility.IsVisibleToTenant(metadata.Tenant, trustedTenant)))
        {
            throw new LayerPublishingException(LayerPublishingErrorKind.NotFound, "The requested resource was not found.");
        }
    }

    internal static void ValidateLegacyLinkScope(MetadataV2Graph graph, string serviceName, int layerId)
    {
        // The legacy linking contract has no explicit scope intent. Do not expose a scoped
        // source through a new shared service, or attach shared data to a scoped service.
        if (SelectPublicationMetadata(graph, serviceName, new HashSet<int> { layerId }).Any(metadata =>
                metadata.Namespace is not null || !string.IsNullOrWhiteSpace(metadata.Tenant)))
        {
            throw new LayerPublishingException(LayerPublishingErrorKind.Conflict,
                "Legacy layer linking does not support scoped sources or services; use same-scope publication instead.");
        }
    }

    private static IEnumerable<MetadataV2ObjectMetadata> SelectPublicationMetadata(
        MetadataV2Graph graph, string? serviceName, IReadOnlySet<int>? layerIds)
    {
        var services = graph.Services.Where(service => serviceName is not null &&
            (string.Equals(service.Metadata.Name, serviceName, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(service.Metadata.Id, serviceName, StringComparison.Ordinal))).ToArray();
        var serviceIds = services.Select(service => service.Metadata.Id).ToHashSet(StringComparer.Ordinal);
        var bindings = graph.StorageBindings.Where(binding => binding.StorageLayerId is { } id &&
            layerIds?.Contains(id) == true).ToArray();
        var resourceIds = bindings.Select(binding => binding.ResourceId).ToHashSet(StringComparer.Ordinal);
        var publications = graph.Publications.Where(publication =>
            (publication.ServiceId is not null && serviceIds.Contains(publication.ServiceId)) ||
            resourceIds.Contains(publication.ResourceId) ||
            (publication.LayerIndex is { } id && layerIds?.Contains(id) == true)).ToArray();
        resourceIds.UnionWith(publications.Select(publication => publication.ResourceId));
        serviceIds.UnionWith(publications.Where(publication => publication.ServiceId is not null)
            .Select(publication => publication.ServiceId!));

        return graph.Services.Where(service => serviceIds.Contains(service.Metadata.Id)).Select(service => service.Metadata)
            .Concat(publications.Select(publication => publication.Metadata))
            .Concat(graph.Resources.Where(resource => resourceIds.Contains(resource.Metadata.Id)).Select(resource => resource.Metadata))
            .Concat(graph.StorageBindings.Where(binding => resourceIds.Contains(binding.ResourceId)).Select(binding => binding.Metadata));
    }
}
