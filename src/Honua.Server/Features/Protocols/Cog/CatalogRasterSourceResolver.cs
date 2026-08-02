// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Protocols.Cog;

/// <summary>
/// Resolves geoprocessing raster-source references (<c>layerId</c>/<c>rasterId</c>) to
/// immutable metadata-only descriptors from the registered COG catalog (#2264/#3090), reusing the same
/// <see cref="ICogStore"/> registry and per-provider <see cref="ICloudRangeReader"/>
/// readers the COG tile path uses. Registered as a singleton (the GP submit service is
/// a singleton); the scoped <see cref="ICogStore"/> is resolved through a per-call
/// service scope so there is no captive-dependency violation. When no COG store is
/// configured the resolver returns a clear failure rather than throwing.
/// </summary>
internal sealed class CatalogRasterSourceResolver(IServiceScopeFactory scopeFactory)
    : IGeoprocessingRasterSourceResolver
{
    /// <inheritdoc />
    public async Task<RasterSourceLayerResolution> ResolveLayerIdAsync(
        RasterSourceReference reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);

        await using var scope = scopeFactory.CreateAsyncScope();
        var cogStore = scope.ServiceProvider.GetService<ICogStore>();
        if (cogStore is null)
        {
            return RasterSourceLayerResolution.NotFound();
        }

        var registration = await ResolveRegistrationAsync(cogStore, reference, cancellationToken)
            .ConfigureAwait(false);
        return registration is null
            ? RasterSourceLayerResolution.NotFound()
            : RasterSourceLayerResolution.Success(registration.LayerId);
    }

    /// <inheritdoc />
    public async Task<RasterSourceResolution> ResolveAsync(
        RasterSourceReference reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);

        await using var scope = scopeFactory.CreateAsyncScope();
        var cogStore = scope.ServiceProvider.GetService<ICogStore>();
        if (cogStore is null)
        {
            return RasterSourceResolution.Failure(
                "no raster catalog is configured in this deployment.");
        }

        var registration = await ResolveRegistrationAsync(cogStore, reference, cancellationToken)
            .ConfigureAwait(false);
        if (registration is null)
        {
            return RasterSourceResolution.Failure(DescribeMissing(reference));
        }

        var reader = scope.ServiceProvider
            .GetServices<ICloudRangeReader>()
            .FirstOrDefault(r => r.Provider == registration.Provider);
        if (reader is null)
        {
            return RasterSourceResolution.Failure(
                $"no reader is configured for the resolved raster's storage provider "
                + $"({registration.Provider}).");
        }

        CloudObjectMetadata metadata;
        try
        {
            metadata = await reader.GetObjectMetadataAsync(
                    registration.Bucket,
                    registration.ObjectKey,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested is false)
        {
            return RasterSourceResolution.Failure("the resolved raster object metadata could not be read.");
        }

        if (metadata.SizeBytes <= 0)
        {
            return RasterSourceResolution.Failure("the resolved raster object is empty.");
        }

        if (string.IsNullOrWhiteSpace(metadata.ETag))
        {
            return RasterSourceResolution.Failure(
                "the resolved raster object has no ETag and cannot be opened with a conditional worker read.");
        }

        var immutableVersion = FirstNonEmpty(metadata.Version, metadata.ETag)!;

        var mediaType = string.IsNullOrWhiteSpace(metadata.MediaType)
            ? "image/tiff"
            : metadata.MediaType.Split(';', 2, StringSplitOptions.TrimEntries)[0];
        var checksum = string.IsNullOrWhiteSpace(metadata.ChecksumAlgorithm)
            || string.IsNullOrWhiteSpace(metadata.ChecksumValue)
                ? null
                : new RasterChecksum(metadata.ChecksumAlgorithm, metadata.ChecksumValue);

        return RasterSourceResolution.Success(new ObjectStoreCogRasterSourceDescriptor
        {
            Provider = registration.Provider,
            StoreReference = registration.Bucket,
            ObjectKey = registration.ObjectKey,
            CatalogLayerId = registration.LayerId,
            CatalogRasterId = registration.Id,
            Version = immutableVersion,
            Content = new RasterContentIdentity
            {
                SizeBytes = metadata.SizeBytes,
                MediaType = mediaType,
                ETag = metadata.ETag,
                Checksum = checksum,
            },
            SecurityContext = new RasterSecurityContextReference
            {
                TenantId = "execution",
                AuthorizationSnapshotReference = $"catalog-registration:{registration.Id.ToString(CultureInfo.InvariantCulture)}",
            },
        });
    }

    private static async Task<CogRegistration?> ResolveRegistrationAsync(
        ICogStore cogStore,
        RasterSourceReference reference,
        CancellationToken cancellationToken)
    {
        if (reference.RasterId is { } rasterId)
        {
            var registration = await cogStore.GetAsync(rasterId, cancellationToken).ConfigureAwait(false);
            if (registration is null)
            {
                return null;
            }

            // When a layerId is also supplied it is a consistency hint: reject a
            // rasterId that belongs to a different layer rather than silently sourcing
            // an unrelated raster.
            if (reference.LayerId is { } layerHint && registration.LayerId != layerHint)
            {
                return null;
            }

            return registration;
        }

        if (reference.LayerId is { } layerId)
        {
            var registrations = await cogStore.ListByLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
            // ListByLayerAsync orders newest-first; take the most recent registration.
            return registrations.Length > 0 ? registrations[0] : null;
        }

        return null;
    }

    private static string DescribeMissing(RasterSourceReference reference)
    {
        if (reference.RasterId is { } rasterId)
        {
            return reference.LayerId is { } layerId
                ? $"no registered raster with rasterId={rasterId.ToString(CultureInfo.InvariantCulture)} on layerId={layerId.ToString(CultureInfo.InvariantCulture)}."
                : $"no registered raster with rasterId={rasterId.ToString(CultureInfo.InvariantCulture)}.";
        }

        return reference.LayerId is { } onlyLayer
            ? $"no registered raster for layerId={onlyLayer.ToString(CultureInfo.InvariantCulture)}."
            : "either a layerId or a rasterId is required to resolve a raster source.";
    }

    private static string? FirstNonEmpty(string? primary, string? fallback)
        => !string.IsNullOrWhiteSpace(primary)
            ? primary
            : !string.IsNullOrWhiteSpace(fallback) ? fallback : null;
}
