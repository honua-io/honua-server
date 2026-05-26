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

        AddEntityIds(errors, entityIds, "resource", graph.Resources.Select(resource => resource.Metadata.Id));
        AddEntityIds(errors, entityIds, "connection", graph.Connections.Select(connection => connection.Metadata.Id));
        AddEntityIds(
            errors,
            entityIds,
            "storage binding",
            graph.StorageBindings.Select(storageBinding => storageBinding.Metadata.Id));
        AddEntityIds(errors, entityIds, "service", graph.Services.Select(service => service.Metadata.Id));
        AddEntityIds(errors, entityIds, "publication", graph.Publications.Select(publication => publication.Metadata.Id));

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
        ValidatePublicationPrimary(errors, graph.Publications);
        ValidateObjectMetadataUniversals(errors, graph);
        ValidateStyleResources(errors, graph);

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

            if (!string.IsNullOrWhiteSpace(resource.PrimaryStorageBindingId) &&
                !storageBindingIds.Contains(resource.PrimaryStorageBindingId, StringComparer.Ordinal))
            {
                errors.Add(
                    $"resource '{resource.Metadata.Id}' primary storage binding '{resource.PrimaryStorageBindingId}' must be listed in storageBindingIds.");
            }

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
            ValidateResourceDisplayEditing(errors, resource);
        }
    }

    private static void ValidateResourceDisplayEditing(List<string> errors, MetadataV2Resource resource)
    {
        if (resource.Display is { } display && !string.IsNullOrWhiteSpace(display.DisplayField))
        {
            EnsureFieldDeclared(errors, resource, display.DisplayField, "display.displayField");
        }

        if (resource.Editing is { } editing)
        {
            if (!string.IsNullOrWhiteSpace(editing.GlobalIdField))
            {
                EnsureFieldDeclared(errors, resource, editing.GlobalIdField, "editing.globalIdField");
            }
            if (!string.IsNullOrWhiteSpace(editing.CreatorField))
            {
                EnsureFieldDeclared(errors, resource, editing.CreatorField, "editing.creatorField");
            }
            if (!string.IsNullOrWhiteSpace(editing.CreatedAtField))
            {
                EnsureFieldDeclared(errors, resource, editing.CreatedAtField, "editing.createdAtField");
            }
            if (!string.IsNullOrWhiteSpace(editing.EditorField))
            {
                EnsureFieldDeclared(errors, resource, editing.EditorField, "editing.editorField");
            }
            if (!string.IsNullOrWhiteSpace(editing.UpdatedAtField))
            {
                EnsureFieldDeclared(errors, resource, editing.UpdatedAtField, "editing.updatedAtField");
            }
        }
    }

    private static void EnsureFieldDeclared(
        List<string> errors, MetadataV2Resource resource, string? fieldName, string slot)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return;
        }

        var declared = resource.SchemaFields.Any(f =>
            string.Equals(f.Name, fieldName, StringComparison.OrdinalIgnoreCase));
        if (!declared)
        {
            errors.Add(
                $"resource '{resource.Metadata.Id}' {slot} '{fieldName}' is not declared in schemaFields.");
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

    private static readonly HashSet<string> CanonicalStyleEncodings = new(StringComparer.Ordinal)
    {
        "mapbox-style",
        "sld-1.0.0",
        "sld-1.1.0",
        "esri-drawing-info",
        "esri-image-renderer",
        "3d-tiles-styling",
    };

    private static void ValidateStyleResources(List<string> errors, MetadataV2Graph graph)
    {
        var styleResources = graph.Resources
            .Where(r => r.Type == MetadataV2ResourceType.Style)
            .ToDictionary(r => r.Metadata.Id, StringComparer.Ordinal);

        foreach (var resource in graph.Resources)
        {
            // StyleResourceIds entries must reference declared Resources of Type=Style.
            for (var i = 0; i < resource.StyleResourceIds.Count; i++)
            {
                var styleId = resource.StyleResourceIds[i];
                if (string.IsNullOrWhiteSpace(styleId))
                {
                    errors.Add(
                        $"resource '{resource.Metadata.Id}' styleResourceIds[{i}] is empty.");
                    continue;
                }
                if (!styleResources.ContainsKey(styleId))
                {
                    errors.Add(
                        $"resource '{resource.Metadata.Id}' styleResourceIds[{i}] '{styleId}' is not a declared resource of type Style.");
                }
            }

            // Resources with Type=Style MUST have Style.Encodings non-empty.
            if (resource.Type == MetadataV2ResourceType.Style)
            {
                var style = resource.Style;
                if (style is null || style.Encodings.Count == 0)
                {
                    errors.Add(
                        $"resource '{resource.Metadata.Id}' is type Style but has no style.encodings.");
                }
                else
                {
                    for (var i = 0; i < style.Encodings.Count; i++)
                    {
                        var encoding = style.Encodings[i];
                        var hasBody = !string.IsNullOrEmpty(encoding.Body);
                        var hasBinding = !string.IsNullOrEmpty(encoding.StorageBindingId);
                        if (hasBody == hasBinding)
                        {
                            errors.Add(
                                $"resource '{resource.Metadata.Id}' style.encodings[{i}] must set exactly one of body or storageBindingId.");
                        }

                        if (string.IsNullOrWhiteSpace(encoding.Encoding))
                        {
                            errors.Add(
                                $"resource '{resource.Metadata.Id}' style.encodings[{i}].encoding is required.");
                        }
                        else if (!CanonicalStyleEncodings.Contains(encoding.Encoding) && !encoding.Encoding.StartsWith("x-", StringComparison.Ordinal))
                        {
                            errors.Add(
                                $"resource '{resource.Metadata.Id}' style.encodings[{i}].encoding '{encoding.Encoding}' is not a canonical value (mapbox-style, sld-1.0.0, sld-1.1.0, esri-drawing-info, esri-image-renderer, 3d-tiles-styling) and is not prefixed with 'x-'.");
                        }
                    }
                }
            }
        }
    }

    private static void ValidateObjectMetadataUniversals(List<string> errors, MetadataV2Graph graph)
    {
        ValidateOneMetadata(errors, "graph", graph.Metadata);
        foreach (var r in graph.Resources)
        {
            ValidateOneMetadata(errors, $"resource '{r.Metadata.Id}'", r.Metadata);
        }
        foreach (var c in graph.Connections)
        {
            ValidateOneMetadata(errors, $"connection '{c.Metadata.Id}'", c.Metadata);
        }
        foreach (var sb in graph.StorageBindings)
        {
            ValidateOneMetadata(errors, $"storage binding '{sb.Metadata.Id}'", sb.Metadata);
        }
        foreach (var s in graph.Services)
        {
            ValidateOneMetadata(errors, $"service '{s.Metadata.Id}'", s.Metadata);
        }
        foreach (var p in graph.Publications)
        {
            ValidateOneMetadata(errors, $"publication '{p.Metadata.Id}'", p.Metadata);
        }
    }

    private static void ValidateOneMetadata(List<string> errors, string ownerLabel, MetadataV2ObjectMetadata metadata)
    {
        if (metadata.ContactPoint is { Email: { } email } && !string.IsNullOrWhiteSpace(email) && !email.Contains('@'))
        {
            errors.Add($"{ownerLabel} metadata.contactPoint.email '{email}' must contain '@'.");
        }

        for (var i = 0; i < metadata.Links.Count; i++)
        {
            var link = metadata.Links[i];
            if (string.IsNullOrWhiteSpace(link.Href))
            {
                errors.Add($"{ownerLabel} metadata.links[{i}].href is required.");
            }
            if (string.IsNullOrWhiteSpace(link.Rel))
            {
                errors.Add($"{ownerLabel} metadata.links[{i}].rel is required.");
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
        _ = publicationsById;

        foreach (var service in services)
        {
            if (service.Settings is { } settings)
            {
                ValidatePositiveInt(errors, service, settings.MaxRecordCount, "settings.maxRecordCount");
                ValidatePositiveInt(errors, service, settings.DefaultRecordCount, "settings.defaultRecordCount");
                ValidatePositiveInt(errors, service, settings.MaxImageWidth, "settings.maxImageWidth");
                ValidatePositiveInt(errors, service, settings.MaxImageHeight, "settings.maxImageHeight");
                ValidatePositiveInt(errors, service, settings.DefaultDpi, "settings.defaultDpi");
                ValidatePositiveInt(errors, service, settings.MaxFeaturesPerLayer, "settings.maxFeaturesPerLayer");
                ValidatePositiveInt(errors, service, settings.QueryTimeoutMs, "settings.queryTimeoutMs");
                ValidatePositiveInt(errors, service, settings.MaxEditsPerTransaction, "settings.maxEditsPerTransaction");
                ValidatePositiveLong(errors, service, settings.MaxAttachmentSizeBytes, "settings.maxAttachmentSizeBytes");
                ValidatePositiveLong(errors, service, settings.MaxPayloadBytes, "settings.maxPayloadBytes");
            }
        }
    }

    private static void ValidatePositiveInt(List<string> errors, MetadataV2Service service, int? value, string slot)
    {
        if (value is int v && v <= 0)
        {
            errors.Add($"service '{service.Metadata.Id}' {slot} must be positive (was {v}).");
        }
    }

    private static void ValidatePositiveLong(List<string> errors, MetadataV2Service service, long? value, string slot)
    {
        if (value is long v && v <= 0)
        {
            errors.Add($"service '{service.Metadata.Id}' {slot} must be positive (was {v}).");
        }
    }
}

/// <summary>
/// Result of validating a canonical Metadata v2 graph.
/// </summary>
/// <param name="IsValid">True when graph identity and references are valid.</param>
/// <param name="Errors">Stable validation errors in discovery order.</param>
public sealed record MetadataV2GraphValidationResult(bool IsValid, IReadOnlyList<string> Errors);
