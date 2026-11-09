// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Tiles;
using Honua.Protocols.GeoServices.MapServer;
using Microsoft.Extensions.DependencyInjection;
using static Honua.Infrastructure.Rendering.RasterMapRenderingPipeline;

namespace Honua.Protocols.GeoServices.MapServer.Tiles;

internal sealed class MapTileExportProducer(IServiceScopeFactory scopeFactory) : ITileExportPackageProducer
{
    public bool CanProduce(TileExportJobPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.SourceKind == TileExportSourceKind.Map && plan.Source is TileExportMapSourceDescriptor;
    }

    public async Task ProduceAsync(TileExportJobPlan plan, Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(destination);

        var descriptor = (TileExportMapSourceDescriptor)plan.Source;

        // A fresh DI scope owns the scoped metadata graph / feature readers for the whole streamed
        // enumeration, so the singleton executor never captures a request-lifetime dependency.
        await using var scope = scopeFactory.CreateAsyncScope();
        var graphProvider = scope.ServiceProvider.GetRequiredService<IMetadataV2GraphProvider>();

        // Pin the exact revision the submission captured; the source fence has already verified it is
        // still available, so a null snapshot here is a genuine mid-flight loss and fails the job.
        var snapshot = await graphProvider.GetByRevisionAsync(descriptor.MetadataRevision, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Pinned metadata revision {descriptor.MetadataRevision} is no longer available for the tile export.");
        if (!snapshot.Index.ServicesById.TryGetValue(plan.ResourceId, out var service))
        {
            throw new InvalidOperationException(
                $"MapServer service '{plan.ResourceId}' is not present in the pinned metadata revision.");
        }

        // Resolve exactly the pinned layers to Web-Mercator render descriptors, mirroring the
        // synchronous exportTiles handler (HasMapServerGeometry filter + CreateRenderLayerDescriptorFromV2
        // over the storage layer id) so a queued export renders byte-identically to the sync path.
        var renderLayers = MapTileExportLayerResolver.BuildRenderLayers(snapshot, service, descriptor.Layers, out var serviceSrid);
        var maxFeatures = service.Settings?.MaxFeaturesPerLayer ?? MaxFeaturesPerLayer;

        await TileExportPackagePipeline.WriteAsync(
            plan,
            destination,
            RenderTilesAsync(scope.ServiceProvider, plan, serviceSrid, renderLayers, maxFeatures, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private static async IAsyncEnumerable<TilePackageWriter.PackagedTile> RenderTilesAsync(
        IServiceProvider services,
        TileExportJobPlan plan,
        int serviceSrid,
        IReadOnlyList<RenderLayerDescriptor> renderLayers,
        int maxFeatures,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The grid planner yields coordinates in canonical bundle order without materializing the
        // full grid; every tile is rendered and packaged (matching the sync exportTiles handler,
        // which never skips a tile) so the package carries the full requested mosaic.
        foreach (var tile in TileExportGridPlanner.Create(plan).Tiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = await RenderRasterTileForExportAsync(
                services,
                serviceSrid,
                renderLayers,
                tile.Level,
                tile.Row,
                tile.Column,
                maxFeatures,
                cancellationToken).ConfigureAwait(false);
            yield return new TilePackageWriter.PackagedTile(tile.Level, tile.Column, tile.Row, bytes);
        }
    }
}

// The queued map export confirms the pinned revision, service, and layers are still resolvable, so it does not render newer or missing state under the old artifact identity.
internal sealed class MapTileExportSourceFence(IServiceScopeFactory scopeFactory) : ITileExportSourceFence
{
    public TileExportSourceKind SourceKind => TileExportSourceKind.Map;

    public async ValueTask<bool> IsAvailableAsync(TileExportJobPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Source is not TileExportMapSourceDescriptor descriptor ||
            !TileExportMapPublicationPolicy.IsCurrent(descriptor))
        {
            return false;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var graphProvider = scope.ServiceProvider.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await graphProvider.GetByRevisionAsync(descriptor.MetadataRevision, cancellationToken).ConfigureAwait(false);
        return snapshot is not null
            && snapshot.Index.ServicesById.TryGetValue(plan.ResourceId, out var service)
            && MapTileExportLayerResolver.AllPinnedLayersResolve(snapshot, service, descriptor.Layers);
    }
}

internal static class MapTileExportLayerResolver
{
    internal static RenderLayerDescriptor[] BuildRenderLayers(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service service,
        ImmutableArray<TileExportMapLayerSelection> pinnedLayers,
        out int serviceSrid)
    {
        var resolved = new List<(int StorageLayerId, MetadataV2Object Resource)>(pinnedLayers.Length);
        var publicationsByLayerId = ResolvePublicationsByLayerId(snapshot, service);
        foreach (var selection in pinnedLayers)
        {
            var publication = ResolvePublication(publicationsByLayerId, service, selection);
            var resource = snapshot.ResolveResource(publication)
                ?? throw new InvalidOperationException(
                    $"MapServer layer '{selection.LayerId}' has no resolvable resource in the pinned metadata revision.");

            // Non-geometry (table) layers never contribute pixels; the sync exportTiles path filters
            // them out before building render descriptors, so skip them here for byte parity.
            if (!HasMapServerGeometry(resource))
            {
                continue;
            }

            var storageLayerId = snapshot.ResolveStorageLayerId(publication)
                ?? snapshot.ResolveStorageLayerId(resource)
                ?? ParseLayerId(selection.LayerId);
            resolved.Add((storageLayerId, resource));
        }

        serviceSrid = service.SpatialReference?.ResolveSrid()
            ?? resolved.Select(static layer => layer.Resource.ReadSrid()).FirstOrDefault(static srid => srid.HasValue)
            ?? Honua.Core.Features.Shared.Models.SpatialReference.WGS84.Wkid;

        return [.. resolved.Select(static layer =>
            CreateRenderLayerDescriptorFromV2(layer.StorageLayerId, true, layer.Resource.ReadGeometryType()))];
    }

    internal static bool AllPinnedLayersResolve(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service service,
        ImmutableArray<TileExportMapLayerSelection> pinnedLayers)
    {
        var publicationsByLayerId = ResolvePublicationsByLayerId(snapshot, service);
        foreach (var selection in pinnedLayers)
        {
            if (!int.TryParse(selection.LayerId, NumberStyles.None, CultureInfo.InvariantCulture, out var publicLayerId))
            {
                return false;
            }

            if (!publicationsByLayerId.TryGetValue(publicLayerId, out var publication) ||
                snapshot.ResolveResource(publication) is null)
            {
                return false;
            }
        }

        return true;
    }

    private static MetadataV2Layer ResolvePublication(
        Dictionary<int, MetadataV2Layer> publicationsByLayerId,
        MetadataV2Service service,
        TileExportMapLayerSelection selection)
    {
        var publicLayerId = ParseLayerId(selection.LayerId);
        return publicationsByLayerId.TryGetValue(publicLayerId, out var publication)
            ? publication
            : throw new InvalidOperationException(
                $"MapServer layer '{selection.LayerId}' is not published on service '{service.Metadata.Id}' in the pinned metadata revision.");
    }

    private static Dictionary<int, MetadataV2Layer> ResolvePublicationsByLayerId(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service service)
        => MapServerPublicationResolver.ResolveLayerPublications(
                snapshot, service, JobSecurityScope.Current?.Submitter?.TenantId)
            .ToDictionary(static publication => publication.LayerIndex!.Value);

    private static bool HasMapServerGeometry(MetadataV2Object resource)
        => resource.ReadGeometryType() != MetadataV2GeometryType.None
            || resource.FindPrimaryGeometryField() is not null;

    private static int ParseLayerId(string layerId)
        => int.TryParse(layerId, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"MapServer tile-export layer id '{layerId}' is not a valid layer index.");
}
