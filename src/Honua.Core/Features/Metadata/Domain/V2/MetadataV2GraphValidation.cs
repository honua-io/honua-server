// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// Validates canonical Metadata v2 graph identity and reference integrity.
/// </summary>
public static class MetadataV2GraphValidator
{
    /// <summary>
    /// Validates graph entity identifiers and resource-first references.
    /// </summary>
    /// <param name="graph">Graph to validate.</param>
    /// <returns>Validation result with stable error messages.</returns>
    public static MetadataV2GraphValidationResult Validate(MetadataV2Graph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var errors = new List<string>();
        var entityIds = new HashSet<string>(StringComparer.Ordinal);

        AddEntityIds(errors, entityIds, "catalog", graph.Catalogs.Select(catalog => catalog.Metadata.Id));
        AddEntityIds(errors, entityIds, "resource", graph.Resources.Select(resource => resource.Metadata.Id));
        AddEntityIds(errors, entityIds, "connection", graph.Connections.Select(connection => connection.Metadata.Id));
        AddEntityIds(
            errors,
            entityIds,
            "storage binding",
            graph.StorageBindings.Select(storageBinding => storageBinding.Metadata.Id));
        AddEntityIds(errors, entityIds, "service", graph.Services.Select(service => service.Metadata.Id));
        AddEntityIds(errors, entityIds, "publication", graph.Publications.Select(publication => publication.Metadata.Id));
        AddEntityIds(
            errors,
            entityIds,
            "projection profile",
            graph.ProjectionProfiles.Select(profile => profile.Metadata.Id));
        AddEntityIds(errors, entityIds, "policy", graph.Policies.Select(policy => policy.Metadata.Id));
        AddEntityIds(errors, entityIds, "role", graph.Roles.Select(role => role.Metadata.Id));

        var resourceIds = graph.Resources.Select(resource => resource.Metadata.Id).ToHashSet(StringComparer.Ordinal);
        var connectionIds = graph.Connections.Select(connection => connection.Metadata.Id).ToHashSet(StringComparer.Ordinal);
        var storageBindingsById = ToUniqueDictionary(
            graph.StorageBindings,
            storageBinding => storageBinding.Metadata.Id);
        var serviceIds = graph.Services.Select(service => service.Metadata.Id).ToHashSet(StringComparer.Ordinal);
        var publicationsById = ToUniqueDictionary(
            graph.Publications,
            publication => publication.Metadata.Id);

        ValidateStorageBindings(errors, graph.StorageBindings, resourceIds, connectionIds);
        ValidateResources(errors, graph.Resources, storageBindingsById);
        ValidatePublications(errors, graph.Publications, resourceIds, storageBindingsById, serviceIds);
        ValidateServices(errors, graph.Services, publicationsById);

        return new MetadataV2GraphValidationResult(errors.Count == 0, errors);
    }

    private static Dictionary<string, TEntity> ToUniqueDictionary<TEntity>(
        IEnumerable<TEntity> entities,
        Func<TEntity, string> getId)
    {
        var dictionary = new Dictionary<string, TEntity>(StringComparer.Ordinal);

        foreach (var entity in entities)
        {
            var id = getId(entity);
            if (string.IsNullOrWhiteSpace(id) || dictionary.ContainsKey(id))
            {
                continue;
            }

            dictionary.Add(id, entity);
        }

        return dictionary;
    }

    private static void AddEntityIds(
        List<string> errors,
        HashSet<string> entityIds,
        string entityKind,
        IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                errors.Add($"{entityKind} metadata id is required.");
                continue;
            }

            if (!entityIds.Add(id))
            {
                errors.Add($"metadata id '{id}' is duplicated.");
            }
        }
    }

    private static void ValidateStorageBindings(
        List<string> errors,
        IEnumerable<MetadataV2StorageBinding> storageBindings,
        HashSet<string> resourceIds,
        HashSet<string> connectionIds)
    {
        foreach (var storageBinding in storageBindings)
        {
            if (!resourceIds.Contains(storageBinding.ResourceId))
            {
                errors.Add(
                    $"storage binding '{storageBinding.Metadata.Id}' references missing resource '{storageBinding.ResourceId}'.");
            }

            if (storageBinding.ConnectionId is not null && !connectionIds.Contains(storageBinding.ConnectionId))
            {
                errors.Add(
                    $"storage binding '{storageBinding.Metadata.Id}' references missing connection '{storageBinding.ConnectionId}'.");
            }
        }
    }

    private static void ValidateResources(
        List<string> errors,
        IEnumerable<MetadataV2Resource> resources,
        Dictionary<string, MetadataV2StorageBinding> storageBindingsById)
    {
        foreach (var resource in resources)
        {
            var storageBindingIds = resource.StorageBindingIds ?? Array.Empty<string>();

            foreach (var storageBindingId in storageBindingIds)
            {
                if (!storageBindingsById.TryGetValue(storageBindingId, out var storageBinding))
                {
                    errors.Add(
                        $"resource '{resource.Metadata.Id}' references missing storage binding '{storageBindingId}'.");
                    continue;
                }

                if (!string.Equals(storageBinding.ResourceId, resource.Metadata.Id, StringComparison.Ordinal))
                {
                    errors.Add(
                        $"resource '{resource.Metadata.Id}' references storage binding '{storageBindingId}' owned by resource '{storageBinding.ResourceId}'.");
                }
            }

            if (resource.PrimaryStorageBindingId is not null && !storageBindingIds.Contains(
                    resource.PrimaryStorageBindingId,
                    StringComparer.Ordinal))
            {
                errors.Add(
                    $"resource '{resource.Metadata.Id}' primary storage binding '{resource.PrimaryStorageBindingId}' must be listed in storageBindingIds.");
            }
        }
    }

    private static void ValidatePublications(
        List<string> errors,
        IEnumerable<MetadataV2Publication> publications,
        HashSet<string> resourceIds,
        Dictionary<string, MetadataV2StorageBinding> storageBindingsById,
        HashSet<string> serviceIds)
    {
        foreach (var publication in publications)
        {
            if (!resourceIds.Contains(publication.ResourceId))
            {
                errors.Add(
                    $"publication '{publication.Metadata.Id}' references missing resource '{publication.ResourceId}'.");
            }

            if (!serviceIds.Contains(publication.ServiceId))
            {
                errors.Add(
                    $"publication '{publication.Metadata.Id}' references missing service '{publication.ServiceId}'.");
            }

            if (publication.StorageBindingId is null)
            {
                continue;
            }

            if (!storageBindingsById.TryGetValue(publication.StorageBindingId, out var storageBinding))
            {
                errors.Add(
                    $"publication '{publication.Metadata.Id}' references missing storage binding '{publication.StorageBindingId}'.");
                continue;
            }

            if (!string.Equals(storageBinding.ResourceId, publication.ResourceId, StringComparison.Ordinal))
            {
                errors.Add(
                    $"publication '{publication.Metadata.Id}' uses storage binding '{publication.StorageBindingId}' owned by resource '{storageBinding.ResourceId}'.");
            }
        }
    }

    private static void ValidateServices(
        List<string> errors,
        IEnumerable<MetadataV2Service> services,
        Dictionary<string, MetadataV2Publication> publicationsById)
    {
        foreach (var service in services)
        {
            foreach (var publicationId in service.PublicationIds)
            {
                if (!publicationsById.TryGetValue(publicationId, out var publication))
                {
                    errors.Add($"service '{service.Metadata.Id}' references missing publication '{publicationId}'.");
                    continue;
                }

                if (!string.Equals(publication.ServiceId, service.Metadata.Id, StringComparison.Ordinal))
                {
                    errors.Add(
                        $"service '{service.Metadata.Id}' references publication '{publicationId}' owned by service '{publication.ServiceId}'.");
                }
            }
        }
    }
}

/// <summary>
/// Result of validating a canonical Metadata v2 graph.
/// </summary>
/// <param name="IsValid">True when graph identity and references are valid.</param>
/// <param name="Errors">Stable validation errors in discovery order.</param>
public sealed record MetadataV2GraphValidationResult(bool IsValid, IReadOnlyList<string> Errors);
