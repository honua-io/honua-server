// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Authentication;
using Honua.Protocols.OData.Models;

namespace Honua.Protocols.OData.Services;

internal sealed partial class ODataSearchService
{
    private readonly record struct RelatedResourceLookup(
        MetadataV2GraphSnapshot Snapshot,
        MetadataV2Relationship Relationship);

    private sealed class RelatedFeatureGroupContext
    {
        public required ResolvedRelatedResource Related { get; init; }
        public required MetadataV2Relationship Relationship { get; init; }
        public required ExpandRelationshipOptions ExpandOptions { get; init; }
        public required HashSet<long> RequestedIds { get; init; }
        public required AxisOrder AxisOrder { get; init; }
        public required IReadOnlySet<string> HiddenFields { get; init; }
    }

    private static async Task<ResolvedRelatedResource?> ResolveRelatedResourceAsync(
        RelatedResourceLookup lookup,
        HttpContext? context,
        CancellationToken cancellationToken)
    {
        var (snapshot, relationship) = lookup;
        if (string.IsNullOrWhiteSpace(relationship.RelatedResourceId) ||
            !snapshot.Index.ResourcesById.TryGetValue(relationship.RelatedResourceId, out var resource))
        {
            return null;
        }

        var candidates = TenantScopeHelpers.FilterVisiblePublications(
                snapshot,
                snapshot.Index.PublicationsByResource[relationship.RelatedResourceId],
                context is null ? null : TenantScopeHelpers.ResolveRequestTenantId(context))
            .OrderByDescending(p => p.IsPrimary)
            .ThenBy(p => ResolveServiceName(snapshot, p), StringComparer.OrdinalIgnoreCase);

        foreach (var publication in candidates)
        {
            var resolved = await TryResolveRelatedPublicationAsync(
                (snapshot, resource, publication),
                context,
                cancellationToken).ConfigureAwait(false);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        return null;
    }

    private static async Task<ResolvedRelatedResource?> TryResolveRelatedPublicationAsync(
        (MetadataV2GraphSnapshot Snapshot, MetadataV2Object Resource, MetadataV2Layer Publication) candidate,
        HttpContext? context,
        CancellationToken cancellationToken)
    {
        var (snapshot, resource, publication) = candidate;
        if (!snapshot.IsRoutable(publication) ||
            !publication.LayerIndex.HasValue ||
            !snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var service) ||
            !IsODataProtocolEnabled(service))
        {
            return null;
        }

        if (context is not null && !await AccessPolicyHelpers.IsResourceAccessibleAsync(
                context,
                resource,
                service,
                AuthorizationOperation.Query,
                cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var storageLayerId = snapshot.ResolveStorageLayerId(publication)
            ?? snapshot.ResolveStorageLayerId(resource);
        return storageLayerId.HasValue
            ? new ResolvedRelatedResource(resource, publication.LayerIndex.Value, storageLayerId.Value)
            : null;
    }

    private static IReadOnlySet<string> ValidateRelatedSelectFields(
        MetadataV2Object resource,
        HashSet<string>? requestedSelect)
    {
        var relatedHiddenFields = resource.HiddenSchemaFieldNames();
        if (requestedSelect is { Count: > 0 })
        {
            var forbiddenField = requestedSelect.FirstOrDefault(relatedHiddenFields.Contains);
            if (forbiddenField is not null)
            {
                throw new ArgumentException($"Unknown field in $expand $select: {forbiddenField}");
            }
        }

        return relatedHiddenFields;
    }

    private Dictionary<long, List<object?>> GroupRelatedFeatures(
        ImmutableArray<Feature> items,
        RelatedFeatureGroupContext groupContext,
        CancellationToken cancellationToken)
    {
        var childGroups = new Dictionary<long, List<object?>>();
        foreach (var feature in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var originIds = feature.Attributes.TryGetValue(RelatedQuery.OriginObjectIdsAttribute, out var stamped)
                && stamped is IEnumerable<long> ids
                ? ids
                : ReadLegacyOriginIds(feature, groupContext.Relationship.DestinationField);
            var matchingIds = originIds.Where(groupContext.RequestedIds.Contains).Distinct().ToArray();
            if (matchingIds.Length == 0)
            {
                continue;
            }

            var relatedFeatureDict = BuildRelatedFeaturePayload(feature, groupContext);
            foreach (var originId in matchingIds)
            {
                if (!childGroups.TryGetValue(originId, out var children))
                {
                    children = new List<object?>();
                    childGroups.Add(originId, children);
                }

                children.Add(relatedFeatureDict);
            }
        }

        return childGroups;
    }

    private Dictionary<string, object?> BuildRelatedFeaturePayload(
        Feature feature,
        RelatedFeatureGroupContext groupContext)
    {
        var relatedGeometry = ODataGeometryConverter.ConvertWkbToGeometry(
            _geometryService,
            feature.Geometry,
            groupContext.Related.Resource.ReadSrid() ?? 4326,
            groupContext.AxisOrder);
        var relatedAttributes = ODataAttributeSerializer.Serialize(
            feature.Attributes.Remove(RelatedQuery.OriginObjectIdsAttribute));
        var payload = ODataUtilityService.BuildFeaturePayload(new ODataUtilityService.ODataFeaturePayloadRequest
        {
            LayerId = groupContext.Related.PublicLayerId,
            Feature = feature,
            Geometry = relatedGeometry,
            Attributes = relatedAttributes,
            HiddenFields = groupContext.HiddenFields
        });
        return groupContext.ExpandOptions.SelectFields is { Count: > 0 } selectedFields
            ? ODataUtilityService.ApplySelect(payload, string.Join(",", selectedFields))
            : payload;
    }
}
