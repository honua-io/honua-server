// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Domain;
using Honua.Infrastructure.Raster;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

/// <summary>
/// Metadata v2 lookup helpers scoped to the GeoServices ImageServer handler family.
/// These collapse the "layerId integer → publication → resource" walk that every handler
/// performs into a single call and project the small subset of v1 layer fields the
/// handlers still consume (display name, description, mosaic merge strategy, time info)
/// directly from the V2 graph without any v1 adapter or shim.
/// </summary>
internal static class ImageServerV2Lookups
{
    /// <summary>
    /// Resolved view of an ImageServer publication / resource pair, exposing the small
    /// surface the handlers need without v1 types.
    /// </summary>
    internal readonly record struct ResolvedImageLayer(
        MetadataV2Publication Publication,
        MetadataV2Resource? Resource,
        string DisplayName,
        string? Description);

    private static readonly object RouteBindingItemKey = new();

    private sealed record RouteBinding(
        int StorageLayerId,
        AuthorizationOperation Operation,
        ImageServerLayerResolution Resolution);

    /// <summary>
    /// Records the publication a route resolved and authorized for this request, so handlers
    /// that only receive the storage layer id read metadata from that exact publication. The
    /// first successful resolution wins: a service-scoped route resolves its own publication
    /// before delegating to the numeric-route pipeline, which must not rebind it.
    /// </summary>
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

    /// <summary>
    /// Returns the resolution recorded for this request when it bound the same storage layer
    /// under the same authorization operation.
    /// </summary>
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

    /// <summary>
    /// Finds the publication and resource backing a storage layer id — the value every
    /// ImageServer route resolver hands to the handlers and the raster store consumes.
    /// </summary>
    /// <remarks>
    /// Resolution order (#4065):
    /// <list type="number">
    /// <item>the publication the route resolved and authorized for this request, when it is
    /// still routable and bound to <paramref name="storageLayerId"/>;</item>
    /// <item>a routable publication bound to <paramref name="storageLayerId"/>, with the
    /// same preference as the numeric-route resolver (ImageServer-enabled service, primary
    /// publication, service name);</item>
    /// <item>for graphs whose publications carry no storage binding, the publication whose
    /// <see cref="MetadataV2Publication.LayerIndex"/> doubles as the storage handle.</item>
    /// </list>
    /// A publication bound to a different storage layer is never selected by index, so an
    /// aliased publication cannot borrow another publication's title, description or merge
    /// strategy.
    /// </remarks>
    public static ResolvedImageLayer? FindByStorageLayerId(
        MetadataV2GraphSnapshot snapshot,
        int storageLayerId,
        HttpContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (context?.Items.TryGetValue(RouteBindingItemKey, out var value) == true
            && value is RouteBinding { Resolution.PublicationId: { } routePublicationId } binding
            && binding.StorageLayerId == storageLayerId
            && snapshot.Index.PublicationsById.TryGetValue(routePublicationId, out var routePublication)
            && snapshot.IsRoutable(routePublication)
            && snapshot.ResolveStorageLayerId(routePublication) == storageLayerId)
        {
            return Project(routePublication, snapshot.ResolveResource(routePublication));
        }

        var bound = snapshot.PublicationsForStorageLayer(storageLayerId)
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
            && snapshot.IsRoutable(publication)
            && snapshot.ResolveStorageLayerId(publication) is null);
        return unbound is null ? null : Project(unbound, snapshot.ResolveResource(unbound));
    }

    /// <summary>
    /// Finds one exact publication by its canonical metadata identifier.
    /// </summary>
    public static ResolvedImageLayer? FindByPublicationId(
        MetadataV2GraphSnapshot snapshot,
        string publicationId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicationId);

        if (!snapshot.Index.PublicationsById.TryGetValue(publicationId, out var publication))
        {
            return null;
        }

        return Project(publication, snapshot.ResolveResource(publication));
    }

    private static ResolvedImageLayer Project(MetadataV2Publication publication, MetadataV2Resource? resource)
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

    /// <summary>
    /// Returns the temporal field hints declared by the V2 resource's
    /// <c>temporal</c> document, if present. The shape mirrors the legacy
    /// temporal projection so handlers can keep emitting the
    /// ArcGIS-conformant <c>timeInfo</c> block while reading directly from V2.
    /// </summary>
    public static (string? StartTimeField, string? EndTimeField, string? TrackIdField) ReadTimeFieldHints(
        MetadataV2Resource? resource)
    {
        var t = resource?.Temporal;
        if (t is null)
        {
            return (null, null, null);
        }
        return (t.StartTimeField, t.EndTimeField, t.TrackIdField);
    }

    /// <summary>
    /// Resolves the mosaic merge strategy directly from the V2 resource and request
    /// override. The request override wins; otherwise the resource's
    /// <c>extensions["rasterMosaic"].mergeStrategy</c> value is used if present.
    /// </summary>
    public static RasterMergeStrategy ResolveMergeStrategy(MetadataV2Resource? resource, string? mosaicRule)
        => RasterMosaicUtilities.ResolveMergeStrategy(resource, mosaicRule);
}
