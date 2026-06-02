// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Claims;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Portal.Abstractions;
using Honua.Core.Features.Portal.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.Portal.Services;

/// <summary>
/// Default <see cref="IPortalItemProjector"/> implementation. Maps Esri-shaped
/// Metadata v2 services (FeatureServer / MapServer / ImageServer) to Portal item
/// documents, deriving the item <c>url</c> from the caller-supplied base URL and
/// the <c>access</c> tier from the resource/service <see cref="AccessPolicy"/> via
/// the shared <see cref="IAccessPolicyEvaluator"/>.
/// </summary>
public sealed class PortalItemProjector : IPortalItemProjector
{
    // A throwaway authenticated principal used purely to probe the access tier:
    // if an authenticated-but-role-less principal is allowed, the item is "org";
    // otherwise it is "private" (role-restricted).
    private static readonly ClaimsPrincipal AnonymousPrincipal = new(new ClaimsIdentity());
    private static readonly ClaimsPrincipal AuthenticatedNoRolePrincipal =
        new(new ClaimsIdentity(authenticationType: "PortalAccessProbe"));

    private readonly IAccessPolicyEvaluator _accessPolicyEvaluator;

    /// <summary>
    /// Initializes a new instance of the <see cref="PortalItemProjector"/> class.
    /// </summary>
    /// <param name="accessPolicyEvaluator">
    /// The shared access-policy evaluator that decides whether a principal may read
    /// a resource/service. This is the same seam the protocol endpoints enforce
    /// through, so the Portal <c>access</c> tier cannot drift from the real RBAC
    /// decision.
    /// </param>
    public PortalItemProjector(IAccessPolicyEvaluator accessPolicyEvaluator)
    {
        ArgumentNullException.ThrowIfNull(accessPolicyEvaluator);
        _accessPolicyEvaluator = accessPolicyEvaluator;
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
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
        if (!TryMapItemType(service.PrimaryProtocol, out var itemType, out var urlType))
        {
            return null;
        }

        // Collect the resources published through this service so RBAC and extent
        // can be computed from the resource + service policy composition.
        var publications = snapshot.PublicationsForService(service.Metadata.Id);
        MetadataV2Resource? firstVisibleResource = null;
        var anyResource = false;
        var anyVisible = false;

        foreach (var publication in publications)
        {
            var resource = snapshot.ResolveResource(publication);
            if (resource is null)
            {
                continue;
            }

            anyResource = true;
            if (IsAllowed(principal, resource.AccessPolicy, service.AccessPolicy))
            {
                anyVisible = true;
                firstVisibleResource ??= resource;
            }
        }

        // A service with no resolvable publications is gated on the service policy
        // alone; one with publications must have at least one the principal can read.
        if (anyResource)
        {
            if (!anyVisible)
            {
                return null;
            }
        }
        else if (!IsAllowed(principal, layerPolicy: null, service.AccessPolicy))
        {
            return null;
        }

        var access = DeriveAccessTier(firstVisibleResource?.AccessPolicy, service.AccessPolicy);
        return BuildItem(service, firstVisibleResource, itemType, urlType, access, baseUrl);
    }

    private bool IsAllowed(ClaimsPrincipal principal, AccessPolicy? layerPolicy, AccessPolicy? servicePolicy)
        => _accessPolicyEvaluator.Evaluate(principal, layerPolicy, servicePolicy, scope: null).IsAllowed;

    /// <summary>
    /// Projects the composed (resource, service) access policy into the Esri
    /// <c>public</c>/<c>org</c>/<c>private</c> tier by probing the shared evaluator
    /// with synthetic principals — no second ACL model.
    /// </summary>
    private string DeriveAccessTier(AccessPolicy? layerPolicy, AccessPolicy? servicePolicy)
    {
        if (_accessPolicyEvaluator.Evaluate(AnonymousPrincipal, layerPolicy, servicePolicy, scope: null).IsAllowed)
        {
            return PortalAccessLevels.Public;
        }

        return _accessPolicyEvaluator
            .Evaluate(AuthenticatedNoRolePrincipal, layerPolicy, servicePolicy, scope: null).IsAllowed
            ? PortalAccessLevels.Org
            : PortalAccessLevels.Private;
    }

    private static PortalItem BuildItem(
        MetadataV2Service service,
        MetadataV2Resource? resource,
        string itemType,
        string urlType,
        string access,
        string baseUrl)
    {
        var metadata = service.Metadata;
        var title = FirstNonEmpty(metadata.Title, metadata.Name, metadata.Id);
        var owner = FirstNonEmpty(metadata.Publisher, "honua");
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
            Url = BuildServiceUrl(baseUrl, metadata.Name, urlType),
            Access = access,
            Extent = extent,
            SpatialReference = spatialReference,
            Culture = metadata.Language,
        };
    }

    /// <summary>
    /// Builds the item URL pointing at the existing GeoServices service endpoint,
    /// mirroring <c>GeoservicesCatalogEndpoints</c> so the Portal item and the
    /// services directory always agree on host/scheme and route shape.
    /// </summary>
    private static string BuildServiceUrl(string baseUrl, string serviceName, string urlType)
    {
        var trimmedBase = baseUrl.TrimEnd('/');
        var escapedName = Uri.EscapeDataString(serviceName);
        return $"{trimmedBase}/rest/services/{escapedName}/{urlType}";
    }

    /// <summary>
    /// Maps a Metadata v2 primary protocol to the Esri Portal item type and the
    /// GeoServices URL route segment. Returns <see langword="false"/> for
    /// non-Esri protocols (OGC API, STAC, OData, …) which are not Portal items.
    /// </summary>
    private static bool TryMapItemType(string? primaryProtocol, out string itemType, out string urlType)
    {
        switch (primaryProtocol)
        {
            case ServiceProtocols.FeatureServer:
                itemType = PortalItemTypes.FeatureService;
                urlType = ServiceProtocols.FeatureServer;
                return true;
            case ServiceProtocols.MapServer:
                itemType = PortalItemTypes.MapService;
                urlType = ServiceProtocols.MapServer;
                return true;
            case ServiceProtocols.ImageServer:
                itemType = PortalItemTypes.ImageService;
                urlType = ServiceProtocols.ImageServer;
                return true;
            default:
                itemType = string.Empty;
                urlType = string.Empty;
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
        var bbox = resource?.Spatial?.Bbox;
        if (bbox is null)
        {
            return (Array.Empty<IReadOnlyList<double>>(), null);
        }

        var wkid = resource?.Spatial?.SpatialReference?.ResolveSrid();
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
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }
}
