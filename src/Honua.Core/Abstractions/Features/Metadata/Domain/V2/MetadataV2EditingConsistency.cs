// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

// A resource is modifiable when a publication declares Create, Update or Delete against an
// edit-capable binding, the same rule the editing PUT applies; a stored canModify=false that
// contradicts it is stale. Disabling edits through the PUT removes the edit capabilities, and
// composite-relationship resources stay read-only, so neither is overridden.
public static class MetadataV2EditingConsistency
{
    public static MetadataV2Graph RepairCanModify(MetadataV2Graph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        MetadataV2Object[]? repaired = null;
        EditFacts? facts = null;
        for (var index = 0; index < graph.Resources.Count; index++)
        {
            var resource = graph.Resources[index];
            if (resource.Editing is not { CanModify: false } editing)
            {
                continue;
            }
            facts ??= new EditFacts(graph);
            if (!facts.ExpectsModify(resource))
            {
                continue;
            }
            repaired ??= [.. graph.Resources];
            repaired[index] = resource with { Editing = editing with { CanModify = true } };
        }
        return repaired is null ? graph : graph with { Resources = repaired };
    }

    private sealed class EditFacts(MetadataV2Graph graph)
    {
        private readonly Dictionary<string, MetadataV2Service> _services = graph.Services
            .GroupBy(static service => service.Metadata.Id, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);

        private readonly Dictionary<string, MetadataV2StorageBinding> _bindings = graph.StorageBindings
            .GroupBy(static binding => binding.Metadata.Id, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);

        private readonly ILookup<string, MetadataV2Layer> _publications = graph.Publications
            .ToLookup(static publication => publication.ResourceId, StringComparer.Ordinal);

        public bool ExpectsModify(MetadataV2Object resource)
            => !MetadataV2RelationshipEditPolicy.RequiresReadOnly(resource) &&
               _publications[resource.Metadata.Id].Any(publication =>
                   DeclaresEdit(publication) && HasEditBinding(resource, publication));

        private bool DeclaresEdit(MetadataV2Layer publication)
            => _services.TryGetValue(publication.ServiceId, out var service) &&
               (MetadataV2EditCapabilities.Supports(service, publication, MetadataV2EditCapabilities.Create) ||
                MetadataV2EditCapabilities.Supports(service, publication, MetadataV2EditCapabilities.Update) ||
                MetadataV2EditCapabilities.Supports(service, publication, MetadataV2EditCapabilities.Delete));

        private bool HasEditBinding(MetadataV2Object resource, MetadataV2Layer publication)
        {
            var bindingId = publication.StorageBindingId ?? resource.PrimaryStorageBindingId;
            return bindingId is not null &&
                   _bindings.TryGetValue(bindingId, out var binding) &&
                   binding.ResourceId == resource.Metadata.Id &&
                   binding.Capabilities.Contains(MetadataV2StorageBindingCapability.Edit);
        }
    }
}
