// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Abstractions;

public interface ISurfaceAnalysisService
{
    Task<SurfaceAnalysisResult> ComputeSlopeAsync(
        SurfaceAnalysisRequest request,
        SlopeUnits units,
        double zFactor,
        CancellationToken cancellationToken = default);

    Task<SurfaceAnalysisResult> ComputeAspectAsync(
        SurfaceAnalysisRequest request,
        CancellationToken cancellationToken = default);

    Task<SurfaceAnalysisResult> ComputeHillshadeAsync(
        SurfaceAnalysisRequest request,
        double azimuthDegrees,
        double altitudeDegrees,
        double zFactor,
        CancellationToken cancellationToken = default);

    // The window radius must be 1, because the PostGIS focal functions only support a 3 by 3 neighborhood.
    Task<SurfaceAnalysisResult> ComputeRugosityAsync(
        SurfaceAnalysisRequest request,
        RugosityMethod method,
        int windowRadius,
        CancellationToken cancellationToken = default);
}
