// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Protocols.Cog;

/// <summary>
/// Resolves geoprocessing raster-source references (<c>layerId</c>/<c>rasterId</c>) to
/// raster bytes from the registered COG catalog (#2264), reusing the same
/// <see cref="ICogStore"/> registry and per-provider <see cref="ICloudRangeReader"/>
/// readers the COG tile path uses. Registered as a singleton (the GP submit service is
/// a singleton); the scoped <see cref="ICogStore"/> is resolved through a per-call
/// service scope so there is no captive-dependency violation. When no COG store is
/// configured the resolver returns a clear failure rather than throwing.
///
/// <para>
/// Before materializing bytes the resolver runs a capped, header-only
/// <see cref="ICogDecodedSizeInspector"/> probe and fails closed when the raster's projected
/// <em>decoded</em> grid exceeds <see cref="CatalogRasterSourceOptions.MaxDecodedRasterBytes"/>.
/// The pre-existing size gate bounds only the compressed bytes read from the object; without
/// the decoded-size gate a tiny compressed TIFF could declare an enormous decoded grid (a
/// decompression bomb) that only inflates when the worker decodes it (RAST-005 / #3090).
/// </para>
/// </summary>
internal sealed class CatalogRasterSourceResolver(
    IServiceScopeFactory scopeFactory,
    ICogDecodedSizeInspector decodedSizeInspector,
    IOptions<CatalogRasterSourceOptions> options)
    : IGeoprocessingRasterSourceResolver
{
    /// <inheritdoc />
    public async Task<RasterSourceResolution> ResolveAsync(
        RasterSourceReference reference,
        long maxBytes,
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

        long size;
        try
        {
            size = await reader.GetObjectSizeAsync(registration.Bucket, registration.ObjectKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested is false)
        {
            return RasterSourceResolution.Failure("the resolved raster object could not be read.");
        }

        if (size <= 0)
        {
            return RasterSourceResolution.Failure("the resolved raster object is empty.");
        }

        if (size > maxBytes || size > int.MaxValue)
        {
            return RasterSourceResolution.Failure(
                $"the resolved raster size {size.ToString(CultureInfo.InvariantCulture)} bytes exceeds the "
                + $"maximum {maxBytes.ToString(CultureInfo.InvariantCulture)} bytes accepted for inline sourcing.");
        }

        // Bound the projected DECODED grid before materializing any bytes. The compressed-size
        // gate above cannot see a decompression bomb: a small compressed TIFF may declare an
        // enormous decoded raster that only inflates when the worker decodes it (#3090). The
        // probe reads only the header/first IFD within fixed caps and fails closed.
        var inspection = await decodedSizeInspector
            .InspectAsync(reader, registration.Bucket, registration.ObjectKey, options.Value.MaxDecodedRasterBytes, cancellationToken)
            .ConfigureAwait(false);
        if (!inspection.Accepted)
        {
            return RasterSourceResolution.Failure(
                inspection.RejectionReason ?? "the resolved raster's decoded size could not be validated.");
        }

        byte[] bytes;
        try
        {
            bytes = await reader.ReadRangeAsync(registration.Bucket, registration.ObjectKey, 0, (int)size, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested is false)
        {
            return RasterSourceResolution.Failure("the resolved raster object could not be read.");
        }

        return bytes.Length == 0
            ? RasterSourceResolution.Failure("the resolved raster object is empty.")
            : RasterSourceResolution.Success(bytes);
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
}
