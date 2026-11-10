// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Services;

public sealed class RasterClassStatisticsAnalyzer : IRasterClassStatisticsAnalyzer
{
    private readonly IRasterStore _rasterStore;

    public RasterClassStatisticsAnalyzer(IRasterStore rasterStore)
    {
        _rasterStore = rasterStore ?? throw new ArgumentNullException(nameof(rasterStore));
    }

    public async Task<RasterClassStatisticsResult> ComputeAsync(
        RasterClassStatisticsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var signatures = new List<RasterClassSignature>(request.Classes.Count);
        foreach (var aoi in request.Classes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var vectors = await _rasterStore.ReadClippedBandVectorsAsync(
                request.LayerId,
                request.RasterIds,
                request.MergeStrategy,
                aoi.ClipGeometry,
                aoi.ClipSrid,
                request.Bands,
                request.MaxPixelsPerClass,
                cancellationToken).ConfigureAwait(false);

            if (vectors.ExceededPixelBudget)
            {
                throw new RasterClassStatisticsAoiTooLargeException(
                    aoi.ClassId,
                    vectors.BoundingPixelCount,
                    request.MaxPixelsPerClass);
            }

            signatures.Add(RasterClassStatisticsCalculator.Compute(aoi.ClassId, aoi.Name, vectors));
        }

        return new RasterClassStatisticsResult { Signatures = signatures };
    }
}
