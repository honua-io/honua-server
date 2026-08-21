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
    private const int AbsoluteMaxPublicationProbes = 4096;

    public int MaxPublicationProbes { get; set; } = DefaultMaxPublicationProbes;

    public int ResolveMaxPublicationProbes()
        => Math.Clamp(MaxPublicationProbes, 1, AbsoluteMaxPublicationProbes);
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
    public async Task<ImageServerPublicationProbeResult?> FindFirstRasterBearingAsync(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service service,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var candidates = snapshot.PublicationsForService(service.Metadata.Id)
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
            .Take(options.Value.ResolveMaxPublicationProbes())
            .ToArray();

        Exception? firstFailure = null;
        var completedProbeCount = 0;
        foreach (var candidate in candidates)
        {
            try
            {
                var rasters = await rasterStore.ListRastersAsync(
                    candidate.StorageLayerId!.Value,
                    cancellationToken).ConfigureAwait(false);
                completedProbeCount++;
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

        // A single unavailable publication does not hide a later healthy publication. When
        // every bounded backend probe failed, however, returning "no raster" would turn an
        // infrastructure outage into a false catalog/404 answer, so surface a server fault.
        if (candidates.Length > 0 && completedProbeCount == 0 && firstFailure is not null)
        {
            throw new InvalidOperationException(
                $"Raster availability could not be determined for ImageServer service '{service.Metadata.Name}'.",
                firstFailure);
        }

        return null;
    }

    private readonly record struct ImageServerPublicationProbeCandidate(
        string PublicationId,
        int? PublicationLayerIndex,
        int? StorageLayerId,
        MetadataV2Resource? Resource);
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
