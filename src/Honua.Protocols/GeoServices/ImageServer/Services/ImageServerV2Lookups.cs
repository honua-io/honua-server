// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Raster;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

internal static class ImageServerV2Lookups
{
    internal readonly record struct ResolvedImageLayer(
        MetadataV2Layer Publication,
        MetadataV2Object? Resource,
        string DisplayName,
        string? Description);

    private static readonly object RouteBindingItemKey = new();

    private sealed record RouteBinding(
        int StorageLayerId,
        AuthorizationOperation Operation,
        ImageServerLayerResolution Resolution);

    // The first successful publication resolution wins, so the numeric-route pipeline must not rebind a service-scoped route.
    public static void RecordRouteBinding(
        HttpContext context,
        ImageServerLayerResolution resolution,
        AuthorizationOperation operation)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (resolution.ErrorResult is not null || string.IsNullOrWhiteSpace(resolution.PublicationId))
        {
            return;
        }

        context.Items.TryAdd(RouteBindingItemKey, new RouteBinding(resolution.LayerId, operation, resolution));
    }

    public static bool TryGetRouteBinding(
        HttpContext context,
        int storageLayerId,
        AuthorizationOperation operation,
        out ImageServerLayerResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Items.TryGetValue(RouteBindingItemKey, out var value)
            && value is RouteBinding binding
            && binding.StorageLayerId == storageLayerId
            && binding.Operation == operation)
        {
            resolution = binding.Resolution;
            return true;
        }

        resolution = default;
        return false;
    }

    // A publication bound to a different storage layer is never selected by index, so an alias cannot borrow another publication's title or mosaic strategy.
    public static ResolvedImageLayer? FindByStorageLayerId(
        MetadataV2GraphSnapshot snapshot,
        int storageLayerId,
        HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return FindStorageLayer(snapshot, storageLayerId, context);
    }

    public static ResolvedImageLayer? FindPinnedStorageLayer(MetadataV2GraphSnapshot snapshot, int storageLayerId)
        => FindStorageLayer(snapshot, storageLayerId, null);

    private static ResolvedImageLayer? FindStorageLayer(
        MetadataV2GraphSnapshot snapshot,
        int storageLayerId,
        HttpContext? context)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (context?.Items.TryGetValue(RouteBindingItemKey, out var value) == true
            && value is RouteBinding { Resolution.PublicationId: { } routePublicationId } binding
            && binding.StorageLayerId == storageLayerId)
        {
            return snapshot.Index.PublicationsById.TryGetValue(routePublicationId, out var routePublication)
                && IsVisibleForRequest(snapshot, routePublication, context)
                && snapshot.ResolveStorageLayerId(routePublication) == storageLayerId
                    ? Project(routePublication, snapshot.ResolveResource(routePublication))
                    : null;
        }

        var bound = snapshot.PublicationsForStorageLayer(storageLayerId)
            .Where(publication => IsVisibleForRequest(snapshot, publication, context))
            .Select(publication => (
                Publication: publication,
                Service: snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var service) ? service : null))
            .OrderByDescending(static candidate =>
                candidate.Service is not null
                && ServiceProtocols.IsProtocolEnabled(candidate.Service, ServiceProtocols.ImageServer))
            .ThenByDescending(static candidate => candidate.Publication.IsPrimary)
            .ThenBy(static candidate => candidate.Service?.Metadata.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static candidate => candidate.Publication.Metadata.Id, StringComparer.Ordinal)
            .Select(static candidate => candidate.Publication)
            .FirstOrDefault();
        if (bound is not null)
        {
            return Project(bound, snapshot.ResolveResource(bound));
        }

        var unbound = snapshot.Graph.Publications.FirstOrDefault(publication =>
            publication.LayerIndex == storageLayerId
            && IsVisibleForRequest(snapshot, publication, context)
            && snapshot.ResolveStorageLayerId(publication) is null);
        return unbound is null ? null : Project(unbound, snapshot.ResolveResource(unbound));
    }

    public static ResolvedImageLayer? FindByPublicationId(
        MetadataV2GraphSnapshot snapshot,
        string publicationId,
        HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicationId);

        if (!snapshot.Index.PublicationsById.TryGetValue(publicationId, out var publication)
            || !IsVisibleForRequest(snapshot, publication, context))
        {
            return null;
        }

        return Project(publication, snapshot.ResolveResource(publication));
    }

    private static bool IsVisibleForRequest(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Layer publication,
        HttpContext? context)
        => snapshot.IsRoutable(publication)
            && (context is null || TenantScopeHelpers.IsPublicationVisible(
                context, publication, snapshot.ResolveResource(publication),
                snapshot.Index.ServicesById.GetValueOrDefault(publication.ServiceId)));

    private static ResolvedImageLayer Project(MetadataV2Layer publication, MetadataV2Object? resource)
    {
        var displayName = publication.TitleOverride
            ?? publication.Metadata.Title
            ?? (string.IsNullOrEmpty(publication.Metadata.Name) ? null : publication.Metadata.Name)
            ?? resource?.Metadata.Title
            ?? (string.IsNullOrEmpty(resource?.Metadata.Name) ? null : resource?.Metadata.Name)
            ?? string.Empty;

        var description = resource?.Metadata.Description ?? publication.Metadata.Description;

        return new ResolvedImageLayer(publication, resource, displayName, description);
    }

    public static (string? StartTimeField, string? EndTimeField, string? TrackIdField) ReadTimeFieldHints(
        MetadataV2Object? resource)
    {
        var t = resource?.Temporal;
        if (t is null)
        {
            return (null, null, null);
        }
        return (t.StartTimeField, t.EndTimeField, t.TrackIdField);
    }

    public static RasterMergeStrategy ResolveMergeStrategy(MetadataV2Object? resource, string? mosaicRule)
        => RasterMosaicUtilities.ResolveMergeStrategy(resource, mosaicRule);
}
