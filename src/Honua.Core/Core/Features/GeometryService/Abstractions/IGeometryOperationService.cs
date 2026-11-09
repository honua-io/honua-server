// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Crs;

namespace Honua.Core.Features.GeometryService.Abstractions;

public interface IGeometryOperationService
{
    Task<byte[]> BufferAsync(byte[] wkb, int srid, double distance, bool geodesic, CancellationToken ct = default);

    Task<byte[]> SimplifyAsync(byte[] wkb, double tolerance, bool preserveTopology, CancellationToken ct = default);

    Task<byte[]> ProjectAsync(byte[] wkb, int fromSrid, int toSrid, CancellationToken ct = default);

    Task<byte[]> ProjectAsync(
        byte[] wkb,
        int fromSrid,
        int toSrid,
        DatumTransformationSelection? selection,
        CancellationToken ct = default)
        => ProjectAsync(wkb, fromSrid, toSrid, ct);

    Task<byte[]> MakeValidAsync(byte[] wkb, int srid, CancellationToken ct = default);

    Task<byte[]> UnionAsync(byte[][] wkbs, int srid, CancellationToken ct = default);

    Task<byte[]> IntersectAsync(byte[] targetWkb, byte[] intersectorWkb, int srid, CancellationToken ct = default);

    Task<byte[]> ClipAsync(byte[] targetWkb, byte[] clipEnvelopeWkb, int srid, CancellationToken ct = default);

    Task<byte[]> DifferenceAsync(byte[] targetWkb, byte[] eraserWkb, int srid, CancellationToken ct = default);

    Task<double> AreaAsync(byte[] wkb, int srid, CancellationToken ct = default);

    Task<double> LengthAsync(byte[] wkb, int srid, CancellationToken ct = default);

    Task<double> DistanceAsync(byte[] wkbA, byte[] wkbB, int srid, CancellationToken ct = default)
        => throw new NotSupportedException();

    Task<byte[]> SegmentizeGeodesicAsync(byte[] wkb, int srid, double maxSegmentLengthMeters, CancellationToken ct = default)
        => throw new NotSupportedException();
}
