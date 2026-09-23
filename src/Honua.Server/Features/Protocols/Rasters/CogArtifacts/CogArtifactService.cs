// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.CogParser;
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

internal sealed record CogArtifactSource(
    int LayerId,
    MetadataV2Publication Publication,
    MetadataV2Resource Resource,
    MetadataV2Service Service,
    MetadataV2StorageBinding Binding);

internal sealed record CogArtifactReadTarget(CloudFile File, CogArtifactSource Source);

/// <summary>
/// Exports a layer's primary raster as a Cloud Optimized GeoTIFF into the configured file
/// storage and resolves published artifacts for the source-policy-aware range proxy.
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

    public async Task<CogArtifactSource?> ResolveSourceAsync(int layerId, CancellationToken cancellationToken)
    {
        var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var publication = CogPublicationBinding.Resolve(snapshot, layerId);
        if (publication is null || snapshot.ResolveResource(publication) is not { } resource ||
            !snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var service) ||
            snapshot.ResolveStorageBinding(publication) is not { } binding ||
            !string.Equals(binding.ResourceId, resource.Metadata.Id, StringComparison.Ordinal))
        {
            return null;
        }

        return new CogArtifactSource(layerId, publication, resource, service, binding);
    }

    internal static string BuildObjectKey(int layerId, long rasterId)
        => $"cog/{layerId.ToString(CultureInfo.InvariantCulture)}/{rasterId.ToString(CultureInfo.InvariantCulture)}.tif";

    public async Task<CogArtifactPublishOutcome> PublishAsync(CogArtifactSource source, CancellationToken cancellationToken)
    {
        var layerId = source.LayerId;
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
                .Add("publicationId", source.Publication.Metadata.Id)
                .Add("resourceId", source.Resource.Metadata.Id)
                .Add("serviceId", source.Service.Metadata.Id)
                .Add("bindingId", source.Binding.Metadata.Id)
                .Add("bindingFingerprint", BindingFingerprint(source.Binding))
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
    /// published (media type, operation tag and unchanged source binding), so the proxy
    /// never serves arbitrary files or rebinds an old artifact to a different resource.
    /// </summary>
    public async Task<CogArtifactReadTarget?> ResolvePublishedAsync(string artifactId, CancellationToken cancellationToken)
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

        if (!metadata.Metadata.TryGetValue("layerId", out var layerValue) ||
            !int.TryParse(layerValue, NumberStyles.None, CultureInfo.InvariantCulture, out var layerId))
        {
            return null;
        }

        var source = await ResolveSourceAsync(layerId, cancellationToken).ConfigureAwait(false);
        if (source is null ||
            !metadata.Metadata.TryGetValue("publicationId", out var publicationId) ||
            !string.Equals(publicationId, source.Publication.Metadata.Id, StringComparison.Ordinal) ||
            !metadata.Metadata.TryGetValue("resourceId", out var resourceId) ||
            !string.Equals(resourceId, source.Resource.Metadata.Id, StringComparison.Ordinal) ||
            !metadata.Metadata.TryGetValue("serviceId", out var serviceId) ||
            !string.Equals(serviceId, source.Service.Metadata.Id, StringComparison.Ordinal) ||
            !metadata.Metadata.TryGetValue("bindingId", out var bindingId) ||
            !string.Equals(bindingId, source.Binding.Metadata.Id, StringComparison.Ordinal) ||
            !metadata.Metadata.TryGetValue("bindingFingerprint", out var bindingFingerprint) ||
            !string.Equals(bindingFingerprint, BindingFingerprint(source.Binding), StringComparison.Ordinal))
        {
            return null;
        }

        return new CogArtifactReadTarget(metadata, source);
    }

    private static string BindingFingerprint(MetadataV2StorageBinding binding)
    {
        // Binding IDs can be reused while their source locator changes. Bind a
        // published snapshot to the source-defining fields, not the graph revision
        // (policy-only changes must still be evaluated on every read).
        var text = new StringBuilder();
        static void Append(StringBuilder target, string? value)
        {
            if (value is null)
            {
                target.Append("-1:");
                return;
            }
            target.Append(value.Length).Append(':').Append(value);
        }

        Append(text, binding.ResourceId);
        Append(text, binding.ConnectionId);
        Append(text, binding.StorageType.ToString());
        Append(text, binding.Locator);
        Append(text, binding.StorageLayerId?.ToString(CultureInfo.InvariantCulture));
        foreach (var option in binding.Options.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            Append(text, option.Key);
            Append(text, option.Value.GetRawText());
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
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
