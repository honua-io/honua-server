// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
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
    private const string FeatureRenderingProtocol = "geoservices-feature-service";
    private const string MapRenderingProtocol = "geoservices-map-service";

    internal static async Task<StudioPublishedSource[]> ReadAsync(
        HttpContext context,
        IMetadataV2GraphProvider provider,
        JsonElement? body,
        CancellationToken cancellationToken)
    {
        var requested = ParseRequest(body);
        if (requested.Tokens.Count == 0)
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
        var matches = new List<SourceCandidate>();
        foreach (var service in snapshot.Graph.Services)
        {
            var servicePublications = snapshot.PublicationsForService(service.Metadata.Id);
            var unambiguousLegacyAlias = servicePublications.Where(snapshot.IsRoutable)
                .Select(static publication => publication.LayerIndex).OfType<int>().Distinct().Take(2).Count() == 1;
            foreach (var publication in servicePublications)
            {
                var resource = snapshot.ResolveResource(publication);
                if (publication.LayerIndex is not { } layerId || layerId < 0
                    || resource is null
                    || !snapshot.IsRoutable(publication))
                {
                    continue;
                }

                if (!StudioCatalogSourceIdentity.TryCreate(service.Metadata.Id, layerId, out var id))
                {
                    continue;
                }

                if (!Matches(requested, service, layerId, id, unambiguousLegacyAlias))
                {
                    continue;
                }

                matches.Add(new SourceCandidate(
                    id,
                    service,
                    publication,
                    resource,
                    layerId,
                    snapshot.ResolveStorageBinding(publication)?.Metadata.Id,
                    BindingProtocol(requested, service, layerId)));
            }
        }

        foreach (var group in matches.GroupBy(static candidate => candidate.Id, StringComparer.Ordinal))
        {
            if (group.Select(static candidate => candidate.Resource.Metadata.Id).Distinct(StringComparer.Ordinal).Skip(1).Any()
                || group.Select(static candidate => candidate.BindingId).Distinct(StringComparer.Ordinal).Skip(1).Any())
            {
                throw new InvalidOperationException("Published source identity is ambiguous.");
            }
        }

        var authorized = new List<SourceCandidate>();
        foreach (var candidate in matches)
        {
            if (!TenantScopeHelpers.IsPublicationVisible(
                    context, candidate.Publication, candidate.Resource, candidate.Service))
            {
                continue;
            }

            var access = await AccessPolicyHelpers.EvaluateResourceAccessAsync(
                context, candidate.Resource, candidate.Service, AuthorizationOperation.Metadata, cancellationToken)
                .ConfigureAwait(false);
            if (access.IsAllowed)
            {
                authorized.Add(candidate);
            }
        }

        if (authorized.Count == 0)
        {
            return [];
        }

        // Reuse the serving directory's protocol gating, actual URLs and reader
        // authorization. A source id is never reverse-engineered into a service name.
        var scoped = new MetadataV2GraphSnapshot(snapshot.Graph with
        {
            Services = authorized.Select(static entry => entry.Service).Distinct().ToArray(),
            Publications = authorized.Select(static entry => entry.Publication).Distinct().ToArray(),
            Resources = authorized.Select(static entry => entry.Resource).Distinct().ToArray(),
        }, snapshot.Etag, snapshot.LoadedAt);
        var directory = await GeoServicesCatalogProjection.ReadFeatureMapAsync(context, scoped).ConfigureAwait(false);
        var results = new Dictionary<string, (StudioPublishedSource Source, string ResourceId, string? BindingId)>(StringComparer.Ordinal);
        foreach (var candidate in authorized)
        {
            var directoryType = SelectDirectoryType(candidate.Service, candidate.BindingProtocol);
            if (directoryType is null)
            {
                continue;
            }

            if (SharesPublicRoute(snapshot, candidate.Service, candidate.LayerId, directoryType))
            {
                throw new InvalidOperationException("Published source route is ambiguous.");
            }

            var routes = directory.Where(entry => entry.Type == directoryType
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
                Protocol = directoryType == ServiceProtocols.MapServer ? MapRenderingProtocol : FeatureRenderingProtocol,
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

    private static SourceRequest ParseRequest(JsonElement? body)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var locators = new Dictionary<string, LocatorTarget>(StringComparer.Ordinal);
        if (body is not { ValueKind: JsonValueKind.Object } composition)
        {
            return new SourceRequest(tokens, locators);
        }

        foreach (var key in new[] { "layers", "widgets", "sourceBindings" })
        {
            if (!composition.TryGetProperty(key, out var members) || members.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var member in members.EnumerateArray().Where(static item => item.ValueKind == JsonValueKind.Object))
            {
                if (!member.TryGetProperty("sourceId", out var sourceId)
                    || sourceId.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(sourceId.GetString()))
                {
                    continue;
                }

                var id = sourceId.GetString()!;
                tokens.Add(id);
                if (key != "sourceBindings"
                    || !member.TryGetProperty("locator", out var locator)
                    || locator.ValueKind != JsonValueKind.Object
                    || !locator.TryGetProperty("serviceId", out var serviceId)
                    || serviceId.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(serviceId.GetString())
                    || !TryReadLayerId(locator, out var layerId))
                {
                    continue;
                }

                var protocol = member.TryGetProperty("protocol", out var protocolValue)
                    && protocolValue.ValueKind == JsonValueKind.String
                    ? protocolValue.GetString()
                    : null;
                locators[id] = new LocatorTarget(serviceId.GetString()!, layerId, protocol);
            }
        }

        return new SourceRequest(tokens, locators);
    }

    private static bool Matches(
        SourceRequest requested,
        MetadataV2Service service,
        int layerId,
        string canonicalId,
        bool unambiguousLegacyAlias)
    {
        if (requested.Tokens.Contains(canonicalId) && !requested.Locators.ContainsKey(canonicalId))
        {
            return true;
        }

        foreach (var (sourceId, locator) in requested.Locators)
        {
            if (requested.Tokens.Contains(sourceId)
                && string.Equals(locator.ServiceId, service.Metadata.Id, StringComparison.Ordinal)
                && locator.LayerId == layerId)
            {
                return true;
            }
        }

        return unambiguousLegacyAlias
            && requested.Tokens.Contains(service.Metadata.Id)
            && !requested.Locators.ContainsKey(service.Metadata.Id);
    }

    private static string? BindingProtocol(SourceRequest requested, MetadataV2Service service, int layerId)
    {
        string? protocol = null;
        var seen = false;
        foreach (var (sourceId, locator) in requested.Locators)
        {
            if (!requested.Tokens.Contains(sourceId)
                || !string.Equals(locator.ServiceId, service.Metadata.Id, StringComparison.Ordinal)
                || locator.LayerId != layerId)
            {
                continue;
            }

            if (!seen)
            {
                protocol = locator.Protocol;
                seen = true;
                continue;
            }

            if (!string.Equals(protocol, locator.Protocol, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Published source identity is ambiguous.");
            }
        }

        return protocol;
    }

    private static string? SelectDirectoryType(MetadataV2Service service, string? bindingProtocol)
    {
        var hasFeature = service.Protocols.Contains(ServiceProtocols.FeatureServer, StringComparer.Ordinal);
        var hasMap = service.Protocols.Contains(ServiceProtocols.MapServer, StringComparer.Ordinal);
        if (bindingProtocol is "geoservices_map_service")
        {
            return hasMap ? ServiceProtocols.MapServer : null;
        }

        if (bindingProtocol is "geoservices_feature_service")
        {
            return hasFeature ? ServiceProtocols.FeatureServer : null;
        }

        if (hasMap && !hasFeature)
        {
            return ServiceProtocols.MapServer;
        }

        return hasFeature ? ServiceProtocols.FeatureServer : null;
    }

    private static bool SharesPublicRoute(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service service,
        int layerId,
        string directoryType)
    {
        var matches = 0;
        foreach (var other in snapshot.Graph.Services)
        {
            if (!other.IsRoutable()
                || !other.Protocols.Contains(directoryType, StringComparer.Ordinal)
                || !string.Equals(other.Metadata.Name, service.Metadata.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var publishesLayer = snapshot.PublicationsForService(other.Metadata.Id).Any(publication =>
                snapshot.IsRoutable(publication) && publication.LayerIndex == layerId);
            if (publishesLayer && ++matches > 1)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadLayerId(JsonElement locator, out int layerId)
    {
        layerId = 0;
        if (!locator.TryGetProperty("layerId", out var value))
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out layerId))
        {
            return layerId >= 0;
        }

        return value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out layerId)
            && layerId >= 0;
    }

    private sealed record SourceRequest(
        HashSet<string> Tokens,
        Dictionary<string, LocatorTarget> Locators);

    private readonly record struct LocatorTarget(string ServiceId, int LayerId, string? Protocol);

    private sealed record SourceCandidate(
        string Id,
        MetadataV2Service Service,
        MetadataV2Publication Publication,
        MetadataV2Resource Resource,
        int LayerId,
        string? BindingId,
        string? BindingProtocol);
}
