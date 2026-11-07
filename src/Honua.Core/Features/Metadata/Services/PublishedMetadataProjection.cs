// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Claims;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Portal.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.Metadata.Services;

public sealed class PublishedMetadataProjection(
    IAccessPolicyEvaluator accessPolicyEvaluator,
    IPortalResourceReadAuthorizer readAuthorizer)
{
    private static readonly ClaimsPrincipal AnonymousPrincipal = new(new ClaimsIdentity());
    private static readonly ClaimsPrincipal AuthenticatedNoRolePrincipal =
        new(new ClaimsIdentity(authenticationType: "PortalAccessProbe"));

    public PublishedMetadataService? ProjectService(PublishedMetadataRequest request, MetadataV2Service service)
    {
        var snapshot = request.Snapshot;
        if (!service.IsRoutable() || !ReferenceEquals(service, snapshot.Index.ServicesById.GetValueOrDefault(service.Metadata.Id)))
        {
            return null;
        }

        var publications = snapshot.PublicationsForService(service.Metadata.Id);
        var visible = publications.Where(snapshot.IsDiscoverable)
            .Where(publication => ReferenceEquals(publication, snapshot.Index.PublicationsById[publication.Metadata.Id]))
            .Where(publication => readAuthorizer.Evaluate(
                request.Principal, snapshot.ResolveResource(publication), service, publication).IsAllowed)
            .ToArray();
        if (publications.Count > 0 && visible.Length == 0)
        {
            return null;
        }
        if (publications.Count == 0 && !readAuthorizer.Evaluate(request.Principal, null, service).IsAllowed)
        {
            return null;
        }

        var firstResource = visible.Length == 0 ? null : snapshot.ResolveResource(visible[0]);
        return new PublishedMetadataService
        {
            Service = service,
            Publications = visible,
            Access = DeriveAccessTier(firstResource?.AccessPolicy, service.AccessPolicy),
            FirstResource = firstResource,
            Url = ServiceUrl(request, service)
        };
    }

    public string DeriveAccessTier(AccessPolicy? layerPolicy, AccessPolicy? servicePolicy)
    {
        if (accessPolicyEvaluator.Evaluate(AnonymousPrincipal, layerPolicy, servicePolicy, scope: null).IsAllowed)
        {
            return PortalAccessLevels.Public;
        }
        return accessPolicyEvaluator.Evaluate(AuthenticatedNoRolePrincipal, layerPolicy, servicePolicy, scope: null).IsAllowed
            ? PortalAccessLevels.Org
            : PortalAccessLevels.Private;
    }

    public static string? ServiceUrl(PublishedMetadataRequest request, MetadataV2Service service)
    {
        var baseUrl = request.BaseUrl.TrimEnd('/');
        if (service.PrimaryProtocol is { } protocol && MetadataV2ServicePaths.ServiceTypes.Contains(protocol))
        {
            return $"{baseUrl}{MetadataV2ServicePaths.GeoServicesPath(request.Snapshot.Graph, service)}/{protocol}";
        }
        return service.Route is { Length: > 0 } route && route.StartsWith('/') && !route.StartsWith("//", StringComparison.Ordinal)
            ? baseUrl + route
            : null;
    }

    public static string? PublicationUrl(
        PublishedMetadataRequest request, MetadataV2Service service, MetadataV2Publication publication)
    {
        var serviceUrl = ServiceUrl(request, service);
        if (publication.LayerIndex is { } index && service.PrimaryProtocol is { } protocol
            && MetadataV2ServicePaths.ServiceTypes.Contains(protocol))
        {
            return $"{serviceUrl}/{index.ToString(CultureInfo.InvariantCulture)}";
        }
        return publication.Path is { Length: > 0 } path && path.StartsWith('/') && !path.StartsWith("//", StringComparison.Ordinal)
            ? request.BaseUrl.TrimEnd('/') + path
            : serviceUrl;
    }

    public static string ResolveOwner(MetadataV2ObjectMetadata metadata)
        => string.IsNullOrWhiteSpace(metadata.Publisher) ? "honua" : metadata.Publisher;
}

public sealed record PublishedMetadataRequest(MetadataV2GraphSnapshot Snapshot, ClaimsPrincipal Principal, string BaseUrl);

public sealed record PublishedMetadataService
{
    public required MetadataV2Service Service { get; init; }
    public required IReadOnlyList<MetadataV2Publication> Publications { get; init; }
    public required string Access { get; init; }
    public MetadataV2Resource? FirstResource { get; init; }
    public string? Url { get; init; }
}
