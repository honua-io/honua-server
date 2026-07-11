// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.ZarrParser;

internal readonly record struct ZarrServableRegistrationResolution(
    ZarrRegistration? Registration,
    ICloudRangeReader? RangeReader,
    bool HasScannedRegistration)
{
    public bool IsSuccess => Registration?.Metadata is not null && RangeReader is not null;
}

internal static class ZarrServableRegistrationResolver
{
    public static async Task<ZarrServableRegistrationResolution> ResolveAsync(
        IZarrStore store,
        IReadOnlyList<ICloudRangeReader> rangeReaders,
        int layerId,
        CancellationToken cancellationToken)
    {
        var registrations = await store.ListByLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
        var hasScannedRegistration = false;
        foreach (var candidate in registrations)
        {
            if (candidate.Metadata is null)
            {
                continue;
            }

            hasScannedRegistration = true;
            var reader = rangeReaders.FirstOrDefault(rangeReader => rangeReader.Provider == candidate.Provider);
            if (reader is not null)
            {
                return new ZarrServableRegistrationResolution(candidate, reader, true);
            }
        }

        return new ZarrServableRegistrationResolution(null, null, hasScannedRegistration);
    }
}
