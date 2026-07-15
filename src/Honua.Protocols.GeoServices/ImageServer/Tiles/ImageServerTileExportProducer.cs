// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Runtime.CompilerServices;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Infrastructure.Tiles;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Protocols.GeoServices.ImageServer.Tiles;

/// <summary>
/// Concrete raster tile-source producer for the durable tile-export runtime. Renders ImageServer
/// tiles from the pinned plan descriptor and revision through the canonical streaming grid planner
/// and package pipeline, so a queued worker reproduces the exact mosaic/time/raster selection the
/// submission captured. HTTP-independent: it resolves the scoped raster store and metadata graph
/// from a per-job scope (the shared executor is a singleton), never from request-scoped state.
/// </summary>
internal sealed class ImageServerTileExportProducer(IServiceScopeFactory scopeFactory) : ITileExportPackageProducer
{
    public bool CanProduce(TileExportJobPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.SourceKind == TileExportSourceKind.Raster && plan.Source is TileExportRasterSourceDescriptor;
    }

    public async Task ProduceAsync(TileExportJobPlan plan, Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(destination);

        var descriptor = (TileExportRasterSourceDescriptor)plan.Source;
        var layerId = ParseLayerId(descriptor.LayerId);

        // A fresh DI scope owns the scoped raster store / metadata graph for the whole streamed
        // enumeration, so the singleton executor never captures a request-lifetime dependency.
        await using var scope = scopeFactory.CreateAsyncScope();
        var rasterStore = scope.ServiceProvider.GetRequiredService<IRasterStore>();
        var graphProvider = scope.ServiceProvider.GetRequiredService<IMetadataV2GraphProvider>();

        // Pin the exact revision the submission captured; the source fence has already verified it is
        // still available, so a null snapshot here is a genuine mid-flight loss and fails the job.
        var snapshot = await graphProvider.GetByRevisionAsync(descriptor.MetadataRevision, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Pinned metadata revision {descriptor.MetadataRevision} is no longer available for the tile export.");
        var resolved = ImageServerV2Lookups.FindByLayerIndex(snapshot, layerId)
            ?? throw new InvalidOperationException(
                $"ImageServer layer {layerId} is not present in the pinned metadata revision.");

        var mergeStrategy = ImageServerV2Lookups.ResolveMergeStrategy(resolved.Resource, descriptor.MosaicRule);
        var rasterFormat = ResolveRasterFormat(plan.TileImageFormat);
        var timestamp = ResolveTimestamp(descriptor.TimeSelection);

        await TileExportPackagePipeline.WriteAsync(
            plan,
            destination,
            RenderTilesAsync(rasterStore, plan, layerId, mergeStrategy, rasterFormat, timestamp, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private static async IAsyncEnumerable<TilePackageWriter.PackagedTile> RenderTilesAsync(
        IRasterStore rasterStore,
        TileExportJobPlan plan,
        int layerId,
        RasterMergeStrategy mergeStrategy,
        RasterFormat rasterFormat,
        DateTimeOffset? timestamp,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The grid planner yields coordinates in canonical bundle order without materializing the
        // full grid; empty tiles are skipped so the package carries only populated cells.
        foreach (var tile in TileExportGridPlanner.Create(plan).Tiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var envelope = CreateTileEnvelope(tile.Level, tile.Row, tile.Column);
            var selected = await rasterStore.QueryRastersAsync(
                layerId,
                new RasterSelectionQuery { Geometry = envelope, GeometrySrid = 3857, Timestamp = timestamp },
                cancellationToken).ConfigureAwait(false);
            if (selected.Length == 0)
            {
                continue;
            }

            var rendered = selected.Length == 1
                ? await rasterStore.GetImageTileAsync(
                    layerId, selected[0].Id, tile.Level, tile.Row, tile.Column, rasterFormat, cancellationToken).ConfigureAwait(false)
                : await rasterStore.GetMosaicImageTileAsync(
                    layerId,
                    Array.ConvertAll(selected, static raster => raster.Id),
                    mergeStrategy,
                    tile.Level,
                    tile.Row,
                    tile.Column,
                    rasterFormat,
                    cancellationToken).ConfigureAwait(false);
            if (rendered is null || rendered.Value.Data.Length == 0)
            {
                continue;
            }

            yield return new TilePackageWriter.PackagedTile(tile.Level, tile.Column, tile.Row, rendered.Value.Data);
        }
    }

    private static int ParseLayerId(string layerId)
        => int.TryParse(layerId, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"ImageServer tile-export layer id '{layerId}' is not a valid layer index.");

    private static RasterFormat ResolveRasterFormat(string imageFormat) => imageFormat switch
    {
        "JPEG" => RasterFormat.JPEG,
        _ => RasterFormat.PNG
    };

    private static DateTimeOffset? ResolveTimestamp(string? timeSelection)
        => ImageServerMosaicHelpers.TryParseTime(timeSelection, out var timestamp, out _) ? timestamp : null;

    // Web Mercator (EPSG:3857) tile envelope in WKB, matching the synchronous exportTiles handler.
    private static byte[] CreateTileEnvelope(int level, int row, int col)
    {
        const double worldExtent = 20037508.342789244;
        var tileSpan = (worldExtent * 2d) / (1 << level);
        var minX = -worldExtent + (col * tileSpan);
        var maxX = minX + tileSpan;
        var maxY = worldExtent - (row * tileSpan);
        var minY = maxY - tileSpan;
        return ImageServerMosaicHelpers.CreateEnvelopeGeometry(minX, minY, maxX, maxY);
    }
}

/// <summary>
/// Source fence for raster tile exports: confirms the pinned metadata revision and layer are still
/// resolvable before a queued job renders, so an export never silently renders newer or missing
/// state under an old artifact identity.
/// </summary>
internal sealed class ImageServerTileExportSourceFence(IServiceScopeFactory scopeFactory) : ITileExportSourceFence
{
    public TileExportSourceKind SourceKind => TileExportSourceKind.Raster;

    public async ValueTask<bool> IsAvailableAsync(TileExportJobPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Source is not TileExportRasterSourceDescriptor descriptor
            || !int.TryParse(descriptor.LayerId, NumberStyles.None, CultureInfo.InvariantCulture, out var layerId))
        {
            return false;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var graphProvider = scope.ServiceProvider.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await graphProvider.GetByRevisionAsync(descriptor.MetadataRevision, cancellationToken).ConfigureAwait(false);
        return snapshot is not null && ImageServerV2Lookups.FindByLayerIndex(snapshot, layerId) is not null;
    }
}
