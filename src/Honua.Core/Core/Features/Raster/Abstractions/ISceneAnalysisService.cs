// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Abstractions;

public interface ISceneAnalysisService
{
    Task<SunShadowResult> ComputeSunShadowAsync(
        int layerId,
        ShadowObserver observer,
        SunShadowOptions options,
        RasterMergeStrategy mergeStrategy,
        CancellationToken cancellationToken = default);

    Task<SliceResult> ComputeSliceAsync(
        int layerId,
        SlicePlane plane,
        int? sampleCount,
        RasterMergeStrategy mergeStrategy,
        CancellationToken cancellationToken = default);
}
