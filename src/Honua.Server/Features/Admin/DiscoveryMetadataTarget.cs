// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Admin.Models;

namespace Honua.Server.Features.Admin;

internal sealed record DiscoveryMetadataTarget(
    MetadataV2ObjectMetadata Metadata,
    IReadOnlySet<string> ResourceIds,
    IReadOnlySet<string> ServiceIds,
    IReadOnlyList<DiscoveryPublicationImpact> Publications)
{
    public string ItemKind => ResourceIds.Count > 0 ? MetadataV2CustomFieldKinds.Dataset : MetadataV2CustomFieldKinds.Service;

    public string HistoryItemKind => ResourceIds.Count > 0 ? "resources" : "services";

    public static ResourceValidationResult<DiscoveryMetadataTarget> ForLayer(MetadataV2GraphSnapshot snapshot, int storageLayerId)
    {
        var resolution = snapshot.ResolveStorageResource(storageLayerId);
        if (resolution.IsAmbiguous)
            return ResourceValidationResult.AmbiguousIdentifier<DiscoveryMetadataTarget>(resolution.ConflictDetail!);
        var resource = resolution.Resource;
        if (resource is null)
            return ResourceValidationResult.NotFound<DiscoveryMetadataTarget>("Metadata target not found.");
        var resourceIds = new HashSet<string>(StringComparer.Ordinal) { resource.Metadata.Id };
        return ResourceValidationResult.Success(new DiscoveryMetadataTarget(
            resource.Metadata,
            resourceIds,
            new HashSet<string>(),
            Impact(snapshot, snapshot.Graph.Publications.Where(publication => resourceIds.Contains(publication.ResourceId)))));
    }

    public static ResourceValidationResult<DiscoveryMetadataTarget> ForService(MetadataV2GraphSnapshot snapshot, string serviceName)
    {
        var services = snapshot.Graph.Services.Where(service =>
            string.Equals(service.Metadata.Name, serviceName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (services.Length == 0)
            return ResourceValidationResult.NotFound<DiscoveryMetadataTarget>("Metadata target not found.");
        var serviceIds = services.Select(service => service.Metadata.Id).ToHashSet(StringComparer.Ordinal);
        return ResourceValidationResult.Success(new DiscoveryMetadataTarget(
            services[0].Metadata,
            new HashSet<string>(),
            serviceIds,
            Impact(snapshot, snapshot.Graph.Publications.Where(publication => serviceIds.Contains(publication.ServiceId)))));
    }

    public MetadataV2Graph Apply(MetadataV2Graph graph, DiscoveryMetadataUpdateRequest request) => graph with
    {
        Resources = graph.Resources.Select(resource => ResourceIds.Contains(resource.Metadata.Id)
            ? resource with { Metadata = ApplyFields(resource.Metadata, request) } : resource).ToArray(),
        Services = graph.Services.Select(service => ServiceIds.Contains(service.Metadata.Id)
            ? service with { Metadata = ApplyFields(service.Metadata, request) } : service).ToArray(),
        Revision = graph.Revision + 1
    };

    private static MetadataV2ObjectMetadata ApplyFields(MetadataV2ObjectMetadata metadata,
        DiscoveryMetadataUpdateRequest request) => metadata with
        {
            Title = request.Title,
            Description = request.Description,
            Keywords = request.Keywords,
            CustomFields = request.CustomFields is null
                ? metadata.CustomFields
                : CloneCustomFields(request.CustomFields),
        };

    private static Dictionary<string, JsonElement>? CloneCustomFields(IReadOnlyDictionary<string, JsonElement> fields)
    {
        var cloned = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in fields)
        {
            if (value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                cloned[key] = value.Clone();
            }
        }
        return cloned.Count == 0 ? null : cloned;
    }

    private static DiscoveryPublicationImpact[] Impact(MetadataV2GraphSnapshot snapshot,
        IEnumerable<MetadataV2Layer> publications) => publications.Select(publication => new DiscoveryPublicationImpact(
            publication.Metadata.Id, snapshot.Index.ServicesById[publication.ServiceId].Metadata.Name,
            ReadPublicationIdentifier(publication))
        {
            PreviewStorageLayerId = snapshot.IsRoutable(publication) ? snapshot.ResolveStorageLayerId(publication) : null
        }).ToArray();

    private static string ReadPublicationIdentifier(MetadataV2Layer publication)
    {
        var publicationWithIdentifier = publication.Identifier is null ? publication with { Identifier = new() } : publication;
        return publicationWithIdentifier.ServiceLocalId ?? publicationWithIdentifier.LayerIndex?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    }
}
