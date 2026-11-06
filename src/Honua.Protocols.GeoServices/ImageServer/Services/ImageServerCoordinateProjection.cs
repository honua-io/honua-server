// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.Infrastructure.Services;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

internal sealed class ImageServerCoordinateProjection
{
    private readonly SpatialReferenceResolver _spatialReferenceResolver;
    private readonly IDatumTransformationCatalog _datumTransformationCatalog;
    private readonly ICoordinateTransformService? _transformService;

    public ImageServerCoordinateProjection(
        SpatialReferenceResolver spatialReferenceResolver,
        IDatumTransformationCatalog datumTransformationCatalog,
        ICoordinateTransformService? transformService = null)
    {
        _spatialReferenceResolver = spatialReferenceResolver ?? throw new ArgumentNullException(nameof(spatialReferenceResolver));
        _datumTransformationCatalog = datumTransformationCatalog ?? throw new ArgumentNullException(nameof(datumTransformationCatalog));
        _transformService = transformService;
    }

    public bool HasTransformService => _transformService is not null;

    public Task<int?> ResolveSridAsync(string? srValue, CancellationToken cancellationToken)
        => _spatialReferenceResolver.ResolveSridAsync(srValue, null, cancellationToken);

    public bool TryResolveDatumTransformation(
        string? datumTransformationValue,
        int fromSrid,
        int toSrid,
        out DatumTransformationSelection? selection,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? errorMessage)
        => GeoServicesDatumTransformationResolver.TryResolve(
            _datumTransformationCatalog,
            datumTransformationValue,
            fromSrid,
            toSrid,
            out selection,
            out errorMessage);

    public ValueTask<(double MinX, double MinY, double MaxX, double MaxY)?> TransformExtentAsync(
        double minX,
        double minY,
        double maxX,
        double maxY,
        int fromSrid,
        int toSrid,
        DatumTransformationSelection? selection,
        CancellationToken cancellationToken)
        => _transformService!.TransformExtentAsync(minX, minY, maxX, maxY, fromSrid, toSrid, selection, cancellationToken);

    public ValueTask<(double MinX, double MinY, double MaxX, double MaxY)?> TransformExtentAsync(
        double minX,
        double minY,
        double maxX,
        double maxY,
        int fromSrid,
        int toSrid,
        CancellationToken cancellationToken)
        => _transformService!.TransformExtentAsync(minX, minY, maxX, maxY, fromSrid, toSrid, cancellationToken);
}
