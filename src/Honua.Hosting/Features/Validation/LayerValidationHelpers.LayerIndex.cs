// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Infrastructure.Validation;

internal static partial class LayerValidationHelpers
{
    private static IResult CreateProtocolError(
        HttpContext context,
        ValidationProtocol protocol,
        (string Message, int StatusCode) error)
        => protocol switch
        {
            ValidationProtocol.OData => CreateODataError(context, error.Message, error.StatusCode),
            ValidationProtocol.OgcFeatures => CreateOgcError(context, error.Message, error.StatusCode),
            ValidationProtocol.ProblemJson => CreateProblemJsonError(context, error.Message, error.StatusCode),
            _ => error.StatusCode == StatusCodes.Status409Conflict
                ? StandardErrorHelpers.CreateConflict(context, error.Message)
                : StandardErrorHelpers.CreateNotFound(context, error.Message),
        };

    private static MetadataV2ValidationResult? TryCreateAmbiguousLayerResult(
        HttpContext context,
        string? ambiguityError,
        ValidationProtocol? protocol = null)
    {
        if (ambiguityError is null)
        {
            return null;
        }

        var error = protocol is { } targetProtocol
            ? CreateProtocolError(context, targetProtocol, (ambiguityError, StatusCodes.Status409Conflict))
            : StandardErrorHelpers.CreateConflict(context, ambiguityError);
        return new MetadataV2ValidationResult(false, null, null, null, error);
    }

    private static (MetadataV2Layer? Publication, MetadataV2Object? Resource, MetadataV2Service? Service, string? AmbiguityError)
        ResolveV2Triple(
            HttpContext context,
            MetadataV2GraphSnapshot snapshot,
            int layerId,
            string? requiredProtocol)
        => ResolveV2TripleForTenant(
            snapshot,
            layerId,
            requiredProtocol,
            TenantScopeHelpers.ResolveRequestTenantId(context),
            applyTenantScope: true);

    internal static (MetadataV2Layer? Publication, MetadataV2Object? Resource, MetadataV2Service? Service, string? AmbiguityError)
        ResolveV2TripleForTenant(
            MetadataV2GraphSnapshot snapshot,
            int layerId,
            string? requiredProtocol,
            string? tenantId,
            bool applyTenantScope)
    {
        bool IsVisible(MetadataV2Layer p)
        {
            if (!applyTenantScope)
            {
                return true;
            }

            var resource = snapshot.ResolveResource(p);
            var service = snapshot.Index.ServicesById.TryGetValue(p.ServiceId, out var resolvedService)
                ? resolvedService
                : null;
            return MetadataV2TenantVisibility.IsVisibleToTenant(p, resource, service, tenantId);
        }

        var storageCandidates = snapshot.PublicationsForStorageLayer(layerId).Where(IsVisible).ToList();
        if (storageCandidates.Count > 0)
        {
            return SelectTripleFromCandidates(snapshot, storageCandidates, requiredProtocol);
        }

        var indexCandidates = snapshot.Graph.Publications
            .Where(p => p.LayerIndex == layerId && snapshot.IsRoutable(p) && IsVisible(p))
            .ToList();
        if (indexCandidates.Count == 0)
        {
            return (null, null, null, null);
        }

        var ambiguity = CheckAmbiguousLayerCandidates(snapshot, indexCandidates, (layerId, requiredProtocol));
        return ambiguity is not null
            ? (null, null, null, ambiguity)
            : SelectTripleFromCandidates(snapshot, indexCandidates, requiredProtocol);
    }

    private static (MetadataV2Layer? Publication, string? AmbiguityError) TryResolveNumericCollection(
        MetadataV2GraphSnapshot snapshot,
        (string CollectionId, string? RequiredProtocol) lookup,
        Func<MetadataV2Layer, bool> isVisible)
    {
        if (!int.TryParse(lookup.CollectionId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericId))
        {
            return (null, null);
        }

        var storageCandidates = snapshot.PublicationsForStorageLayer(numericId).Where(isVisible).ToList();
        if (storageCandidates.Count > 0)
        {
            var (pub, _, _, _) = SelectTripleFromCandidates(snapshot, storageCandidates, lookup.RequiredProtocol);
            return (pub, null);
        }

        var indexCandidates = snapshot.Graph.Publications
            .Where(p => p.LayerIndex == numericId && isVisible(p))
            .ToList();
        if (indexCandidates.Count == 0)
        {
            return (null, null);
        }

        var ambiguity = CheckAmbiguousLayerCandidates(snapshot, indexCandidates, (numericId, lookup.RequiredProtocol));
        if (ambiguity is not null)
        {
            return (null, ambiguity);
        }

        var (matched, _, _, _) = SelectTripleFromCandidates(snapshot, indexCandidates, lookup.RequiredProtocol);
        return (matched, null);
    }

    private static string? CheckAmbiguousLayerCandidates(
        MetadataV2GraphSnapshot snapshot,
        List<MetadataV2Layer> candidates,
        (int LayerId, string? RequiredProtocol) lookup)
    {
        IReadOnlyList<MetadataV2Layer> effective = candidates;
        if (!string.IsNullOrWhiteSpace(lookup.RequiredProtocol))
        {
            var protocolEnabled = candidates
                .Where(p => snapshot.Index.ServicesById.TryGetValue(p.ServiceId, out var s) &&
                            MetadataV2ServiceProtocols.IsProtocolEnabled(s, lookup.RequiredProtocol))
                .ToArray();
            if (protocolEnabled.Length > 0)
            {
                effective = protocolEnabled;
            }
        }

        if (!effective.Select(static p => p.ResourceId).Distinct(StringComparer.Ordinal).Skip(1).Any())
        {
            return null;
        }

        var serviceNames = effective
            .Select(p => snapshot.Index.ServicesById.TryGetValue(p.ServiceId, out var service)
                && !string.IsNullOrWhiteSpace(service.Metadata.Name)
                    ? service.Metadata.Name
                    : p.ServiceId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .Select(static name => $"'{name}'");
        return $"Layer index {lookup.LayerId} is ambiguous across multiple services ({string.Join(", ", serviceNames)}). Use the storage layer id or a service-scoped route.";
    }

    private static (MetadataV2Layer? Publication, MetadataV2Object? Resource, MetadataV2Service? Service, string? AmbiguityError)
        SelectTripleFromCandidates(
            MetadataV2GraphSnapshot snapshot,
            List<MetadataV2Layer> candidatePublications,
            string? requiredProtocol)
    {
        if (!string.IsNullOrWhiteSpace(requiredProtocol))
        {
            var preferred = candidatePublications
                .Where(p =>
                    snapshot.Index.ServicesById.TryGetValue(p.ServiceId, out var s) &&
                    MetadataV2ServiceProtocols.IsProtocolEnabled(s, requiredProtocol))
                .OrderByDescending(p => MetadataV2ServiceProtocols.IsPreferredPublicationType(requiredProtocol, p.PublicationType))
                .ThenByDescending(p => p.IsPrimary)
                .FirstOrDefault();
            if (preferred is not null)
            {
                var preferredResource = snapshot.ResolveResource(preferred);
                var preferredService = snapshot.Index.ServicesById.GetValueOrDefault(preferred.ServiceId);
                return (preferred, preferredResource, preferredService, null);
            }
        }

        var publication = candidatePublications
            .OrderByDescending(p => p.IsPrimary)
            .ThenBy(p => snapshot.Index.ServicesById.TryGetValue(p.ServiceId, out var s) ? s.Metadata.Name : p.ServiceId,
                StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (publication is null)
        {
            return (null, null, null, null);
        }

        return (publication, snapshot.ResolveResource(publication), snapshot.Index.ServicesById.GetValueOrDefault(publication.ServiceId), null);
    }

    private static (MetadataV2Layer Publication, MetadataV2Service Service)[] ResolveVisibleProtocolCandidates(
        HttpContext context,
        MetadataV2GraphSnapshot snapshot,
        (int LayerId, string? RequiredProtocol) lookup)
    {
        var byStorage = snapshot.PublicationsForStorageLayer(lookup.LayerId)
            .Select(p => (Publication: p, Service: ResolveVisibleProtocolService(context, snapshot, p, lookup.RequiredProtocol)))
            .Where(static candidate => candidate.Service is not null)
            .Select(static candidate => (candidate.Publication, candidate.Service!))
            .ToArray();
        if (byStorage.Length > 0)
        {
            return byStorage;
        }

        var byIndex = snapshot.Graph.Publications
            .Where(p => p.LayerIndex == lookup.LayerId && snapshot.IsRoutable(p))
            .Select(p => (Publication: p, Service: ResolveVisibleProtocolService(context, snapshot, p, lookup.RequiredProtocol)))
            .Where(static candidate => candidate.Service is not null)
            .Select(static candidate => (candidate.Publication, candidate.Service!))
            .ToArray();
        return byIndex.Select(static c => c.Publication.ResourceId).Distinct(StringComparer.Ordinal).Skip(1).Any()
            ? []
            : byIndex;
    }
}
