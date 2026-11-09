// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Abstractions;

public interface IVisibilityAnalysisService
{
    Task<LineOfSightResult> ComputeLineOfSightAsync(
        int layerId,
        VisibilityPoint observer,
        VisibilityPoint target,
        int? sampleCount,
        RasterMergeStrategy mergeStrategy,
        CancellationToken cancellationToken = default);

    Task<ViewshedResult> ComputeViewshedAsync(
        int layerId,
        VisibilityPoint observer,
        ViewshedOptions options,
        RasterMergeStrategy mergeStrategy,
        CancellationToken cancellationToken = default);
}
