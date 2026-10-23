// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>Explicit, partial edits to imported resource bindings and publication edit policy.</summary>
public sealed record MetadataV2EditingPatch
{
    /// <summary>Declared UUID field; null preserves the binding and an empty string clears it.</summary>
    [JsonPropertyName("globalIdField")]
    public string? GlobalIdField { get; init; }

    /// <summary>Attachment support; null preserves the existing declaration.</summary>
    [JsonPropertyName("supportsAttachments")]
    public bool? SupportsAttachments { get; init; }

    /// <summary>Creation policy for the selected publications; null preserves it.</summary>
    [JsonPropertyName("create")]
    public bool? Create { get; init; }

    /// <summary>Update policy for the selected publications; null preserves it.</summary>
    [JsonPropertyName("update")]
    public bool? Update { get; init; }

    /// <summary>Deletion policy for the selected publications; null preserves it.</summary>
    [JsonPropertyName("delete")]
    public bool? Delete { get; init; }
}

/// <summary>Applies editing configuration without moving data, changing identities or granting access.</summary>
public static class MetadataV2EditingConfiguration
{
    /// <summary>Validates and applies a partial patch against one current graph snapshot.</summary>
    /// <param name="graph">Current graph; the caller owns revision and optimistic concurrency.</param>
    /// <param name="resourceId">Immutable resource identity selected by the caller.</param>
    /// <param name="publicationIds">Publications whose edit operations may change.</param>
    /// <param name="patch">Explicit binding and policy changes.</param>
    /// <returns>A graph preserving all unrelated resources, publications and access policies.</returns>
    /// <exception cref="ArgumentException">The binding is invalid or writes are unsupported.</exception>
    public static MetadataV2Graph Apply(
        MetadataV2Graph graph,
        string resourceId,
        IReadOnlySet<string> publicationIds,
        MetadataV2EditingPatch patch)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(publicationIds);
        ArgumentNullException.ThrowIfNull(patch);
        if (patch.GlobalIdField is null && patch.SupportsAttachments is null &&
            patch.Create is null && patch.Update is null && patch.Delete is null)
        {
            return graph;
        }
        var resource = graph.Resources.SingleOrDefault(candidate => candidate.Metadata.Id == resourceId)
            ?? throw new ArgumentException("The selected resource no longer exists.", nameof(resourceId));
        var editing = resource.Editing ?? new MetadataV2ResourceEditing
        {
            CanModify = false,
            SupportsAttachments = ReadAttachmentAnnotation(resource, "honua.io/attachments") ??
                ReadAttachmentAnnotation(resource, "supportsAttachments") ?? false
        };
        var globalId = patch.GlobalIdField == "" ? null : patch.GlobalIdField ?? editing.GlobalIdField;
        if (globalId is not null)
        {
            var field = resource.SchemaFields.FirstOrDefault(candidate =>
                candidate.Name.Equals(globalId, StringComparison.OrdinalIgnoreCase));
            if (field?.Type != MetadataV2FieldType.Uuid)
            {
                throw new ArgumentException("GlobalIdField must reference a declared UUID field.", nameof(patch));
            }
            globalId = field.Name;
        }

        var changesOperations = patch.Create.HasValue || patch.Update.HasValue || patch.Delete.HasValue;
        var publications = graph.Publications.ToArray();
        if (changesOperations)
        {
            if (publicationIds.Count == 0 || publicationIds.Any(id => !publications.Any(publication =>
                    publication.Metadata.Id == id && publication.ResourceId == resourceId)))
            {
                throw new ArgumentException("Every selected publication must belong to the resource.", nameof(publicationIds));
            }
            for (var index = 0; index < publications.Length; index++)
            {
                var publication = publications[index];
                if (!publicationIds.Contains(publication.Metadata.Id))
                {
                    continue;
                }
                var service = graph.Services.Single(candidate => candidate.Metadata.Id == publication.ServiceId);
                var create = patch.Create ?? MetadataV2EditCapabilities.Supports(service, publication, MetadataV2EditCapabilities.Create);
                var update = patch.Update ?? MetadataV2EditCapabilities.Supports(service, publication, MetadataV2EditCapabilities.Update);
                var delete = patch.Delete ?? MetadataV2EditCapabilities.Supports(service, publication, MetadataV2EditCapabilities.Delete);
                if (create || update || delete)
                {
                    if (MetadataV2RelationshipEditPolicy.RequiresReadOnly(resource))
                    {
                        throw new ArgumentException(MetadataV2RelationshipEditPolicy.ReadOnlyReason, nameof(patch));
                    }
                    var bindingId = publication.StorageBindingId ?? resource.PrimaryStorageBindingId;
                    var binding = graph.StorageBindings.SingleOrDefault(candidate => candidate.Metadata.Id == bindingId);
                    if (binding is null || binding.ResourceId != resourceId ||
                        !binding.Capabilities.Contains(MetadataV2StorageBindingCapability.Edit))
                    {
                        throw new ArgumentException(
                            "The selected storage binding does not declare edit support; metadata alone cannot enable its writer.", nameof(patch));
                    }
                }
                var capabilities = MetadataV2EditCapabilities.Resolve(service, publication)
                    .Where(static capability => !IsEditCapability(capability)).ToList();
                if (create)
                {
                    capabilities.Add(MetadataV2EditCapabilities.Create);
                }
                if (update)
                {
                    capabilities.Add(MetadataV2EditCapabilities.Update);
                }
                if (delete)
                {
                    capabilities.Add(MetadataV2EditCapabilities.Delete);
                }
                // An empty declaration inherits service policy; retain explicit Query
                // so disabling an Editing-only publication cannot re-enable it by fallback.
                if (capabilities.Count == 0)
                {
                    capabilities.Add("Query");
                }
                publications[index] = publication with { Capabilities = capabilities };
            }
        }

        var canModify = changesOperations
            ? publications.Where(publication => publication.ResourceId == resourceId).Any(publication =>
            {
                var service = graph.Services.Single(candidate => candidate.Metadata.Id == publication.ServiceId);
                return MetadataV2EditCapabilities.Supports(service, publication, MetadataV2EditCapabilities.Create) ||
                    MetadataV2EditCapabilities.Supports(service, publication, MetadataV2EditCapabilities.Update) ||
                    MetadataV2EditCapabilities.Supports(service, publication, MetadataV2EditCapabilities.Delete);
            })
            : editing.CanModify;
        var updatedResource = resource with
        {
            Editing = editing with
            {
                GlobalIdField = globalId,
                SupportsAttachments = patch.SupportsAttachments ?? editing.SupportsAttachments,
                CanModify = canModify
            }
        };
        return graph with
        {
            Resources = graph.Resources.Select(candidate => candidate.Metadata.Id == resourceId ? updatedResource : candidate).ToArray(),
            Publications = publications
        };
    }

    private static bool IsEditCapability(string capability)
        => capability.Equals(MetadataV2EditCapabilities.Create, StringComparison.OrdinalIgnoreCase) ||
           capability.Equals(MetadataV2EditCapabilities.Update, StringComparison.OrdinalIgnoreCase) ||
           capability.Equals(MetadataV2EditCapabilities.Delete, StringComparison.OrdinalIgnoreCase) ||
           capability.Equals(MetadataV2EditCapabilities.Editing, StringComparison.OrdinalIgnoreCase);

    private static bool? ReadAttachmentAnnotation(MetadataV2Resource resource, string key)
        => resource.Metadata.Annotations.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed)
            ? parsed : null;
}
