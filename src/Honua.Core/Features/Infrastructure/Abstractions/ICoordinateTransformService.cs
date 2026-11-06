// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Crs;

namespace Honua.Core.Features.Infrastructure.Abstractions;

public interface ICoordinateTransformService
{
    ValueTask<(double MinX, double MinY, double MaxX, double MaxY)?> TransformExtentAsync(
        double minX, double minY, double maxX, double maxY,
        int fromSrid, int toSrid,
        CancellationToken cancellationToken = default);

    // A PROJ pipeline uses three-argument ST_Transform so the result matches the selected datum pipeline; the default method ignores the pipeline.
    ValueTask<(double MinX, double MinY, double MaxX, double MaxY)?> TransformExtentAsync(
        double minX, double minY, double maxX, double maxY,
        int fromSrid, int toSrid,
        DatumTransformationSelection? selection,
        CancellationToken cancellationToken = default)
        => TransformExtentAsync(minX, minY, maxX, maxY, fromSrid, toSrid, cancellationToken);

    ValueTask<(double X, double Y)?> TransformPointAsync(
        double x, double y,
        int fromSrid, int toSrid,
        CancellationToken cancellationToken = default);

    // A PROJ pipeline uses three-argument ST_Transform so the result matches the selected datum pipeline; the default method ignores the pipeline.
    ValueTask<(double X, double Y)?> TransformPointAsync(
        double x, double y,
        int fromSrid, int toSrid,
        DatumTransformationSelection? selection,
        CancellationToken cancellationToken = default)
        => TransformPointAsync(x, y, fromSrid, toSrid, cancellationToken);

    // On failure the coordinate arrays are unspecified, and the default method transforms one point at a time.
    async ValueTask<bool> TransformPointsAsync(
        double[] xs,
        double[] ys,
        int fromSrid,
        int toSrid,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(xs);
        ArgumentNullException.ThrowIfNull(ys);
        if (xs.Length != ys.Length)
        {
            throw new ArgumentException("Coordinate arrays must have the same length.", nameof(ys));
        }

        for (var index = 0; index < xs.Length; index++)
        {
            var transformed = await TransformPointAsync(xs[index], ys[index], fromSrid, toSrid, cancellationToken)
                .ConfigureAwait(false);
            if (!transformed.HasValue)
            {
                return false;
            }

            xs[index] = transformed.Value.X;
            ys[index] = transformed.Value.Y;
        }

        return true;
    }
}
