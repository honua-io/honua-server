// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Claims;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Metadata.Services;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.Portal.Abstractions;
using Honua.Core.Features.Portal.Domain;
using Honua.Core.Features.Security.Abstractions;

namespace Honua.Core.Features.Portal.Services;

public sealed class PortalItemProjector : IPortalItemProjector
{
    private readonly PublishedMetadataProjection _catalogProjection;
    private readonly IGeoServicesConstructCapabilityRegistry _capabilityRegistry;

    public PortalItemProjector(
        IAccessPolicyEvaluator accessPolicyEvaluator,
        IGeoServicesConstructCapabilityRegistry capabilityRegistry,
        IPortalResourceReadAuthorizer readAuthorizer)
    {
        ArgumentNullException.ThrowIfNull(accessPolicyEvaluator);
        ArgumentNullException.ThrowIfNull(capabilityRegistry);
        _capabilityRegistry = capabilityRegistry;
        ArgumentNullException.ThrowIfNull(readAuthorizer);
        _catalogProjection = new PublishedMetadataProjection(accessPolicyEvaluator, readAuthorizer);
    }

    public IReadOnlyList<PortalItem> ProjectVisibleItems(
        MetadataV2GraphSnapshot snapshot,
        ClaimsPrincipal principal,
        string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(baseUrl);

        var items = new List<PortalItem>();
        foreach (var service in snapshot.Graph.Services)
        {
            var item = TryProjectService(snapshot, service, principal, baseUrl);
            if (item is not null)
            {
                items.Add(item);
            }
        }

        items.Sort(static (a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
        return items;
    }

    public PortalItem? ProjectItem(
        MetadataV2GraphSnapshot snapshot,
        ClaimsPrincipal principal,
        string itemId,
        string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(baseUrl);

        if (string.IsNullOrWhiteSpace(itemId))
        {
            return null;
        }

        foreach (var service in snapshot.Graph.Services)
        {
            if (!string.Equals(service.Metadata.Id, itemId, StringComparison.Ordinal))
            {
                continue;
            }

            return TryProjectService(snapshot, service, principal, baseUrl);
        }

        return null;
    }

    private PortalItem? TryProjectService(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service service,
        ClaimsPrincipal principal,
        string baseUrl)
    {
        if (!TryMapItemType(service.PrimaryProtocol, out var itemType, out var constructKey))
        {
            return null;
        }

        // The facade only serves constructs the shared capability registry marks
        // as servable. Unknown protocols resolve to the manual-review fallback
        // (CanServe == false) and are therefore not surfaced as Portal items.
        if (!_capabilityRegistry.ResolveOrUnknown(constructKey).CanServe)
        {
            return null;
        }

        var projected = _catalogProjection.ProjectService(new(snapshot, principal, baseUrl), service);
        if (projected is null)
        {
            return null;
        }
        return BuildItem(service, projected.FirstResource, itemType, projected.Access, projected.Url!);
    }

    private static PortalItem BuildItem(
        MetadataV2Service service,
        MetadataV2Resource? resource,
        string itemType,
        string access,
        string url)
    {
        var metadata = service.Metadata;
        var title = FirstNonEmpty(metadata.Title, service.EffectivePublicName, metadata.Id);
        var owner = PublishedMetadataProjection.ResolveOwner(metadata);
        var created = ToEpochMilliseconds(metadata.CreatedAt);
        var modified = ToEpochMilliseconds(metadata.UpdatedAt ?? metadata.CreatedAt);

        var (extent, spatialReference) = BuildExtent(resource);

        return new PortalItem
        {
            Id = metadata.Id,
            Owner = owner,
            Created = created,
            Modified = modified,
            Type = itemType,
            TypeKeywords = BuildTypeKeywords(itemType),
            Title = title,
            Snippet = metadata.Description,
            Description = metadata.Description,
            Tags = metadata.Keywords.Count > 0 ? metadata.Keywords : metadata.Tags,
            Url = url,
            Access = access,
            Extent = extent,
            SpatialReference = spatialReference,
            Culture = metadata.Language,
        };
    }

    private static bool TryMapItemType(
        string? primaryProtocol,
        out string itemType,
        out string constructKey)
    {
        switch (primaryProtocol)
        {
            case ServiceProtocols.FeatureServer:
                itemType = PortalItemTypes.FeatureService;
                constructKey = GeoServicesConstructCapabilityRegistry.Keys.FacadeFeatureService;
                return true;
            case ServiceProtocols.MapServer:
                itemType = PortalItemTypes.MapService;
                constructKey = GeoServicesConstructCapabilityRegistry.Keys.FacadeMapService;
                return true;
            case ServiceProtocols.ImageServer:
                itemType = PortalItemTypes.ImageService;
                constructKey = GeoServicesConstructCapabilityRegistry.Keys.FacadeImageService;
                return true;
            default:
                itemType = string.Empty;
                constructKey = string.Empty;
                return false;
        }
    }

    private static IReadOnlyList<string> BuildTypeKeywords(string itemType)
    {
        // Esri clients key capability detection off these tokens. "Data" + "Service"
        // are common to all hosted service items; the type-specific token follows.
        return itemType switch
        {
            PortalItemTypes.FeatureService => ["Data", "Service", "Feature Service", "ArcGIS Server", "Hosted Service"],
            PortalItemTypes.MapService => ["Data", "Service", "Map Service", "ArcGIS Server"],
            PortalItemTypes.ImageService => ["Data", "Service", "Image Service", "ArcGIS Server"],
            _ => ["Data", "Service"],
        };
    }

    private static (IReadOnlyList<IReadOnlyList<double>> Extent, string? SpatialReference) BuildExtent(
        MetadataV2Resource? resource)
    {
        if (resource?.Spatial is not { Bbox: { } bbox } spatial)
        {
            return (Array.Empty<IReadOnlyList<double>>(), null);
        }
        // `spatial is null` is logically implied by `bbox is null` here (bbox comes from
        // `spatial?.Bbox`), but the explicit check is kept so the compiler's nullable-flow
        // analysis narrows `spatial` to non-null below — dropping it would reintroduce a
        // possible-null-dereference warning on `spatial.SpatialReference` further down.
        // `spatial` is already proven non-null above (it is the source of `bbox`),
        // so no need to re-navigate from `resource` here — that would repeat the
        // same null check the compiler just narrowed away.
        var wkid = spatial.SpatialReference?.ResolveSrid();
        var extent = new IReadOnlyList<double>[]
        {
            new[] { bbox.West, bbox.South },
            new[] { bbox.East, bbox.North },
        };

        return (extent, wkid?.ToString(CultureInfo.InvariantCulture));
    }

    private static long ToEpochMilliseconds(DateTimeOffset? timestamp)
        => timestamp.HasValue ? timestamp.Value.ToUnixTimeMilliseconds() : 0L;

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
}
