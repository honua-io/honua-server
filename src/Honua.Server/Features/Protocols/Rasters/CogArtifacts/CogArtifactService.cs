// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Server.Features.Protocols.Rasters.CogArtifacts;

/// <summary>Outcome of a COG artifact publish.</summary>
internal enum CogArtifactPublishStatus
{
    Published,
    LayerNotFound,
    NoRaster,
    ExportFailed,
    UploadFailed,
}

internal sealed record CogArtifactPublishOutcome(
    CogArtifactPublishStatus Status,
    CogArtifactDescriptor? Descriptor,
    string? Error);

/// <summary>
/// Exports a layer's primary raster as a Cloud Optimized GeoTIFF into the configured file
/// storage and resolves published artifacts for the public range proxy.
/// </summary>
/// <remarks>
/// <para>
/// Desktop clients (QGIS through GDAL <c>/vsicurl</c>, ArcGIS Pro, arcpy) read a COG with
/// HTTP HEAD + Range requests against a plain URL. Nothing else in the server hands a client
/// a rasters' bytes with range support: the ImageServer and WCS surfaces render on demand and
/// the cloud-raster catalog consumes COGs server-side. This surface materialises the raster
/// once (PostGIS <c>ST_AsGDALRaster(..., 'COG')</c>, the same export path the raster store
/// already guards) under a deterministic object key and serves it through the same byte-range
/// proxy the PMTiles publish uses.
/// </para>
/// <para>
/// The object key is <c>cog/{layerId}/{rasterId}.tif</c>; re-publishing overwrites it, so one
/// URL stays stable for a fixture or a deployment.
/// </para>
/// </remarks>
internal sealed class CogArtifactService
{
    /// <summary>Media type registered for Cloud Optimized GeoTIFF (OGC 21-026).</summary>
    internal const string ContentType = "image/tiff; application=geotiff; profile=cloud-optimized";
    internal const string OperationMetadataKey = "operation";
    internal const string PublishOperationValue = "publish-cog";
    internal const string ProxyRoutePrefix = "/api/v1/rasters/cog/";

    private readonly IRasterStore _rasterStore;
    private readonly ICloudFileStorage _storage;
    private readonly IMetadataV2GraphProvider _graphProvider;
    private readonly ILogger<CogArtifactService> _logger;

    public CogArtifactService(
        IRasterStore rasterStore,
        ICloudFileStorage storage,
        IMetadataV2GraphProvider graphProvider,
        ILogger<CogArtifactService> logger)
    {
        _rasterStore = rasterStore ?? throw new ArgumentNullException(nameof(rasterStore));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _graphProvider = graphProvider ?? throw new ArgumentNullException(nameof(graphProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private static bool LayerIsRoutable(MetadataV2GraphSnapshot snapshot, int layerId)
    {
        foreach (var publication in snapshot.Graph.Publications)
        {
            if (publication.LayerIndex == layerId && snapshot.IsRoutable(publication))
            {
                return true;
            }
        }

        return false;
    }

    internal static string BuildObjectKey(int layerId, long rasterId)
        => $"cog/{layerId.ToString(CultureInfo.InvariantCulture)}/{rasterId.ToString(CultureInfo.InvariantCulture)}.tif";

    public async Task<CogArtifactPublishOutcome> PublishAsync(int layerId, CancellationToken cancellationToken)
    {
        // A layer is publishable when the graph routes at least one publication to it. The
        // CogPublicationBinding helper is deliberately not used here: it fails closed when a
        // layer index carries more than one routable publication, and every fixture layer
        // that has a raster also has FeatureServer/MapServer/ImageServer publications.
        var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (!LayerIsRoutable(snapshot, layerId))
        {
            return new CogArtifactPublishOutcome(CogArtifactPublishStatus.LayerNotFound, null, "Layer not found.");
        }

        var primary = await _rasterStore.GetPrimaryRasterInfoAsync(layerId, cancellationToken).ConfigureAwait(false);
        if (primary is not { } raster)
        {
            return new CogArtifactPublishOutcome(CogArtifactPublishStatus.NoRaster, null, "The layer has no raster to export.");
        }

        RasterResult export;
        try
        {
            export = await _rasterStore.ExportImageAsync(
                layerId,
                raster.Id,
                new RasterQuery { OutputFormat = RasterFormat.COG },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            CogArtifactLog.ExportFailed(_logger, layerId, raster.Id, ex);
            return new CogArtifactPublishOutcome(CogArtifactPublishStatus.ExportFailed, null, "COG export failed.");
        }

        if (export.Data is not { Length: > 0 })
        {
            CogArtifactLog.ExportFailed(_logger, layerId, raster.Id, null);
            return new CogArtifactPublishOutcome(CogArtifactPublishStatus.ExportFailed, null, "COG export produced no bytes.");
        }

        var objectKey = BuildObjectKey(layerId, raster.Id);
        await using var content = new MemoryStream(export.Data, writable: false);
        var upload = await _storage.UploadAsync(new FileUploadRequest
        {
            Content = content,
            FileName = Path.GetFileName(objectKey),
            ContentType = ContentType,
            SizeBytes = export.Data.LongLength,
            TimeToLive = null,
            ObjectKeyOverride = objectKey,
            Metadata = ImmutableDictionary<string, string>.Empty
                .Add(OperationMetadataKey, PublishOperationValue)
                .Add("layerId", layerId.ToString(CultureInfo.InvariantCulture))
                .Add("rasterId", raster.Id.ToString(CultureInfo.InvariantCulture)),
        }, cancellationToken).ConfigureAwait(false);

        if (!upload.Success || upload.File is null)
        {
            CogArtifactLog.UploadFailed(_logger, layerId, raster.Id, upload.ErrorMessage);
            return new CogArtifactPublishOutcome(CogArtifactPublishStatus.UploadFailed, null, "COG artifact upload failed.");
        }

        var descriptor = new CogArtifactDescriptor
        {
            ArtifactId = upload.File.FileId,
            LayerId = layerId,
            RasterId = raster.Id,
            SizeBytes = export.Data.LongLength,
            ContentType = ContentType,
            Url = ProxyRoutePrefix + upload.File.FileId,
            Width = export.Width,
            Height = export.Height,
            BandCount = raster.BandCount,
            Srid = export.Srid ?? raster.Srid,
            PublishedAt = upload.File.UploadedAt,
        };
        CogArtifactLog.Published(_logger, layerId, raster.Id, descriptor.ArtifactId, descriptor.SizeBytes);
        return new CogArtifactPublishOutcome(CogArtifactPublishStatus.Published, descriptor, null);
    }

    /// <summary>
    /// Resolves an artifact id to its stored metadata, accepting only objects this surface
    /// published (media type and operation tag), so the proxy never serves arbitrary files.
    /// </summary>
    public async Task<CloudFile?> ResolvePublishedAsync(string artifactId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artifactId))
        {
            return null;
        }

        var metadata = await _storage.GetMetadataAsync(artifactId, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            return null;
        }

        if (!string.Equals(metadata.ContentType, ContentType, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!metadata.Metadata.TryGetValue(OperationMetadataKey, out var operation)
            || !string.Equals(operation, PublishOperationValue, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return metadata;
    }
}

internal static partial class CogArtifactLog
{
    [LoggerMessage(EventId = 7910, Level = LogLevel.Information,
        Message = "Published COG artifact {ArtifactId} for layer {LayerId} raster {RasterId} ({SizeBytes} bytes)")]
    public static partial void Published(ILogger logger, int layerId, long rasterId, string artifactId, long sizeBytes);

    [LoggerMessage(EventId = 7911, Level = LogLevel.Warning,
        Message = "COG export failed for layer {LayerId} raster {RasterId}")]
    public static partial void ExportFailed(ILogger logger, int layerId, long rasterId, Exception? exception);

    [LoggerMessage(EventId = 7912, Level = LogLevel.Warning,
        Message = "COG artifact upload failed for layer {LayerId} raster {RasterId}: {Error}")]
    public static partial void UploadFailed(ILogger logger, int layerId, long rasterId, string? error);
}
