// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.MultiTenancy;
using Honua.Server.Features.Protocols.Tiles.PMTilesProxy;

namespace Honua.Server.Features.Admin.TileOperations;

internal static class TileOperationTenantAuthorization
{
    public static async Task<ProblemDetailsResponse?> AuthorizeStartAsync(
        HttpContext context,
        TileOperationStartRequest request,
        CancellationToken cancellationToken)
    {
        if (OperationTenantAuthorization.HasCrossTenantAccess(context))
        {
            return null;
        }

        var hasServiceId = !string.IsNullOrWhiteSpace(request.ServiceId);
        var hasLayerId = request.LayerId.HasValue;
        if (!hasServiceId && !hasLayerId)
        {
            return ProblemDetailsHelpers.CreateAdminProblemDetails(
                context,
                StatusCodes.Status403Forbidden,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status403Forbidden),
                "Global unscoped tile operations require platform administrator authority.");
        }

        var snapshot = await context.RequestServices
            .GetRequiredService<IMetadataV2GraphProvider>()
            .GetCurrentAsync(cancellationToken)
            .ConfigureAwait(false);
        var visible = hasServiceId
            ? IsServiceTargetVisible(context, snapshot, request)
            : IsLayerOnlyTargetVisible(context, snapshot, request.LayerId!.Value);
        return visible
            ? null
            : ProblemDetailsHelpers.CreateAdminProblemDetails(
                context,
                StatusCodes.Status404NotFound,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status404NotFound),
                "Tile operation target was not found.");
    }

    private static bool IsServiceTargetVisible(
        HttpContext context,
        MetadataV2GraphSnapshot snapshot,
        TileOperationStartRequest request)
    {
        var service = snapshot.FindService(request.ServiceId!);
        if (service is null || !TenantScopeHelpers.IsTenantVisible(context, resource: null, service))
        {
            return false;
        }

        var publications = snapshot.PublicationsForService(service.Metadata.Id);
        if (request.LayerId is not { } targetLayer)
        {
            return AllPublicationsVisible(context, snapshot, publications);
        }

        var matching = publications
            .Where(publication => MatchesLayerId(snapshot, publication, targetLayer))
            .ToArray();
        if (matching.Length == 0 || !AllPublicationsVisible(context, snapshot, matching))
        {
            return false;
        }

        // Guard against non-routable service fallback to another tenant's storage layer during publish/archive.
        var published = PMTilesProxySourceResolver.ResolveForPublish(snapshot, request.ServiceId, targetLayer);
        return published is null ||
            TenantScopeHelpers.IsPublicationVisible(context, published.Publication, published.Resource, published.Service);
    }

    private static bool IsLayerOnlyTargetVisible(
        HttpContext context,
        MetadataV2GraphSnapshot snapshot,
        int layerId)
    {
        var candidates = snapshot.Index.PublicationsById.Values
            .Where(publication => MatchesLayerId(snapshot, publication, layerId))
            .ToArray();
        var hasStorageResource = snapshot.Index.ResourcesByStorageLayerId.TryGetValue(layerId, out var storageResource);
        if (candidates.Length == 0)
        {
            return hasStorageResource && TenantScopeHelpers.IsTenantVisible(context, storageResource, service: null);
        }

        if (hasStorageResource && !TenantScopeHelpers.IsTenantVisible(context, storageResource, service: null))
        {
            return false;
        }

        return AllPublicationsVisible(context, snapshot, candidates);
    }

    private static bool MatchesLayerId(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Layer publication,
        int layerId)
        => (publication.LayerIndex ?? snapshot.ResolveStorageLayerId(publication)) == layerId ||
            snapshot.ResolveStorageLayerId(publication) == layerId;

    private static bool AllPublicationsVisible(
        HttpContext context,
        MetadataV2GraphSnapshot snapshot,
        IReadOnlyList<MetadataV2Layer> publications)
    {
        foreach (var publication in publications)
        {
            if (!snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var service) ||
                !TenantScopeHelpers.IsPublicationVisible(context, publication, snapshot.ResolveResource(publication), service))
            {
                return false;
            }
        }

        return true;
    }
}
