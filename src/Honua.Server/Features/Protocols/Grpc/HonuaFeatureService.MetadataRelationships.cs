// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Grpc.Core;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Authorization.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Proto = Geospatial.V1;

namespace Honua.Server.Features.Protocols.Grpc;

internal sealed partial class HonuaFeatureService
{
    private static async Task AddMetadataRelationshipsAsync(Proto.LayerDescriptor descriptor, MetadataVisibility visibility,
        ServerCallContext context)
    {
        var layer = visibility.Layer;
        if (layer.Snapshot is null)
        {
            return;
        }
        foreach (var relationship in layer.Resource.Relationships)
        {
            var projected = await MetadataRelationshipAsync(visibility, relationship, context).ConfigureAwait(false);
            if (projected is not null)
            {
                descriptor.Relationships.Add(projected);
            }
        }
    }

    private static async Task<Proto.RelationshipDescriptor?> MetadataRelationshipAsync(MetadataVisibility visibility,
        MetadataV2Relationship relationship, ServerCallContext context)
    {
        var (layer, visible) = visibility;
        var snapshot = layer.Snapshot!;
        var id = relationship.GeoServicesRelationshipId;
        if (!id.HasValue && int.TryParse(relationship.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            id = parsed;
        }
        if (!id.HasValue || id < 0)
        {
            return null;
        }
        foreach (var publication in snapshot.PublicationsForService(layer.Service.Metadata.Id)
                     .Where(publication => publication.ResourceId == relationship.RelatedResourceId &&
                                           publication.LayerIndex.HasValue && snapshot.IsRoutable(publication))
                     .OrderBy(publication => publication.LayerIndex))
        {
            var related = snapshot.ResolveResource(publication)!;
            var decision = await AccessPolicyHelpers.EvaluateResourceAccessAsync(context.GetHttpContext(), related,
                layer.Service, AuthorizationOperation.Query, context.CancellationToken).ConfigureAwait(false);
            if (!decision.IsAllowed || snapshot.ResolveStorageLayerId(publication, related) is not { } storageId)
            {
                continue;
            }
            var relatedFields = (await VisibleMetadataFieldsAsync(
                CreateLayerContext(layer.Service, publication, related, storageId, snapshot), context).ConfigureAwait(false))
                .Select(field => field.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var destination = relationship.Role.Equals("destination", StringComparison.OrdinalIgnoreCase);
            if (!visible.Contains(destination ? relationship.DestinationField : relationship.OriginField) ||
                !relatedFields.Contains(destination ? relationship.OriginField : relationship.DestinationField))
            {
                continue;
            }
            return ProjectRelationship(relationship, publication.LayerIndex!.Value, id.Value);
        }
        return null;
    }

    private static Proto.RelationshipDescriptor ProjectRelationship(MetadataV2Relationship relationship,
        int relatedLayerId, int id)
    {
        return new Proto.RelationshipDescriptor
        {
            RelationshipId = id,
            Name = relationship.Name,
            RelatedLayerId = relatedLayerId,
            OriginKeyField = relationship.OriginField,
            DestinationKeyField = relationship.DestinationField,
            Description = relationship.Description ?? string.Empty,
            Composite = relationship.Composite,
            Role = relationship.Role.ToLowerInvariant() switch
            {
                "origin" => Proto.RelationshipRole.Origin,
                "destination" => Proto.RelationshipRole.Destination,
                _ => Proto.RelationshipRole.Unspecified
            },
            Cardinality = relationship.Cardinality.ToLowerInvariant() switch
            {
                "one-to-one" => Proto.RelationshipCardinality.OneToOne,
                "one-to-many" => Proto.RelationshipCardinality.OneToMany,
                "many-to-many" => Proto.RelationshipCardinality.ManyToMany,
                _ => Proto.RelationshipCardinality.Unspecified
            }
        };
    }

}
