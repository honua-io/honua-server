// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Services;

/// <summary>
/// Default <see cref="IRasterClassStatisticsAnalyzer"/>. For each class it reads the training-AOI
/// pixel vectors through the shared <see cref="IRasterStore"/> and folds them into a signature via
/// <see cref="RasterClassStatisticsCalculator"/>. Classes are analysed one at a time so at most one
/// AOI's pixels are resident at once, keeping memory bounded by the request's per-class budget.
/// </summary>
public sealed class RasterClassStatisticsAnalyzer : IRasterClassStatisticsAnalyzer
{
    private readonly IRasterStore _rasterStore;

    /// <summary>Creates the analyzer over the shared raster store.</summary>
    public RasterClassStatisticsAnalyzer(IRasterStore rasterStore)
    {
        _rasterStore = rasterStore ?? throw new ArgumentNullException(nameof(rasterStore));
    }

    /// <inheritdoc />
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
