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

        var policyIds = graph.Policies.Select(p => p.Metadata.Id).ToHashSet(StringComparer.Ordinal);

        ValidateStorageBindings(errors, graph.StorageBindings, resourceIds, connectionIds);
        ValidateResources(errors, graph.Resources, storageBindingsById, policyIds);
        ValidatePublications(errors, graph.Publications, resourceIds, storageBindingsById, serviceIds);
        ValidateServices(errors, graph.Services, publicationsById);
        ValidatePublicationPrimary(errors, graph.Publications);
        ValidateRoles(errors, graph.Roles, policyIds);

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
        Dictionary<string, MetadataV2StorageBinding> storageBindingsById,
        HashSet<string> policyIds)
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

            ValidateResourceStorageLayerId(errors, resource, storageBindingIds, storageBindingsById);
            ValidateResourceSpatial(errors, resource);
            ValidateResourceTemporal(errors, resource);
            ValidateResourceSchemaFields(errors, resource);
            ValidateResourcePolicyIds(errors, resource, policyIds);
        }
    }

    private static void ValidateResourceStorageLayerId(
        List<string> errors,
        MetadataV2Resource resource,
        IReadOnlyList<string> storageBindingIds,
        Dictionary<string, MetadataV2StorageBinding> storageBindingsById)
    {
        if (resource.Type is not (MetadataV2ResourceType.FeatureDataset
            or MetadataV2ResourceType.RasterDataset
            or MetadataV2ResourceType.TileDataset))
        {
            return;
        }

        if (storageBindingIds.Count == 0)
        {
            return;
        }

        foreach (var id in storageBindingIds)
        {
            if (storageBindingsById.TryGetValue(id, out var binding) && binding.StorageLayerId is null)
            {
                errors.Add(
                    $"storage binding '{id}' on resource '{resource.Metadata.Id}' (type '{resource.Type}') must declare storageLayerId.");
            }
        }
    }

    private static void ValidateResourceSpatial(List<string> errors, MetadataV2Resource resource)
    {
        var spatial = resource.Spatial;
        if (spatial is null)
        {
            return;
        }

        if (spatial.SpatialReference is { } sr && sr.ResolveSrid() is null && (sr.Srid is null && string.IsNullOrWhiteSpace(sr.Crs)))
        {
            errors.Add(
                $"resource '{resource.Metadata.Id}' spatial reference has neither srid nor crs.");
        }

        if (!string.IsNullOrWhiteSpace(spatial.PrimaryGeometryField))
        {
            var named = resource.SchemaFields.FirstOrDefault(f =>
                string.Equals(f.Name, spatial.PrimaryGeometryField, StringComparison.OrdinalIgnoreCase));
            if (named is null)
            {
                errors.Add(
                    $"resource '{resource.Metadata.Id}' primaryGeometryField '{spatial.PrimaryGeometryField}' is not declared in schemaFields.");
            }
            else if (named.Type is not (MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography))
            {
                errors.Add(
                    $"resource '{resource.Metadata.Id}' primaryGeometryField '{spatial.PrimaryGeometryField}' is type '{named.Type}' (must be Geometry or Geography).");
            }
        }
    }

    private static void ValidateResourceTemporal(List<string> errors, MetadataV2Resource resource)
    {
        var temporal = resource.Temporal;
        if (temporal is null)
        {
            return;
        }

        ValidateTemporalField(errors, resource, temporal.StartTimeField, "startTimeField", required: false);
        ValidateTemporalField(errors, resource, temporal.EndTimeField, "endTimeField", required: false);
    }

    private static void ValidateTemporalField(
        List<string> errors, MetadataV2Resource resource, string? fieldName, string slot, bool required)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            if (required)
            {
                errors.Add($"resource '{resource.Metadata.Id}' temporal.{slot} is required.");
            }
            return;
        }

        var field = resource.SchemaFields.FirstOrDefault(f =>
            string.Equals(f.Name, fieldName, StringComparison.OrdinalIgnoreCase));
        if (field is null)
        {
            errors.Add($"resource '{resource.Metadata.Id}' temporal.{slot} '{fieldName}' is not declared in schemaFields.");
            return;
        }

        if (field.Type is not (MetadataV2FieldType.Date or MetadataV2FieldType.DateTime or MetadataV2FieldType.Time))
        {
            errors.Add(
                $"resource '{resource.Metadata.Id}' temporal.{slot} '{fieldName}' is type '{field.Type}' (must be Date, DateTime, or Time).");
        }
    }

    private static void ValidateResourceSchemaFields(List<string> errors, MetadataV2Resource resource)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var primaryGeometryCount = 0;
        var primaryIdCount = 0;

        foreach (var field in resource.SchemaFields)
        {
            if (string.IsNullOrWhiteSpace(field.Name))
            {
                errors.Add($"resource '{resource.Metadata.Id}' has a schema field with empty name.");
                continue;
            }
            if (!seen.Add(field.Name))
            {
                errors.Add($"resource '{resource.Metadata.Id}' schema field name '{field.Name}' is duplicated.");
            }

            foreach (var role in field.SemanticRoles)
            {
                if (string.Equals(role, "geometry.primary", StringComparison.OrdinalIgnoreCase))
                {
                    primaryGeometryCount++;
                }
                else if (string.Equals(role, "id.primary", StringComparison.OrdinalIgnoreCase))
                {
                    primaryIdCount++;
                }
            }
        }

        if (primaryGeometryCount > 1)
        {
            errors.Add(
                $"resource '{resource.Metadata.Id}' declares {primaryGeometryCount} schema fields with the 'geometry.primary' semantic role (at most one allowed).");
        }
        if (primaryIdCount > 1)
        {
            errors.Add(
                $"resource '{resource.Metadata.Id}' declares {primaryIdCount} schema fields with the 'id.primary' semantic role (at most one allowed).");
        }
    }

    private static void ValidateResourcePolicyIds(
        List<string> errors, MetadataV2Resource resource, HashSet<string> policyIds)
    {
        foreach (var id in resource.PolicyIds)
        {
            if (!policyIds.Contains(id))
            {
                errors.Add(
                    $"resource '{resource.Metadata.Id}' references missing policy '{id}'.");
            }
        }
    }

    private static void ValidatePublicationPrimary(
        List<string> errors, IReadOnlyList<MetadataV2Publication> publications)
    {
        // At most one publication per (resourceId, serviceId) may set IsPrimary = true.
        var counts = new Dictionary<(string, string), int>();
        foreach (var pub in publications)
        {
            if (!pub.IsPrimary) continue;
            var key = (pub.ResourceId, pub.ServiceId);
            counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
        }
        foreach (var ((resourceId, serviceId), n) in counts)
        {
            if (n > 1)
            {
                errors.Add(
                    $"resource '{resourceId}' has {n} primary publications on service '{serviceId}' (at most one allowed).");
            }
        }
    }

    private static void ValidateRoles(
        List<string> errors, IEnumerable<MetadataV2Role> roles, HashSet<string> policyIds)
    {
        foreach (var role in roles)
        {
            foreach (var id in role.PolicyIds)
            {
                if (!policyIds.Contains(id))
                {
                    errors.Add(
                        $"role '{role.Metadata.Id}' references missing policy '{id}'.");
                }
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
        // Service→publications is now derived from publication.ServiceId only;
        // the redundant Service.PublicationIds slot was removed in design slice 55/N.
        // This method is kept as a hook for future service-only invariants.
        _ = errors; _ = services; _ = publicationsById;
    }
}

/// <summary>
/// Result of validating a canonical Metadata v2 graph.
/// </summary>
/// <param name="IsValid">True when graph identity and references are valid.</param>
/// <param name="Errors">Stable validation errors in discovery order.</param>
public sealed record MetadataV2GraphValidationResult(bool IsValid, IReadOnlyList<string> Errors);
