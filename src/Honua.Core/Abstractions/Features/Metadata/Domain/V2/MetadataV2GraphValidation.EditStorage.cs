// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

public static partial class MetadataV2GraphValidator
{
    private static readonly string[] _editOperations =
        [MetadataV2EditCapabilities.Create, MetadataV2EditCapabilities.Update, MetadataV2EditCapabilities.Delete];

    // A publication that accepts edits must be bound to storage that declares edit support. Feature
    // writes go through the managed store's writer, so edits declared over a read-only source binding
    // would be written where that layer never reads them.
    private static void ValidatePublicationEditStorage(
        List<string> errors,
        MetadataV2Graph graph,
        Dictionary<string, MetadataV2Object> resourcesById,
        Dictionary<string, MetadataV2StorageBinding> storageBindingsById)
    {
        var servicesById = ToUniqueDictionary(graph.Services, service => service.Metadata.Id);
        foreach (var publication in graph.Publications)
        {
            if (!servicesById.TryGetValue(publication.ServiceId, out var service) || !DeclaresEdits(service, publication))
            {
                continue;
            }

            var bindingId = publication.StorageBindingId
                ?? (resourcesById.TryGetValue(publication.ResourceId, out var resource) ? resource.PrimaryStorageBindingId : null);
            if (bindingId is not null
                && storageBindingsById.TryGetValue(bindingId, out var binding)
                && binding.Capabilities.Contains(MetadataV2StorageBindingCapability.Edit))
            {
                continue;
            }

            errors.Add($"publication '{publication.Metadata.Id}' declares edit capabilities, but its storage binding does not declare edit support.");
        }
    }

    private static bool DeclaresEdits(MetadataV2Service service, MetadataV2Layer publication)
        => _editOperations.Any(operation => MetadataV2EditCapabilities.Supports(service, publication, operation));
}
