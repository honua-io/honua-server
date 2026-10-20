// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Studio.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Studio.Models;
#if !HONUA_EXCLUDE_GEOSERVICES
using Honua.Protocols.GeoServices.Catalog;
#endif

namespace Honua.Server.Features.Studio;

/// <summary>Current-reader metadata for the sources a sealed composition actually references.</summary>
internal static class StudioPublishedSourceProjection
{
    internal static async Task<StudioPublishedSource[]> ReadAsync(
        HttpContext context,
        IMetadataV2GraphProvider provider,
        JsonElement? body,
        CancellationToken cancellationToken)
    {
        var requested = ReferencedSourceIds(body);
        if (requested.Count == 0)
        {
            return [];
        }

#if HONUA_EXCLUDE_GEOSERVICES
        _ = context;
        _ = provider;
        await Task.CompletedTask.ConfigureAwait(false);
        return [];
#else
        var snapshot = await provider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var candidates = new List<SourceCandidate>();
        foreach (var service in snapshot.Graph.Services)
        {
            var servicePublications = snapshot.PublicationsForService(service.Metadata.Id);
            var unambiguousLegacyAlias = servicePublications.Where(snapshot.IsRoutable)
                .Select(static publication => publication.LayerIndex).OfType<int>().Distinct().Take(2).Count() == 1;
            foreach (var publication in servicePublications)
            {
                var resource = snapshot.ResolveResource(publication);
                if (publication.LayerIndex is not { } layerId || layerId < 0
                    || !snapshot.IsRoutable(publication)
                    || !TenantScopeHelpers.IsPublicationVisible(context, publication, resource, service))
                {
                    continue;
                }

                var id = StudioCatalogSourceIdentity.Create(service.Metadata.Id, layerId);
                // Authority must not narrow an ambiguous legacy alias down to a
                // different layer. Canonical ids retain their exact local index.
                if (!requested.Contains(id) && !(unambiguousLegacyAlias && requested.Contains(service.Metadata.Id)))
                {
                    continue;
                }

                var access = await AccessPolicyHelpers.EvaluateResourceAccessAsync(
                    context, resource!, service, AuthorizationOperation.Metadata, cancellationToken).ConfigureAwait(false);
                if (access.IsAllowed)
                {
                    candidates.Add(new SourceCandidate(id, service, publication, resource!, layerId,
                        snapshot.ResolveStorageBinding(publication)?.Metadata.Id));
                }
            }
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        // Reuse the serving directory's protocol gating, actual URLs and reader
        // authorization. A source id is never reverse-engineered into a service name.
        var scoped = new MetadataV2GraphSnapshot(snapshot.Graph with
        {
            Services = candidates.Select(static entry => entry.Service).Distinct().ToArray(),
            Publications = candidates.Select(static entry => entry.Publication).Distinct().ToArray(),
            Resources = candidates.Select(static entry => entry.Resource).Distinct().ToArray(),
        }, snapshot.Etag, snapshot.LoadedAt);
        var directory = await GeoServicesCatalogProjection.ReadFeatureMapAsync(context, scoped).ConfigureAwait(false);
        var results = new Dictionary<string, (StudioPublishedSource Source, string ResourceId, string? BindingId)>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var routes = directory.Where(entry => entry.Type == ServiceProtocols.FeatureServer
                && string.Equals(entry.Name, candidate.Service.Metadata.Name, StringComparison.OrdinalIgnoreCase))
                .Select(static entry => entry.Url).Distinct(StringComparer.Ordinal).ToArray();
            if (routes.Length == 0)
            {
                continue;
            }

            if (routes.Length != 1)
            {
                throw new InvalidOperationException("Published source route is ambiguous.");
            }

            var title = !string.IsNullOrWhiteSpace(candidate.Publication.TitleOverride)
                ? candidate.Publication.TitleOverride
                : !string.IsNullOrWhiteSpace(candidate.Resource.Metadata.Title)
                    ? candidate.Resource.Metadata.Title
                    : candidate.Resource.Metadata.Name;
            var descriptor = new StudioPublishedSource
            {
                Id = candidate.Id,
                Title = title!,
                GeometryType = (candidate.Resource.Spatial?.GeometryType ?? MetadataV2GeometryType.None).ToString(),
                Locator = new StudioPublishedSourceLocator
                {
                    Url = routes[0],
                    ServiceId = candidate.Service.Metadata.Id,
                    LayerId = candidate.LayerId,
                },
            };
            if (results.TryGetValue(candidate.Id, out var existing)
                && (existing.ResourceId != candidate.Resource.Metadata.Id
                    || existing.BindingId != candidate.BindingId || existing.Source != descriptor))
            {
                throw new InvalidOperationException("Published source identity is ambiguous.");
            }

            results[candidate.Id] = (descriptor, candidate.Resource.Metadata.Id, candidate.BindingId);
        }

        return results.Values.Select(static entry => entry.Source).OrderBy(static entry => entry.Id, StringComparer.Ordinal).ToArray();
#endif
    }

    private static HashSet<string> ReferencedSourceIds(JsonElement? body)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (body is not { ValueKind: JsonValueKind.Object } composition)
        {
            return ids;
        }

        foreach (var key in new[] { "layers", "widgets", "sourceBindings" })
        {
            if (!composition.TryGetProperty(key, out var members) || members.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var member in members.EnumerateArray())
            {
                if (member.ValueKind == JsonValueKind.Object
                    && member.TryGetProperty("sourceId", out var sourceId)
                    && sourceId.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(sourceId.GetString()))
                {
                    ids.Add(sourceId.GetString()!);
                }
            }
        }

        return ids;
    }

    private sealed record SourceCandidate(
        string Id,
        MetadataV2Service Service,
        MetadataV2Publication Publication,
        MetadataV2Resource Resource,
        int LayerId,
        string? BindingId);
}
