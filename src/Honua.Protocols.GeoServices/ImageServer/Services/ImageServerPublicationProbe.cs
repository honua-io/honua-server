// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Infrastructure.Authentication;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

internal sealed class ImageServerPublicationProbeOptions
{
    public const string SectionName = "GeoServices:ImageServer:Discovery";
    public const int DefaultMaxPublicationProbes = 256;
    public const int DefaultMaxRequestPublicationProbes = 512;
    private const int AbsoluteMaxPublicationProbes = 4096;

    public int MaxPublicationProbes { get; set; } = DefaultMaxPublicationProbes;

    public int MaxRequestPublicationProbes { get; set; } = DefaultMaxRequestPublicationProbes;

    public int ResolveMaxPublicationProbes()
        => Math.Clamp(MaxPublicationProbes, 1, AbsoluteMaxPublicationProbes);

    public int ResolveMaxRequestPublicationProbes()
        => Math.Clamp(MaxRequestPublicationProbes, 1, AbsoluteMaxPublicationProbes);
}

internal interface IImageServerPublicationProbe
{
    Task<ImageServerPublicationProbeResult?> FindFirstRasterBearingAsync(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service service,
        HttpContext context,
        CancellationToken cancellationToken);
}

internal readonly record struct ImageServerPublicationProbeResult(
    string PublicationId,
    int? PublicationLayerIndex,
    int StorageLayerId);

/// <summary>
/// Applies one bounded, ordered availability decision to ImageServer discovery and serving.
/// </summary>
internal sealed class ImageServerPublicationProbe(
    IRasterStore rasterStore,
    IOptions<ImageServerPublicationProbeOptions> options,
    ILogger<ImageServerPublicationProbe> logger) : IImageServerPublicationProbe
{
    private int _requestProbeCount;

    public async Task<ImageServerPublicationProbeResult?> FindFirstRasterBearingAsync(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service service,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var maxPublicationProbes = options.Value.ResolveMaxPublicationProbes();
        var boundedCandidates = snapshot.PublicationsForService(service.Metadata.Id)
            .Where(snapshot.IsRoutable)
            .Select(publication => new ImageServerPublicationProbeCandidate(
                publication.Metadata.Id,
                publication.LayerIndex,
                snapshot.ResolveStorageLayerId(publication),
                snapshot.ResolveResource(publication) as MetadataV2Resource))
            .Where(static candidate =>
                candidate.StorageLayerId.HasValue
                && candidate.Resource is not null)
            .Where(candidate => AccessPolicyHelpers.IsResourceAccessible(
                context,
                candidate.Resource!,
                service))
            .OrderBy(static candidate => candidate.PublicationLayerIndex ?? int.MaxValue)
            .ThenBy(static candidate => candidate.PublicationId, StringComparer.Ordinal)
            .Take(maxPublicationProbes + 1)
            .ToArray();
        var candidates = boundedCandidates.Take(maxPublicationProbes).ToArray();
        var wasTruncated = boundedCandidates.Length > maxPublicationProbes;

        Exception? firstFailure = null;
        foreach (var candidate in candidates)
        {
            try
            {
                if (!TryReserveRequestProbe(options.Value.ResolveMaxRequestPublicationProbes()))
                {
                    throw new ImageServerPublicationProbeIndeterminateException(
                        "The request-wide raster availability probe budget was exhausted.");
                }

                var rasters = await rasterStore.ListRastersAsync(
                    candidate.StorageLayerId!.Value,
                    cancellationToken).ConfigureAwait(false);
                if (rasters.Length > 0)
                {
                    return new ImageServerPublicationProbeResult(
                        candidate.PublicationId,
                        candidate.PublicationLayerIndex,
                        candidate.StorageLayerId!.Value);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ImageServerPublicationProbeIndeterminateException)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                firstFailure ??= exception;
                ImageServerPublicationProbeLogging.ProbeFailed(
                    logger,
                    service.Metadata.Name,
                    candidate.PublicationId,
                    exception);
            }
        }

        // A failed candidate does not hide a later proven raster. If no raster was found,
        // however, any backend failure or an unexamined max+1 candidate makes the answer
        // indeterminate. Never turn either condition into a false catalog omission/404.
        if (firstFailure is not null)
        {
            throw new ImageServerPublicationProbeIndeterminateException(
                $"Raster availability could not be determined for ImageServer service '{service.Metadata.Name}'.",
                firstFailure);
        }

        if (wasTruncated)
        {
            throw new ImageServerPublicationProbeIndeterminateException(
                $"Raster availability for ImageServer service '{service.Metadata.Name}' exceeded the configured per-service probe bound.");
        }

        return null;
    }

    private bool TryReserveRequestProbe(int maxRequestProbes)
    {
        while (true)
        {
            var current = Volatile.Read(ref _requestProbeCount);
            if (current >= maxRequestProbes)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _requestProbeCount, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    private readonly record struct ImageServerPublicationProbeCandidate(
        string PublicationId,
        int? PublicationLayerIndex,
        int? StorageLayerId,
        MetadataV2Resource? Resource);
}

internal sealed class ImageServerPublicationProbeIndeterminateException : InvalidOperationException
{
    public ImageServerPublicationProbeIndeterminateException(string message)
        : base(message)
    {
    }

    public ImageServerPublicationProbeIndeterminateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal static partial class ImageServerPublicationProbeLogging
{
    [LoggerMessage(
        EventId = 9751,
        Level = LogLevel.Warning,
        Message = "Raster availability probe failed for ImageServer service {ServiceName}, publication {PublicationId}.")]
    public static partial void ProbeFailed(
        ILogger logger,
        string serviceName,
        string publicationId,
        Exception exception);
}
