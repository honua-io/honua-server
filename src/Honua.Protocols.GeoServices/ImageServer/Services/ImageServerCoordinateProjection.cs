// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.Infrastructure.Services;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

/// <summary>
/// Bundles the CRS/datum/transform collaborators used by the ImageServer project operation behind
/// a single seam so request handlers compose one projection dependency instead of three separate
/// coordinate services. Delegates verbatim to the underlying spatial-reference resolver, Esri
/// datum-transformation catalog, and (optional) coordinate transform service; it adds no behavior.
/// </summary>
internal sealed class ImageServerCoordinateProjection
{
    private readonly SpatialReferenceResolver _spatialReferenceResolver;
    private readonly IDatumTransformationCatalog _datumTransformationCatalog;
    private readonly ICoordinateTransformService? _transformService;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImageServerCoordinateProjection"/> class.
    /// </summary>
    /// <param name="spatialReferenceResolver">Resolves client spatial-reference values to SRIDs.</param>
    /// <param name="datumTransformationCatalog">Esri WKID → PROJ pipeline catalog for datum selection.</param>
    /// <param name="transformService">
    /// Optional coordinate transform service. When absent, envelope/image reprojection between
    /// differing spatial references is unavailable (callers surface a 501), matching prior behavior.
    /// </param>
    public ImageServerCoordinateProjection(
        SpatialReferenceResolver spatialReferenceResolver,
        IDatumTransformationCatalog datumTransformationCatalog,
        ICoordinateTransformService? transformService = null)
    {
        _spatialReferenceResolver = spatialReferenceResolver ?? throw new ArgumentNullException(nameof(spatialReferenceResolver));
        _datumTransformationCatalog = datumTransformationCatalog ?? throw new ArgumentNullException(nameof(datumTransformationCatalog));
        _transformService = transformService;
    }

    /// <summary>
    /// Gets a value indicating whether a coordinate transform service is configured. When
    /// <see langword="false"/>, cross-SRID envelope/image reprojection is unsupported.
    /// </summary>
    public bool HasTransformService => _transformService is not null;

    /// <summary>
    /// Resolves a client-supplied spatial-reference value (WKID or JSON spatial reference) to a
    /// supported SRID, returning <see langword="null"/> when it cannot be resolved.
    /// </summary>
    public Task<int?> ResolveSridAsync(string? srValue, CancellationToken cancellationToken)
        => _spatialReferenceResolver.ResolveSridAsync(srValue, null, cancellationToken);

    /// <summary>
    /// Resolves the requested datum transformation for the given SRID pair, mirroring
    /// <see cref="GeoServicesDatumTransformationResolver.TryResolve"/>.
    /// </summary>
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

    /// <summary>
    /// Transforms a bounding-box extent using an explicitly selected datum transformation.
    /// Requires a configured transform service (guard with <see cref="HasTransformService"/>).
    /// </summary>
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

    /// <summary>
    /// Transforms a bounding-box extent between spatial references. Requires a configured transform
    /// service (guard with <see cref="HasTransformService"/>).
    /// </summary>
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
