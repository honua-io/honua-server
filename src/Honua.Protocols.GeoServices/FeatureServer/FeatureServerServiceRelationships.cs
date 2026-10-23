// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Protocols.GeoServices.FeatureServer;

internal static partial class FeatureServerEndpoints
{
    /// <summary>
    /// Projects accessible publications to unique, service-local relationship classes.
    /// Canonical fields describe this resource and its related resource; destination-side
    /// metadata must therefore be reversed when describing the whole relationship class.
    /// </summary>
    internal static ServiceRelationshipInfo[] BuildServiceRelationshipResponseV2(
        IReadOnlyList<(MetadataV2Publication Publication, MetadataV2Resource Resource)> publications,
        MetadataV2GraphSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(publications);
        ArgumentNullException.ThrowIfNull(snapshot);

        var visible = new Dictionary<string, (int LayerId, MetadataV2Resource Resource)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (publication, resource) in publications
            .OrderByDescending(pair => pair.Publication.IsPrimary)
            .ThenBy(pair => pair.Publication.LayerIndex ?? snapshot.ResolveStorageLayerId(pair.Resource) ?? -1))
        {
            if (!snapshot.IsRoutable(publication))
            {
                continue;
            }

            var layerId = publication.LayerIndex ?? snapshot.ResolveStorageLayerId(resource) ?? -1;
            if (layerId >= 0)
            {
                visible.TryAdd(resource.Metadata.Id, (layerId, resource));
            }
        }

        var result = new Dictionary<int, ServiceRelationshipInfo>();
        var identities = new Dictionary<int, (ServiceRelationshipClassIdentity Class, bool DestinationSide)>();
        var reciprocals = new HashSet<int>();
        // Prefer the origin declaration when reciprocal declarations have the same id.
        var declarations = visible.Values
            .SelectMany(local => local.Resource.Relationships.Select(relationship => (Local: local, Relationship: relationship)))
            .OrderBy(pair => IsDestinationRelationshipRole(pair.Relationship.Role))
            .ThenBy(pair => pair.Local.LayerId);
        foreach (var (local, relationship) in declarations)
        {
            if (!visible.TryGetValue(relationship.RelatedResourceId, out var related))
            {
                continue;
            }

            var role = MapEsriRelationshipRole(relationship.Role);
            if (role is not ("esriRelRoleOrigin" or "esriRelRoleDestination"))
            {
                // Other canonical vocabularies do not establish Esri origin/destination roles.
                continue;
            }

            var destinationSide = IsDestinationRelationshipRole(role);
            var origin = destinationSide ? related : local;
            var destination = destinationSide ? local : related;
            var id = relationship.EsriRelationshipId ?? StableStringHash(relationship.Id);
            var originPrimaryKey = destinationSide ? relationship.DestinationField : relationship.OriginField;
            var originForeignKey = destinationSide ? relationship.OriginField : relationship.DestinationField;
            var cardinality = MapEsriCardinality(relationship.Cardinality);
            var identity = new ServiceRelationshipClassIdentity(origin.Resource.Metadata.Id,
                destination.Resource.Metadata.Id, originPrimaryKey, originForeignKey,
                cardinality, relationship.Composite);
            if (identities.TryGetValue(id, out var existing))
            {
                if (existing.Class != identity || existing.DestinationSide == destinationSide || !reciprocals.Add(id))
                {
                    // Resource-local identities cannot safely be remapped independently of
                    // the layer metadata and related-records query contract.
                    throw new InvalidOperationException($"Relationship class id {id} is ambiguous within the service.");
                }

                // Resource-local names describe traversal in each direction, not
                // the identity of the shared relationship class.
                result[id].BackwardPathLabel = relationship.Name;
                continue;
            }

            identities.Add(id, (identity, destinationSide));
            result.Add(id, new ServiceRelationshipInfo
            {
                Id = id,
                Name = relationship.Name,
                BackwardPathLabel = relationship.Name,
                ForwardPathLabel = relationship.Name,
                OriginLayerId = origin.LayerId,
                DestinationLayerId = destination.LayerId,
                OriginPrimaryKey = originPrimaryKey,
                OriginForeignKey = originForeignKey,
                DestinationPrimaryKey = destination.Resource.SchemaFields
                    .FirstOrDefault(field => field.SemanticRoles.Any(semanticRole =>
                        semanticRole.Equals("id.primary", StringComparison.OrdinalIgnoreCase)))?.Name
                    ?? GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(destination.Resource),
                Cardinality = cardinality,
                Composite = relationship.Composite,
                Description = relationship.Description
            });
        }

        return [.. result.Values.OrderBy(relationship => relationship.Id)];
    }

    private sealed record ServiceRelationshipClassIdentity(
        string OriginResourceId, string DestinationResourceId, string OriginPrimaryKey,
        string OriginForeignKey, string Cardinality, bool Composite);
}
