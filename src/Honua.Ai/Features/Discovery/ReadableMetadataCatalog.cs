// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Authentication;

namespace Honua.Ai.Discovery;

/// <summary>Projects the published catalog through the shared metadata access evaluation.</summary>
internal static class ReadableMetadataCatalog
{
    public static async Task<IReadOnlyList<ReadableMetadataPublication>> GetPublicationsAsync(
        HttpContext context,
        MetadataV2GraphSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var publications = new List<ReadableMetadataPublication>();
        foreach (var service in snapshot.Graph.Services)
        {
            foreach (var publication in snapshot.PublicationsForService(service.Metadata.Id))
            {
                var resource = snapshot.ResolveResource(publication);
                if (!snapshot.IsRoutable(publication) ||
                    !TenantScopeHelpers.IsPublicationVisible(context, publication, resource, service))
                {
                    continue;
                }

                var decision = await AccessPolicyHelpers.EvaluateResourceAccessAsync(
                    context, resource!, service, AuthorizationOperation.Metadata, cancellationToken).ConfigureAwait(false);
                if (decision.IsAllowed)
                {
                    publications.Add(new ReadableMetadataPublication(service, publication, resource!));
                }
            }
        }

        return publications;
    }

    public static async Task<IReadOnlyList<MetadataV2Service>> GetServicesAsync(
        HttpContext context,
        IReadOnlyList<ReadableMetadataPublication> publications,
        CancellationToken cancellationToken)
    {
        var services = new List<MetadataV2Service>();
        // codeql[cs/linq/missed-where]: awaits an access check before keeping the service
        foreach (var service in publications.Select(entry => entry.Service).DistinctBy(service => service.Metadata.Id))
        {
            if (await AccessPolicyHelpers.RequireServiceAccessAsync(
                context, service, AuthorizationOperation.Metadata, cancellationToken).ConfigureAwait(false) is null)
            {
                services.Add(service);
            }
        }

        return services;
    }

    public static DefaultHttpContext CreateAccessContext(IServiceProvider services, ClaimsPrincipal? principal)
    {
        // Grounding reads metadata in a fresh scope, while tenant resolution belongs
        // to the active request. Keep its services and use the supplied principal.
        var request = services.GetService<IHttpContextAccessor>()?.HttpContext;
        return new DefaultHttpContext
        {
            RequestServices = request?.RequestServices ?? services,
            User = principal ?? new ClaimsPrincipal(new ClaimsIdentity())
        };
    }
}

internal sealed record ReadableMetadataPublication(
    MetadataV2Service Service,
    MetadataV2Publication Publication,
    MetadataV2Resource Resource);
